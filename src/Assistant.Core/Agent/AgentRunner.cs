using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Assistant.Core.Audit;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Agent;

/// <summary>What an agent run is given to start from: the conversation, the first prompt, and what the later prompts are built from.</summary>
/// <param name="Session">The conversation the run answers in; its messages grow with every answer, call and result.</param>
/// <param name="Instructions">The caller's system instructions, or <see langword="null"/> for the default ones.</param>
/// <param name="First">The first prompt, as the orchestrator built it (with the tools <see cref="AgentRunner.PlanToolsAsync"/> chose).</param>
/// <param name="Model">The model that answers.</param>
/// <param name="Limits">The user's context limits, which every later prompt is fitted under.</param>
/// <param name="Context">The conversation and the request being answered: what tools are chosen and run for.</param>
/// <param name="PrepareImages">Makes the images of a prompt ready for the model, or <see langword="null"/> for none.</param>
public sealed record AgentTask(
    ConversationSession Session,
    string? Instructions,
    BuiltPrompt First,
    ModelInfo Model,
    ContextLimitSettings Limits,
    ToolContext Context,
    Func<BuiltPrompt, CancellationToken, Task<BuiltPrompt>>? PrepareImages = null);

/// <summary>
/// The bounded agent loop (PROJECT_SPEC §4.8, step 114): the model alternates between answering and calling tools, a round at a time, until it answers
/// in words or a bound is reached; the run then ends with a final answer, which is asked for without tools, so every run ends in words.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounds</b> (<see cref="AgentLimits"/>): at most so many rounds of tool calls, so many calls in a round and in the run, and a total time. A bound
/// is not an error: the model is asked for its answer from what it has, with <see cref="AssistantInstructions.FinalAnswerGuidance"/>, within
/// <see cref="AgentLimits.FinalAnswerTime"/>; when it says nothing, the Assistant says that it stopped, in words of its own. Time that runs out
/// while a model answer or a tool is in progress gives that up (a tool's result says it timed out) and goes to the final answer. The caller's own
/// cancellation is different: it stops everything at once and what was done stays in the conversation.
/// </para>
/// <para>
/// <b>Loops</b> (<see cref="AgentLoopGuard"/>): the same call twice is not run twice, and a run that keeps repeating itself, or whose rounds in a row
/// come to nothing, is stopped.
/// </para>
/// <para>
/// <b>Tools.</b> The tools are the registry's, built-in ones and those of the user's connected apps alike, and are run by the same executor, so every
/// rule that applies to one applies to the other. Which are offered is decided for the request, not for the catalog: the registry loads only the
/// connected apps the request seems to be about, and an <see cref="IAgentToolSelector"/> keeps what is offered within a budget, so a small model is
/// never given the whole catalog. The model may call only what it was offered in that round.
/// </para>
/// <para>
/// <b>Trace.</b> How the loop went is kept in an <see cref="AgentTrace"/> handed to an <see cref="IAgentTraceSink"/>, not in the conversation: the
/// conversation holds what was said and each call with its result, as the user sees them. The trace and the log hold counts, names and durations,
/// never a prompt, an argument, a result or an answer (PROJECT_SPEC §3.3).
/// </para>
/// </remarks>
public sealed partial class AgentRunner(
    IModelService models,
    PromptBuilder prompts,
    TimeProvider clock,
    ILogger logger,
    IToolRegistry? toolRegistry = null,
    IToolExecutor? toolExecutor = null,
    IAgentToolSelector? toolSelector = null,
    IAgentTraceSink? traces = null,
    AgentLimits? limits = null,
    IAgentTaskLog? taskLog = null,
    Assistant.Core.ModelHosting.IModelContextDemand? contextDemand = null)
{
    /// <summary>The temperature of a request that offers tools: low, so that a call to a tool is the same call every time.</summary>
    public const double ToolTemperature = 0.3;

    /// <summary>What the Assistant says when the model gave no words after the tools ran.</summary>
    public const string NoAnswerText = "I couldn't put an answer together from that. Please ask again, maybe in other words.";

    /// <summary>What the Assistant says when the time ran out and the model gave no words.</summary>
    public const string TimeLimitText =
        "I ran out of the time I'm allowed for one request, so I stopped. Ask me again, or ask for one smaller thing at a time.";

    /// <summary>What the Assistant says when the steps or calls ran out and the model gave no words.</summary>
    public const string StepLimitText =
        "I used all the steps I'm allowed for one request and couldn't put an answer together. Ask me again, maybe one smaller step at a time.";

    /// <summary>What the Assistant says when the run was going in circles and the model gave no words.</summary>
    public const string LoopText =
        "I kept getting nowhere with the tools, so I stopped. Ask me again, maybe in other words.";

    private const string RepeatedCallText = "You already made exactly this call in this answer. Use its result.";
    private const string NotOfferedText = "That tool is not available for this request. Use one of the tools you were given, or answer in words.";
    private const string OutOfTimeText = "The time allowed for this request ran out, so this call was not made.";
    private const string OverLimitText = "Only so many calls are run for one request, so this call was not made. Answer in words with what you have.";

    private readonly AgentLimits _limits = limits ?? AgentLimits.Default;
    private readonly IAgentToolSelector _selector = toolSelector ?? new BudgetedToolSelector();

    /// <summary>The bounds this runner works under.</summary>
    public AgentLimits Limits => _limits;

    /// <summary>
    /// The tools worth offering for the start of a run: the tools that are loaded by what is asked (a connected app's) are made ready first, for the
    /// request in <paramref name="context"/>, and only the ones that fit the request are loaded; then the registry's choice for the conversation is
    /// narrowed to the selector's budget. Nothing when the model cannot call tools or there is nothing to run them with. Loading is the registry's
    /// own business and what cannot be loaded is left out: nothing in here fails the run.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<ToolDefinition>> PlanToolsAsync(ModelInfo model, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        if (toolRegistry is null || toolExecutor is null || !model.SupportsToolCalling)
        {
            return [];
        }

        try
        {
            await toolRegistry.PrepareToolsAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nothing the loading of tools does can fail the run.
        }

        return _selector.Select(context, toolRegistry.ToolsFor(context), toolRegistry.FocusedFor(context));
    }

    /// <summary>
    /// The tools to offer in a later round: decided again each time, because a tool's own result may make another useful (a screenshot that was
    /// just taken can be read), and without loading anything, so it is instant.
    /// </summary>
    public IReadOnlyList<ToolDefinition> ToolsForRound(ModelInfo model, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        return toolRegistry is not null && toolExecutor is not null && model.SupportsToolCalling
            ? _selector.Select(context, toolRegistry.ToolsFor(context), toolRegistry.FocusedFor(context))
            : [];
    }

    /// <summary>
    /// Runs the loop and streams what the user sees: the model's words, each call and each result, in order. What the run does is told in
    /// <paramref name="progress"/> as it goes, and its trace goes to the sink when it ends, however it ends.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; what was done stays in the conversation.</exception>
    /// <exception cref="ModelHosting.ModelHostException">The model could not answer.</exception>
    public async IAsyncEnumerable<AssistantResponseChunk> RunAsync(
        AgentTask task, AgentProgress progress, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(progress);

        // The run is followed in the activity log (step 117), which records it from its first tool call. The user may stop it from the panel that shows it: that
        // stops the run as its caller's own cancellation does, so nothing below tells the two apart.
        using var scope = taskLog?.BeginTask(task.Session.Conversation.Id);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope?.CancelRequested ?? CancellationToken.None);
        var run = new RunState(progress, task.Session.Conversation.Id, clock.GetUtcNow(), scope);
        var enumerator = StepsAsync(task, run, stop.Token).GetAsyncEnumerator(stop.Token);
        try
        {
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (Exception exception)
                    {
                        progress.Outcome = exception is OperationCanceledException ? AgentOutcome.Stopped : AgentOutcome.Failed;
                        progress.StopReason = exception is OperationCanceledException ? AgentStopReason.Cancelled : AgentStopReason.Failed;
                        throw;
                    }

                    yield return enumerator.Current;
                }
            }

            progress.Outcome = AgentOutcome.Completed;
        }
        finally
        {
            Finish(run);
        }
    }

    private async IAsyncEnumerable<AssistantResponseChunk> StepsAsync(
        AgentTask task, RunState run, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = task.Session;
        var guard = new AgentLoopGuard(_limits);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The run's time stops while the user is asked something (step 115): a tool that needs their yes holds the clock until they have answered.
        var context = task.Context with { RunPause = new RunClock(work, _limits.TotalTime) };
        var built = task.First;
        var rounds = 0;
        var toolsRun = 0;
        AgentStopReason? finalFor = null;

        while (true)
        {
            var isFinal = finalFor is not null;
            var step = run.NewStep(built.Request.Tools, isFinal);
            using var finalTime = isFinal ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
            finalTime?.CancelAfter(_limits.FinalAnswerTime);
            var token = finalTime?.Token ?? work.Token;

            var answer = new AnswerState();
            var modelStarted = Stopwatch.GetTimestamp();
            await foreach (var chunk in StreamAnswerAsync(session, built, run.Progress, answer, token, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }

            step.ModelTime = Stopwatch.GetElapsedTime(modelStarted);
            step.AnswerCharacters = answer.Text.Length;
            step.TimedOut = answer.TimedOut;

            if (isFinal)
            {
                // The answer the run ends with is in words; when the model gave none the Assistant says that it stopped.
                if (answer.Text.ToString().AsSpan().Trim().IsEmpty)
                {
                    yield return AssistantSays(session, run, FallbackText(finalFor!.Value));
                }

                yield break;
            }

            if (answer.TimedOut)
            {
                // What the model had asked for when the time ran out was never run, so it is not in the conversation. The words it had
                // written stay there, since the user has read them, but the final answer is asked for after the conversation as it was.
                finalFor = AgentStopReason.TimeLimit;
                built = await BuildNextAsync(task, [], final: true, answer.Text.Length > 0 ? answer.Id : null, cancellationToken).ConfigureAwait(false);
                run.Progress.StopReason = finalFor.Value;
                continue;
            }

            if (answer.Calls.Count == 0 || toolExecutor is null)
            {
                // The model answered. One that said nothing after tools had run is not left without words.
                if (answer.Text.ToString().AsSpan().Trim().IsEmpty && run.Progress.ToolCalls > 0)
                {
                    yield return AssistantSays(session, run, FallbackText(AgentStopReason.Answered));
                }

                yield break;
            }

            // The model's message holds its calls, and each result follows it as a message of its own, which the next prompt carries.
            var calledAt = clock.GetUtcNow();
            session.Upsert(
                new Message(answer.Id, MessageRole.Assistant, answer.Text.ToString(), answer.Started == default ? calledAt : answer.Started)
                {
                    ToolCalls = answer.Calls,
                },
                calledAt);

            var offered = built.Request.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

            // The model is going to look for a file or read one: from here on the conversation is about files and has the larger context window
            // (PROJECT_SPEC §5.5), so that what the file tool returns, and the prompt that carries it, are fitted to the window the model is then loaded with.
            if (contextDemand is not null && answer.Calls.Any(call => offered.Contains(call.ToolName) && ModelProfiles.ContextWindowPlan.IsFileTool(call.ToolName))
                && contextDemand.Use(true)
                && await models.GetActiveModelAsync(cancellationToken).ConfigureAwait(false) is { } larger)
            {
                task = task with { Model = larger };
            }

            var position = 0;
            var madeProgress = false;
            foreach (var call in answer.Calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timer = Stopwatch.GetTimestamp();
                ToolResult result;
                AgentCallDisposition disposition;
                if (work.IsCancellationRequested)
                {
                    (result, disposition) = (ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.TimedOut, OutOfTimeText), AgentCallDisposition.OverLimit);
                }
                else if (position++ >= _limits.MaxCallsPerRound || toolsRun >= _limits.MaxToolCalls)
                {
                    // Only so many calls are run; the rest are told so and may be made in a next round if still needed.
                    (result, disposition) = (
                        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.TooManyCalls, TooManyCallsText(position > _limits.MaxCallsPerRound)),
                        AgentCallDisposition.OverLimit);
                }
                else if (answer.Unreadable.TryGetValue(call.Id, out var problem))
                {
                    (result, disposition) = (
                        ToolErrors.Result(call, ToolResultStatus.Failed, problem.Code, problem.Message, UsageOf(call)), AgentCallDisposition.Unreadable);
                }
                else if (toolRegistry?.Find(call.ToolName) is not null && !offered.Contains(call.ToolName))
                {
                    // A tool the app has, which the model was not given in this round (it was not about this request, or did not fit the budget).
                    (result, disposition) = (
                        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.UnknownTool, NotOfferedText), AgentCallDisposition.NotOffered);
                }
                else if (guard.IsRepeat(call.ToolName, call.ArgumentsJson))
                {
                    (result, disposition) = (
                        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Repeated, RepeatedCallText), AgentCallDisposition.Repeated);
                }
                else
                {
                    toolsRun++;
                    var audit = run.Scope?.BeginStep(call.ToolName, RiskOf(call.ToolName));
                    try
                    {
                        result = await RunToolAsync(call, context with { Confirmations = audit }, work.Token, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stopped while it ran: what the log says of the step is that it was stopped.
                        audit?.End(AuditStatus.Cancelled);
                        throw;
                    }

                    disposition = AgentCallDisposition.Ran;
                    madeProgress |= result.Status == ToolResultStatus.Succeeded;
                    audit?.End(StatusOf(result), ErrorCodeOf(result));
                }

                // A call that was not made is in the log too, with the reason, so that the log says what the model asked for and what became of it.
                if (disposition != AgentCallDisposition.Ran)
                {
                    run.Scope?.RecordSkipped(call.ToolName, RiskOf(call.ToolName), ErrorCodeOf(result));
                }

                run.Progress.ToolCalls++;
                step.Calls.Add(Trace(call, result, disposition, Stopwatch.GetElapsedTime(timer)));
                session.Upsert(
                    new Message(Guid.NewGuid(), MessageRole.Tool, result.OutputJson, clock.GetUtcNow()) { ToolResult = result },
                    clock.GetUtcNow());
                yield return AssistantResponseChunk.ForToolResult(result);
            }

            rounds++;
            guard.EndRound(madeProgress);

            // After a round: another, unless a bound was reached, which ends the run with the model's answer in words.
            finalFor =
                work.IsCancellationRequested ? AgentStopReason.TimeLimit
                : toolsRun >= _limits.MaxToolCalls ? AgentStopReason.ToolCallLimit
                : rounds >= _limits.MaxToolRounds ? AgentStopReason.RoundLimit
                : guard.IsLooping ? AgentStopReason.Looping
                : null;
            if (finalFor is { } reason)
            {
                run.Progress.StopReason = reason;
            }

            built = await BuildNextAsync(
                task, finalFor is null ? ToolsForRound(task.Model, task.Context) : [], finalFor is not null, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // The model's answer to one prompt, streamed. A call is only read when tools were offered: the model may not call what it was not given. It
    // is read first (ToolCallParser): the name and the arguments as the model wrote them are made tidy, and what is wrong with them is kept to be
    // told to the model. Its id is the conversation's own, since an engine may number its calls from zero in every answer. Time that runs out
    // (token, as opposed to the caller's own cancellation) ends the answer early and is told in the state.
    private async IAsyncEnumerable<AssistantResponseChunk> StreamAnswerAsync(
        ConversationSession session,
        BuiltPrompt built,
        AgentProgress progress,
        AnswerState answer,
        [EnumeratorCancellation] CancellationToken token,
        CancellationToken callerToken)
    {
        // A tool call the model writes out as words (it had no tools left to call) is not an answer: it is kept from the user.
        var markup = new ToolCallTextFilter();
        var enumerator = models.GenerateAsync(built.Request, token).GetAsyncEnumerator(token);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && token.IsCancellationRequested)
                {
                    answer.TimedOut = true;
                    break;
                }

                var chunk = enumerator.Current;
                if (chunk is { Type: AssistantResponseChunkType.TextDelta, Text.Length: > 0 })
                {
                    var shown = markup.Take(chunk.Text);
                    if (shown.Length == 0)
                    {
                        continue;
                    }

                    chunk = shown == chunk.Text ? chunk : AssistantResponseChunk.ForTextDelta(shown);
                    // The conversation holds what has been said so far, so stopping or failing here loses nothing.
                    var now = clock.GetUtcNow();
                    answer.Started = answer.Started == default ? now : answer.Started;
                    progress.TextChunks++;
                    answer.Text.Append(chunk.Text);
                    session.Upsert(new Message(answer.Id, MessageRole.Assistant, answer.Text.ToString(), answer.Started), now);
                }
                else if (chunk is { Type: AssistantResponseChunkType.ToolCall, ToolCall: { } call } && built.Request.Tools.Count > 0)
                {
                    var parsed = ToolCallParser.Parse(call);
                    call = parsed.Call with { Id = UniqueCallId(session, answer.Calls, parsed.Call.Id) };
                    answer.Calls.Add(call);
                    if (parsed.Problem is { } problem)
                    {
                        answer.Unreadable[call.Id] = problem;
                    }

                    chunk = AssistantResponseChunk.ForToolCall(call);
                }

                yield return chunk;
            }
        }

        // What was held back at the end, to see whether it began a tool call, was words.
        if (markup.Flush() is { Length: > 0 } rest)
        {
            var now = clock.GetUtcNow();
            answer.Started = answer.Started == default ? now : answer.Started;
            answer.Text.Append(rest);
            session.Upsert(new Message(answer.Id, MessageRole.Assistant, answer.Text.ToString(), answer.Started), now);
            yield return AssistantResponseChunk.ForTextDelta(rest);
        }
    }

    // Words the Assistant writes itself, for a run whose model said nothing at the end: they join the conversation like any answer.
    private AssistantResponseChunk AssistantSays(ConversationSession session, RunState run, string text)
    {
        var now = clock.GetUtcNow();
        session.Upsert(new Message(Guid.NewGuid(), MessageRole.Assistant, text, now), now);
        run.Progress.TextChunks++;
        run.AssistantWrotePart = true;
        return AssistantResponseChunk.ForTextDelta(text);
    }

    private static string FallbackText(AgentStopReason reason) => reason switch
    {
        AgentStopReason.TimeLimit => TimeLimitText,
        AgentStopReason.RoundLimit or AgentStopReason.ToolCallLimit => StepLimitText,
        AgentStopReason.Looping => LoopText,
        _ => NoAnswerText,
    };

    // The next prompt: the conversation as it is, without the message of the words that were cut off (leaveOut), when there is one, because a
    // prompt ends with the user's message or a tool's result.
    private async Task<BuiltPrompt> BuildNextAsync(
        AgentTask task, IReadOnlyList<ToolDefinition> tools, bool final, Guid? leaveOut, CancellationToken cancellationToken)
    {
        var session = task.Session;
        IReadOnlyList<Message> messages = session.Conversation.Messages;
        if (leaveOut is { } cutOff)
        {
            messages = [.. messages.Where(message => message.Id != cutOff)];
        }

        var built = Sampled(prompts.Build(task.Instructions, messages, task.Model, task.Limits, session.Conversation.Id, tools, final));
        return task.PrepareImages is null ? built : await task.PrepareImages(built, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A request that offers tools asks for a low temperature.</summary>
    internal static BuiltPrompt Sampled(BuiltPrompt built) =>
        built.Request.Tools.Count > 0 ? built with { Request = built.Request with { Temperature = ToolTemperature } } : built;

    private string TooManyCallsText(bool roundFull) =>
        roundFull
            ? $"Only the first {_limits.MaxCallsPerRound} calls of one answer are run. Make the rest in your next step, if they are still needed."
            : OverLimitText;

    // How the tool a call names is called, for a call whose arguments were not right; nothing when the call names no tool the app has.
    private string? UsageOf(ToolCall call) =>
        toolRegistry?.Find(call.ToolName) is { } definition ? ToolUsage.Describe(definition) : null;

    // A tool call that did not work is a result for the model to read, not a failure of the answer; stopping is the only exception, and time that
    // ran out is a result too: it says the call was given up.
    private async Task<ToolResult> RunToolAsync(ToolCall call, ToolContext context, CancellationToken work, CancellationToken callerToken)
    {
        try
        {
            return await toolExecutor!.ExecuteAsync(call, context, work).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && work.IsCancellationRequested)
        {
            return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.TimedOut, OutOfTimeText);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, "The tool failed.");
        }
    }

    // The id the engine gave the call, unless the conversation already has a call by that id (an engine may number from zero every time).
    private static string UniqueCallId(ConversationSession session, List<ToolCall> thisRound, string? given)
    {
        var id = string.IsNullOrWhiteSpace(given) ? "call_" + thisRound.Count : given;
        var taken = session.Conversation.Messages.SelectMany(message => message.ToolCalls).Select(call => call.Id)
            .Concat(thisRound.Select(call => call.Id)).ToHashSet(StringComparer.Ordinal);
        while (taken.Contains(id))
        {
            id = "call_" + Guid.NewGuid().ToString("N")[..8];
        }

        return id;
    }

    // How much the tool can change, for the activity log; not known for a name the app has no tool of.
    private RiskLevel? RiskOf(string? toolName) => toolName is null ? null : toolRegistry?.Find(toolName)?.RiskLevel;

    // How a result is told in the activity log.
    private static AuditStatus StatusOf(ToolResult result) => result.Status switch
    {
        ToolResultStatus.Succeeded => AuditStatus.Succeeded,
        ToolResultStatus.Declined => AuditStatus.Declined,
        ToolResultStatus.Cancelled => AuditStatus.Cancelled,
        _ => ErrorCodeOf(result) == ToolErrors.TimedOut ? AuditStatus.TimedOut : AuditStatus.Failed,
    };

    // The code of a result that did not work, or nothing for one that did.
    private static string? ErrorCodeOf(ToolResult result) =>
        result.Status != ToolResultStatus.Succeeded && ToolErrors.TryRead(result.OutputJson, out var code, out _) ? code : null;

    private static AgentCallTrace Trace(ToolCall call, ToolResult result, AgentCallDisposition disposition, TimeSpan duration)
    {
        string? code = null;
        if (result.Status != ToolResultStatus.Succeeded && ToolErrors.TryRead(result.OutputJson, out var read, out _))
        {
            code = read;
        }

        return new AgentCallTrace(
            call.Id,
            call.ToolName ?? string.Empty,
            disposition,
            result.Status,
            code,
            disposition == AgentCallDisposition.Ran ? duration : TimeSpan.Zero,
            result.OutputJson?.Length ?? 0,
            AgentLoopGuard.Fingerprint(call.ToolName ?? string.Empty, call.ArgumentsJson ?? string.Empty));
    }

    // The run is over, however it ended: its trace goes to the sink, and the one line that says how it went is logged. Nothing here can fail the run.
    private void Finish(RunState run)
    {
        var progress = run.Progress;
        try
        {
            // However the run ended, the activity log is told, and what was still going on in it ends with it.
            run.Scope?.End(progress.Outcome, progress.StopReason);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A log that fails is its own business.
        }

        var trace = new AgentTrace(
            Guid.NewGuid(),
            run.ConversationId,
            run.StartedAt,
            Stopwatch.GetElapsedTime(run.Started),
            progress.Outcome,
            progress.StopReason,
            [.. run.Steps.Select(step => step.ToTrace())],
            run.AssistantWrotePart);
        LogRunEnded(logger, trace.Outcome, trace.StopReason, trace.Steps.Count, trace.ToolsRun, (long)trace.Elapsed.TotalMilliseconds);
        if (traces is null)
        {
            return;
        }

        try
        {
            traces.Record(trace);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A sink that fails is its own business.
        }
    }

    [LoggerMessage(
        EventId = 2800,
        Level = LogLevel.Information,
        Message = "Agent run ended: {Outcome}, stop reason {StopReason}, {Rounds} rounds, {ToolsRun} tools run, in {ElapsedMs} ms")]
    private static partial void LogRunEnded(
        ILogger logger, AgentOutcome outcome, AgentStopReason stopReason, int rounds, int toolsRun, long elapsedMs);

    // What the model said in one answer, and the calls it made.
    private sealed class AnswerState
    {
        public Guid Id { get; } = Guid.NewGuid();

        public DateTimeOffset Started { get; set; }

        public StringBuilder Text { get; } = new();

        public List<ToolCall> Calls { get; } = [];

        public Dictionary<string, ToolCallProblem> Unreadable { get; } = new(StringComparer.Ordinal);

        public bool TimedOut { get; set; }
    }

    // The run so far, from which the trace is made when it ends.
    private sealed class RunState(AgentProgress progress, Guid conversationId, DateTimeOffset startedAt, IAgentTaskScope? scope)
    {
        public IAgentTaskScope? Scope { get; } = scope;

        public AgentProgress Progress { get; } = progress;

        public Guid ConversationId { get; } = conversationId;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public long Started { get; } = Stopwatch.GetTimestamp();

        public bool AssistantWrotePart { get; set; }

        public List<StepBuilder> Steps { get; } = [];

        public StepBuilder NewStep(IReadOnlyList<ToolDefinition> offered, bool isFinal)
        {
            var step = new StepBuilder(Steps.Count + 1, [.. offered.Select(tool => tool.Name)], isFinal);
            Steps.Add(step);
            return step;
        }
    }

    private sealed class StepBuilder(int number, IReadOnlyList<string> offered, bool isFinal)
    {
        public int AnswerCharacters { get; set; }

        public TimeSpan ModelTime { get; set; }

        public bool TimedOut { get; set; }

        public List<AgentCallTrace> Calls { get; } = [];

        public AgentStepTrace ToTrace() => new(number, offered, AnswerCharacters, ModelTime, [.. Calls], isFinal, TimedOut);
    }
}
