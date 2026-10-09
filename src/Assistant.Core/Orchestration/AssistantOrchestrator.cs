using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Assistant.Core.Agent;
using Assistant.Core.Audit;
using Assistant.Core.Budgeting;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Orchestration;

/// <summary>
/// The app's <see cref="IAssistantOrchestrator"/> (PROJECT_SPEC §5.5): a turn is a prompt built by the
/// <see cref="PromptBuilder"/>, its images made ready for the model by the <see cref="IImagePreprocessor"/>, the model's
/// streamed answer, and the two messages recorded in the <see cref="ConversationSession"/>.
/// </summary>
/// <remarks>
/// It logs events only: counts, sizes, an outcome and a duration, never a prompt, context, image or answer
/// (PROJECT_SPEC §3.3). Each turn's prompt is fitted into the model's context window under the user's context limits,
/// which are read from the settings as the turn starts. The images a prompt carries are prepared as copies, so the
/// user's context keeps the originals; one that cannot be read is left out with a notice. Retrieval, file text
/// extraction and the tool loop are later steps of the pipeline. A request for something of an external app that cannot be
/// done now is answered by the Assistant itself, through <see cref="IConnectedAppRequestHandler"/>, before the model is asked
/// (PROJECT_SPEC §4.8, steps 105-106); when that answer offers an integration, the request is set aside and is gone back to
/// (<see cref="ContinueAsync"/>) when the user has decided, so they never say it again (step 110).
/// </remarks>
public sealed partial class AssistantOrchestrator(
    IModelService models,
    ISettingsService settings,
    PromptBuilder prompts,
    IImagePreprocessor imagePreprocessor,
    TimeProvider clock,
    ILogger<AssistantOrchestrator> logger,
    IContextService? contexts = null,
    IToolRegistry? toolRegistry = null,
    IToolExecutor? toolExecutor = null,
    IScreenText? screenText = null,
    IConnectedAppRequestHandler? connectedApps = null,
    IAgentToolSelector? toolSelector = null,
    IAgentTraceSink? traceSink = null,
    AgentLimits? agentLimits = null,
    IAgentTaskLog? taskLog = null,
    Assistant.Core.ModelHosting.IModelContextDemand? contextDemand = null,
    IPermissionService? questions = null) : IAssistantOrchestrator
{
    /// <summary>What the user is asked when a chat has outgrown the ordinary context window and a larger one can be had.</summary>
    public const string RaiseContextQuestion = "Would you like to temporarily increase the context limit for this chat to continue?";

    // The question about the context window is put to the user the way a tool's question is, in the conversation: this is what it is asked as. It is
    // not a tool, is never offered to the model and can never be "always allowed".
    private static readonly ToolDefinition RaiseContextAsk = ToolDefinition.Create(
        "raise_context_limit", "Asks the user whether this chat may use the larger context window.", [], RiskLevel.SideEffect);

    private const int MaxRememberedChats = 256;

    // The chats the user said yes for (they are answered with the larger window from then on, while the app runs), and the ones they were asked about
    // at all: a chat is asked about once.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _contextAsked = new();

    /// <summary>Tells the user that an image could not be decoded and was not sent.</summary>
    public const string UnreadableImageNotice = "An attached image couldn't be read, so it was left out.";

    /// <summary>What the Assistant says when a request that was set aside cannot be found any more, so there is nothing to go back to.</summary>
    public const string RequestForgottenText =
        "I can't find the request I set aside for that integration any more, so I haven't carried it out. Ask me again and I will pick it up.";

    /// <summary>The most rounds of tool calls one answer may take (PROJECT_SPEC §4.8); after them the model must answer in words.</summary>
    public const int MaxToolRounds = AgentLimits.DefaultMaxToolRounds;

    /// <summary>
    /// The most calls of one answer that are run; the rest get a result that says so. A model that asks for twenty things at once has
    /// lost the thread, and the calls run one after another.
    /// </summary>
    public const int MaxCallsPerRound = AgentLimits.DefaultMaxCallsPerRound;

    /// <summary>The temperature of a request that offers tools: low, so that a call to a tool is the same call every time.</summary>
    public const double ToolTemperature = AgentRunner.ToolTemperature;

    // The loop of model answers and tool calls (step 114), made of what the orchestrator itself is given.
    private readonly AgentRunner _agent = new(models, prompts, clock, logger, toolRegistry, toolExecutor, toolSelector, traceSink, agentLimits, taskLog, contextDemand);

    /// <inheritdoc/>
    public IAsyncEnumerable<AssistantResponseChunk> AskAsync(
        ConversationSession session,
        string prompt,
        IReadOnlyList<ContextItem>? contextItems = null,
        string? instructions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return RunAsync(session, prompt, contextItems?.ToArray() ?? [], instructions, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<AssistantResponseChunk> ContinueAsync(
        ConversationSession session,
        PendingRequest pending,
        PendingOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pending);
        return ContinueRunAsync(session, pending, outcome, cancellationToken);
    }

    private async IAsyncEnumerable<AssistantResponseChunk> RunAsync(
        ConversationSession session,
        string prompt,
        ContextItem[] contextItems,
        string? instructions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!session.TryBeginTurn())
        {
            throw new InvalidOperationException("The conversation is still answering an earlier message.");
        }

        var start = Stopwatch.GetTimestamp();
        var turn = new AgentProgress();
        try
        {
            Prepared prepared;
            try
            {
                prepared = await PrepareAsync(session, prompt, contextItems, instructions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                turn.Outcome = OutcomeOf(exception);
                throw;
            }

            await foreach (var chunk in AnswerAsync(session, instructions, prepared, turn, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            EndTurn(session, turn, start);
        }
    }

    // Goes back to the request that was set aside for an integration (step 110): the user's message is already in the conversation, so none is added.
    private async IAsyncEnumerable<AssistantResponseChunk> ContinueRunAsync(
        ConversationSession session,
        PendingRequest pending,
        PendingOutcome outcome,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!session.TryBeginTurn())
        {
            throw new InvalidOperationException("The conversation is still answering an earlier message.");
        }

        var start = Stopwatch.GetTimestamp();
        var turn = new AgentProgress();
        try
        {
            LogContinued(logger, outcome);
            if (outcome == PendingOutcome.NotInstalled)
            {
                // What the user decided is what the model remembers: the offer's words give way to the Assistant's that nothing was done.
                var saidAt = clock.GetUtcNow();
                var note = new Message(Guid.NewGuid(), MessageRole.Assistant, pending.NotInstalledText, saidAt);
                session.Edit(messages => ReplacingReply(messages, pending.ReplyMessageId, note), saidAt);
                turn.TextChunks++;
                turn.Outcome = AgentOutcome.Completed;
                yield return AssistantResponseChunk.ForTextDelta(pending.NotInstalledText);
                yield break;
            }

            Prepared? prepared;
            try
            {
                prepared = await PrepareResumeAsync(session, pending, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                turn.Outcome = OutcomeOf(exception);
                throw;
            }

            if (prepared is null)
            {
                turn.TextChunks++;
                turn.Outcome = AgentOutcome.Completed;
                yield return AssistantResponseChunk.ForTextDelta(RequestForgottenText);
                yield break;
            }

            await foreach (var chunk in AnswerAsync(session, null, prepared, turn, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            EndTurn(session, turn, start);
        }
    }

    // What a turn does once its prompt is ready: the warnings and notices, the Assistant's own answer to a request for an external app that cannot be
    // served, or the model's answer with its rounds of tool calls.
    private async IAsyncEnumerable<AssistantResponseChunk> AnswerAsync(
        ConversationSession session,
        string? instructions,
        Prepared prepared,
        AgentProgress turn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = prepared.Prompt;
        var built = prepared.Built;
        foreach (var warning in built.ContextWarnings)
        {
            yield return AssistantResponseChunk.ForContextWarning(warning);
        }

        foreach (var notice in built.Notices)
        {
            yield return AssistantResponseChunk.ForNotice(notice);
        }

        // A request for something of an external app that cannot be done now (no integration is installed, or the one that is cannot
        // be used) is answered by the Assistant itself, in words it wrote from what it checked: a small model that is not given the
        // app's tools tends to say it did the thing (PROJECT_SPEC section 4.8). Nothing in here can fail the turn.
        if (await ConnectedAppReplyAsync(new ToolContext(session.Conversation.Id, prompt), cancellationToken).ConfigureAwait(false) is { } appReply)
        {
            var repliedAt = clock.GetUtcNow();
            var replyId = Guid.NewGuid();
            session.Upsert(new Message(replyId, MessageRole.Assistant, appReply.Text, repliedAt), repliedAt);
            turn.TextChunks++;
            turn.Outcome = AgentOutcome.Completed;
            LogConnectedAppReply(logger, appReply.Kind);
            yield return AssistantResponseChunk.ForTextDelta(appReply.Text);
            if (appReply.Offer is { } offer)
            {
                // The panel that asks the user: nothing is downloaded or run until they click Install on it. The request is set aside, and
                // what the user decides goes back to it (step 110).
                var pending = appReply.NotInstalledText is { Length: > 0 } notInstalled
                    ? new PendingRequest(prepared.RequestMessageId, replyId, notInstalled)
                    : null;
                yield return AssistantResponseChunk.ForIntegrationOffer(offer, pending);
            }

            yield break;
        }

        // The model answers, and when it calls tools instead (or as well) they are run, their results join the conversation and it answers
        // again, within the bounds of the agent loop (step 114): rounds, calls, time, loops; a run always ends with the model's words.
        var task = new AgentTask(
            session, instructions, built, prepared.Model, prepared.Limits, new ToolContext(session.Conversation.Id, prompt) { HasPicture = prepared.HasPicture },
            PrepareImagesAsync);
        await foreach (var chunk in _agent.RunAsync(task, turn, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private void EndTurn(ConversationSession session, AgentProgress turn, long start)
    {
        session.EndTurn();
        LogTurnEnded(logger, turn.Outcome, turn.TextChunks, session.Conversation.Messages.Count, ElapsedMs(start));
        if (turn.ToolCalls > 0)
        {
            LogToolRounds(logger, turn.ToolCalls);
        }
    }

    // The messages with the one that holds an offer replaced by what the Assistant says instead, where it stood, or added when it is not there.
    private static IReadOnlyList<Message> ReplacingReply(IReadOnlyList<Message> messages, Guid replyId, Message instead)
    {
        var changed = new List<Message>(messages.Count + 1);
        var replaced = false;
        foreach (var message in messages)
        {
            if (message.Id == replyId)
            {
                changed.Add(instead);
                replaced = true;
            }
            else
            {
                changed.Add(message);
            }
        }

        if (!replaced)
        {
            changed.Add(instead);
        }

        return changed;
    }

    // The Assistant's own answer to a request for an external app that cannot be served now; null when the model is to answer.
    private async Task<ConnectedAppReply?> ConnectedAppReplyAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (connectedApps is null)
        {
            return null;
        }

        try
        {
            return await connectedApps.TryAnswerAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A request that could not be checked is a request for the model.
            return null;
        }
    }

    // Everything before the model is asked: which model, the prompt, and the user's message in the conversation. A turn
    // that cannot start leaves the conversation as it was.
    private async Task<Prepared> PrepareAsync(
        ConversationSession session,
        string prompt,
        ContextItem[] contextItems,
        string? instructions,
        CancellationToken cancellationToken)
    {
        // A conversation that carries files is answered with the larger context window, and one that does not with the ordinary one: said before
        // the model is asked for, so that the prompt is fitted to the window the model will have for it (PROJECT_SPEC §5.5).
        var conversationId = session.Conversation.Id;
        var larger = Assistant.Core.ModelProfiles.ContextWindowPlan.CarriesDocuments(session.Conversation.Messages, contextItems) || HasRaisedContext(conversationId);
        contextDemand?.Use(larger);
        var model = await models.GetActiveModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ModelNotSetUpException();
        var limits = Raised(conversationId, (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ContextLimits);

        // A model that cannot see images is given the text recognized in a screenshot in its place (PROJECT_SPEC §4.6): the OCR runs
        // only for it, and only when there is a screenshot to read; a model that sees is given the picture alone.
        if (!model.SupportsVision && screenText is not null)
        {
            contextItems = await RecognizeScreenshotsAsync(contextItems, cancellationToken).ConfigureAwait(false);
        }

        // The tools are offered to a model that can call them, when there is something to run them with. Those that are loaded by what is
        // asked (a connected app's, PROJECT_SPEC section 4.8) are made ready first, for this request.
        var hasPicture = HasPicture(contextItems);
        var tools = await _agent.PlanToolsAsync(model, new ToolContext(conversationId, prompt) { HasPicture = hasPicture }, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var message = new Message(Guid.NewGuid(), MessageRole.User, prompt, now) { ContextItems = contextItems };
        var built = AgentRunner.Sampled(prompts.Build(instructions, [.. session.Conversation.Messages, message], model, limits, conversationId, tools));

        // The chat no longer fits the window it has, and a larger one can be had: the user is asked whether to use it for this chat. On a yes the
        // prompt is fitted again, to the larger window; otherwise it goes as it was fitted, with what was left out said as always.
        if (!larger && built.Budget is { AnythingTrimmed: true } budgetNow
            && await RaiseContextAsync(conversationId, budgetNow, limits, cancellationToken).ConfigureAwait(false) is { } roomier)
        {
            (model, limits) = roomier;
            built = AgentRunner.Sampled(prompts.Build(instructions, [.. session.Conversation.Messages, message], model, limits, conversationId, tools));
        }

        built = await PrepareImagesAsync(built, cancellationToken).ConfigureAwait(false);

        session.Upsert(message, now);

        // The conversation carries the context from here on, so the context service stops holding it as waiting.
        contexts?.Commit(conversationId, contextItems);
        LogTurnStarted(logger, built.Request.Messages.Count, contextItems.Length, built.Request.Images.Count);
        if (built.Budget is { } budget)
        {
            LogPromptFitted(logger, budget);
        }

        return new Prepared(built, model, limits, tools, message.Id, prompt) { HasPicture = hasPicture };
    }

    private static bool HasPicture(IEnumerable<ContextItem> items) =>
        items.Any(item => item.Type is ContextItemType.Image or ContextItemType.Screenshot);

    private bool HasRaisedContext(Guid conversationId) => _contextAsked.TryGetValue(conversationId, out var raised) && raised;

    // A chat the user gave more room is held to the larger limit in everything it asks, and not only to the model's larger window.
    private ContextLimitSettings Raised(Guid conversationId, ContextLimitSettings limits) =>
        HasRaisedContext(conversationId) ? RaisedLimits(limits) : limits;

    // The ordinary limit lifted to the one for files ("no limit" stays no limit, and a files limit of "no limit" lifts it to none).
    private static ContextLimitSettings RaisedLimits(ContextLimitSettings limits) => limits with
    {
        NormalContextTokens = limits.NormalContextTokens <= 0 || limits.HeavyContextTokens <= 0 ? 0 : Math.Max(limits.NormalContextTokens, limits.HeavyContextTokens),
    };

    // Asks the user, once for a chat, whether it may have more room: the model loaded with the larger window, and the chat held to the larger limit. Gives the
    // model and the limits the turn then has when they say yes. Null when there is nobody to ask, more room would be no more room, the chat was asked about
    // before, or the answer is anything but a yes.
    private async Task<(ModelInfo Model, ContextLimitSettings Limits)?> RaiseContextAsync(
        Guid conversationId, ContextBudgetReport now, ContextLimitSettings limits, CancellationToken cancellationToken)
    {
        if (contextDemand is null || questions is null || conversationId == Guid.Empty || _contextAsked.ContainsKey(conversationId))
        {
            return null;
        }

        // What the model would be loaded with for a chat that carries files. Nothing is loaded to find out.
        contextDemand.Use(true);
        ModelInfo? roomier;
        try
        {
            roomier = await models.GetActiveModelAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            contextDemand.Use(false);
        }

        var raised = RaisedLimits(limits);
        var then = roomier is null ? default : ContextBudget.Resolve(roomier, raised, now.Mode);
        if (roomier is null || then.WindowTokens <= now.Budget.WindowTokens)
        {
            return null;
        }

        if (_contextAsked.Count >= MaxRememberedChats)
        {
            _contextAsked.Clear();
        }

        _contextAsked[conversationId] = false;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var question = new ToolConfirmation(
            ConfirmationKind.Other,
            RaiseContextQuestion,
            [
                new ConfirmationDetail("Now", string.Create(culture, $"{now.Budget.WindowTokens:N0} tokens: the oldest parts of this chat no longer fit")),
                new ConfirmationDetail("If you say yes", string.Create(culture, $"{then.WindowTokens:N0} tokens, for this chat only, until the Assistant is restarted")),
            ],
            "Yes",
            "The model is loaded again with more room, which takes a moment and more memory. Cancel keeps the limit: the answer goes on without the oldest parts.")
        {
            DeclineLabel = "Cancel",
        };
        ConfirmationDecision decision;
        try
        {
            decision = await questions.ConfirmToolCallAsync(
                RaiseContextAsk, new ToolCall(Guid.NewGuid().ToString("N"), RaiseContextAsk.Name, "{}"), new ToolContext(conversationId), question, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // A question that could not be asked is a no: the chat goes on as it would have.
            _contextAsked.TryRemove(conversationId, out _);
            return null;
        }

        if (decision != ConfirmationDecision.Approved)
        {
            // A question nobody saw was not answered: the chat may be asked about again.
            if (decision == ConfirmationDecision.CouldNotAsk)
            {
                _contextAsked.TryRemove(conversationId, out _);
            }

            return null;
        }

        _contextAsked[conversationId] = true;
        contextDemand.Use(true);
        LogContextRaised(logger, now.Budget.WindowTokens, then.WindowTokens);
        return (await models.GetActiveModelAsync(cancellationToken).ConfigureAwait(false) ?? roomier, raised);
    }

    // As PrepareAsync, for a request that was set aside (step 110): the request is the user's message that is already in the conversation, so it is
    // not added; the offer's words are taken out of what the model remembers, and the tools of the integration that is installed now are loaded for the
    // request. Null when the conversation no longer holds the request. A turn that cannot start (no model) leaves the conversation as it was.
    private async Task<Prepared?> PrepareResumeAsync(ConversationSession session, PendingRequest pending, CancellationToken cancellationToken)
    {
        contextDemand?.Use(Assistant.Core.ModelProfiles.ContextWindowPlan.CarriesDocuments(session.Conversation.Messages) || HasRaisedContext(session.Conversation.Id));
        var model = await models.GetActiveModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ModelNotSetUpException();
        var limits = Raised(session.Conversation.Id, (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ContextLimits);
        var asked = session.Conversation.Messages.FirstOrDefault(message => message.Id == pending.RequestMessageId && message.Role == MessageRole.User);
        if (asked is null)
        {
            return null;
        }

        var conversationId = session.Conversation.Id;
        var hasPicture = HasPicture(asked.ContextItems);
        var tools = await _agent.PlanToolsAsync(model, new ToolContext(conversationId, asked.Text) { HasPicture = hasPicture }, cancellationToken).ConfigureAwait(false);

        // The offer is out of the way. When the conversation has gone on since the request was made (the user asked something else while the
        // integration installed), the request is asked again at the end, as it was asked, so that the model answers it last.
        var now = clock.GetUtcNow();
        var requestId = asked.Id;
        session.Edit(
            messages =>
            {
                var kept = messages.Where(message => message.Id != pending.ReplyMessageId).ToList();
                if (kept.Count == 0 || kept[^1].Id != asked.Id)
                {
                    requestId = Guid.NewGuid();
                    kept.Add(asked with { Id = requestId, CreatedAt = now });
                }

                return kept;
            },
            now);

        var built = AgentRunner.Sampled(prompts.Build(null, session.Conversation.Messages, model, limits, conversationId, tools));
        built = await PrepareImagesAsync(built, cancellationToken).ConfigureAwait(false);
        LogTurnStarted(logger, built.Request.Messages.Count, asked.ContextItems.Count, built.Request.Images.Count);
        if (built.Budget is { } budget)
        {
            LogPromptFitted(logger, budget);
        }

        return new Prepared(built, model, limits, tools, requestId, asked.Text) { HasPicture = hasPicture };
    }

    // The screenshots among the items that have pixels and no text, each with the text OCR recognized in it. What cannot be read is left
    // as it is: the prompt says so for a model that cannot see.
    private async Task<ContextItem[]> RecognizeScreenshotsAsync(ContextItem[] items, CancellationToken cancellationToken)
    {
        var result = new ContextItem[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (item is { Type: ContextItemType.Screenshot } && !item.ImageData.IsEmpty && string.IsNullOrWhiteSpace(item.Text)
                && await screenText!.ReadAsync(item, cancellationToken).ConfigureAwait(false) is { IsEmpty: false } recognized)
            {
                item = item with { Text = recognized.Text };
            }

            result[index] = item;
        }

        return result;
    }

    // What a turn is asked with: its first prompt, and what the next rounds are built from; the user's message that is answered, and its text.
    private sealed record Prepared(
        BuiltPrompt Built, ModelInfo Model, ContextLimitSettings Limits, IReadOnlyList<ToolDefinition> Tools, Guid RequestMessageId, string Prompt)
    {
        // Whether the user's message came with a picture, which the tools of every round of the turn are chosen by.
        public bool HasPicture { get; init; }
    }

    // The images the prompt carries, each made ready for the model: the originals stay as they are in the user's context.
    // An image that cannot be decoded is left out and the user is told, rather than failing the whole turn.
    private async Task<BuiltPrompt> PrepareImagesAsync(BuiltPrompt built, CancellationToken cancellationToken)
    {
        var images = built.Request.Images;
        if (images.Count == 0)
        {
            return built;
        }

        var start = Stopwatch.GetTimestamp();
        var prepared = new List<ReadOnlyMemory<byte>>(images.Count);
        var contents = new List<ImageContent>(images.Count);
        long givenBytes = 0;
        long sentBytes = 0;
        var resized = 0;
        var unreadable = 0;
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var content = index < built.ImageContents.Count ? built.ImageContents[index] : ImageContent.Picture;
            givenBytes += image.Length;
            try
            {
                var ready = await imagePreprocessor.PrepareAsync(image, content, cancellationToken).ConfigureAwait(false);
                prepared.Add(ready.Data);
                contents.Add(content);
                sentBytes += ready.Data.Length;
                resized += ready.IsResized ? 1 : 0;
            }
            catch (ImagePreprocessingException)
            {
                unreadable++;
            }
        }

        LogImagesPrepared(logger, prepared.Count, resized, unreadable, givenBytes, sentBytes, ElapsedMs(start));
        return built with
        {
            ImageContents = contents,
            Request = built.Request with { Images = prepared },
            Notices = unreadable > 0 ? [.. built.Notices, UnreadableImageNotice] : built.Notices,
        };
    }

    // Stopping, whoever asked for it, is not a failure.
    private static AgentOutcome OutcomeOf(Exception exception) =>
        exception is OperationCanceledException ? AgentOutcome.Stopped : AgentOutcome.Failed;

    private static long ElapsedMs(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Debug,
        Message = "Chat turn started: {PromptMessages} messages in the prompt, {ContextItems} context items, {Images} images")]
    private static partial void LogTurnStarted(ILogger logger, int promptMessages, int contextItems, int images);

    // Counts only: what was sent and what was left out, never any of it.
    private static void LogPromptFitted(ILogger logger, ContextBudgetReport budget)
    {
        if (budget.Fits)
        {
            LogPromptFitted(
                logger,
                budget.Mode,
                budget.EstimatedPromptTokens,
                budget.Budget.PromptTokens,
                budget.TurnsLeftOut,
                budget.ContextItemsShortened,
                budget.ContextItemsLeftOut,
                budget.QuestionShortened);
        }
        else
        {
            LogPromptOverBudget(logger, budget.Mode, budget.EstimatedPromptTokens, budget.Budget.PromptTokens);
        }
    }

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Debug,
        Message = "Prompt fitted ({Mode}): about {EstimatedTokens} of {BudgetTokens} tokens, {TurnsLeftOut} earlier turns left out, " +
            "{ItemsShortened} context items shortened, {ItemsLeftOut} left out, message shortened: {QuestionShortened}")]
    private static partial void LogPromptFitted(
        ILogger logger,
        ContextBudgetMode mode,
        int estimatedTokens,
        int budgetTokens,
        int turnsLeftOut,
        int itemsShortened,
        int itemsLeftOut,
        bool questionShortened);

    [LoggerMessage(
        EventId = 2303,
        Level = LogLevel.Warning,
        Message = "Prompt over budget ({Mode}): about {EstimatedTokens} tokens against {BudgetTokens}, the engine may refuse it")]
    private static partial void LogPromptOverBudget(ILogger logger, ContextBudgetMode mode, int estimatedTokens, int budgetTokens);

    [LoggerMessage(
        EventId = 2304,
        Level = LogLevel.Debug,
        Message = "Images prepared: {Images} sent ({Resized} resized, {Unreadable} unreadable left out), " +
            "{GivenBytes} bytes given, {SentBytes} bytes sent, in {ElapsedMs} ms")]
    private static partial void LogImagesPrepared(
        ILogger logger, int images, int resized, int unreadable, long givenBytes, long sentBytes, long elapsedMs);

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Information,
        Message = "Chat turn ended: {Outcome} after {TextChunks} text chunks in {ElapsedMs} ms, {Messages} messages in the conversation")]
    private static partial void LogTurnEnded(
        ILogger logger, AgentOutcome outcome, int textChunks, int messages, long elapsedMs);

    [LoggerMessage(EventId = 2307, Level = LogLevel.Information, Message = "Chat turn ran {ToolCalls} tool calls")]
    private static partial void LogToolRounds(ILogger logger, int toolCalls);

    [LoggerMessage(EventId = 2308, Level = LogLevel.Information, Message = "Chat turn answered by the Assistant for an external app: {Kind}")]
    private static partial void LogConnectedAppReply(ILogger logger, ConnectedAppReplyKind kind);

    [LoggerMessage(EventId = 2312, Level = LogLevel.Information, Message = "Chat turn goes back to a request set aside for an integration: {Outcome}")]
    private static partial void LogContinued(ILogger logger, PendingOutcome outcome);

    [LoggerMessage(EventId = 2313, Level = LogLevel.Information, Message = "The user raised the context window for a chat from {From} to {To} tokens")]
    private static partial void LogContextRaised(ILogger logger, int from, int to);
}
