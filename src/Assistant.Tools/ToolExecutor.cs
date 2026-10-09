using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Tools;

namespace Assistant.Tools;

/// <summary>
/// The app's <see cref="IToolExecutor"/> (PROJECT_SPEC §4.8): finds the tool a call names (only a registered tool is ever run),
/// refuses the call while the permission the tool needs is off (Settings, Permissions), reads its arguments (<see cref="ToolCallParser"/>)
/// and checks them against the tool's schema, applies the tool's risk level (a read-only tool runs; one with a side effect is planned,
/// shown to the user as exactly what it would do, and run only when the user confirms that, step 115; none that can destroy data is ever
/// run), and runs it with its time limit. Whatever goes wrong is a failed
/// result that says so to the model in words and with a code (<see cref="ToolErrors"/>), and never an exception, apart from the call
/// being cancelled. What comes back is always a result of the call that was made (its id, its tool's name) and of a size a conversation
/// can carry. A connected app's tool (step 104) is found through the <see cref="IDynamicToolSource"/> and only if it was offered to the
/// conversation for the request being answered; it then goes through every one of these steps like a built-in tool.
/// </summary>
/// <param name="tools">The registered tools. Each must meet <see cref="ToolDefinitionGuard"/> (the registry holds them to the same rules).</param>
/// <param name="permissions">Asks the user to confirm a call of a tool with a side effect; without it, none is allowed to run.</param>
/// <param name="policy">
/// Says whether a permission is on, for a tool that names one (<see cref="ToolDefinition.RequiredPermission"/>); without it, a tool that
/// needs a permission does not run, since it cannot be known to be allowed.
/// </param>
/// <param name="dynamicTools">Where the tools of connected apps come from; without it, only the registered tools can be called.</param>
public sealed class ToolExecutor : IToolExecutor
{
    /// <summary>The longest a tool's result may be, in characters; a longer one is not given to the model and is a failure that says so.</summary>
    public const int MaxResultLength = 65_536;

    private const string TimedOutText = "That took too long, so it was given up.";
    private const string DeclinedText = "The user did not allow that, so nothing was done. Do not try it again unless the user asks you to.";
    private const string NoAnswerText = "The user was asked and did not answer in time, so nothing was done. Tell the user, and try again only if they want it.";
    private const string CouldNotAskText = "The user could not be asked, so nothing was done. Tell the user.";

    private readonly Dictionary<string, ITool> _tools;
    private readonly IPermissionService? _permissions;
    private readonly IPermissionPolicy? _policy;
    private readonly IDynamicToolSource? _dynamicTools;

    /// <summary>Creates the executor of <paramref name="tools"/>.</summary>
    /// <exception cref="ArgumentException">Two tools have the same name, or one does not meet the rules for a tool.</exception>
    public ToolExecutor(
        IEnumerable<ITool> tools,
        IPermissionService? permissions = null,
        IPermissionPolicy? policy = null,
        IDynamicToolSource? dynamicTools = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _permissions = permissions;
        _policy = policy;
        _dynamicTools = dynamicTools;
        _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (ToolDefinitionGuard.Problem(tool.Definition, tool.Timeout) is { } problem)
            {
                throw new ArgumentException(problem, nameof(tools));
            }

            if (!_tools.TryAdd(tool.Definition.Name, tool))
            {
                throw new ArgumentException($"Two tools are named {tool.Definition.Name}.", nameof(tools));
            }
        }
    }

    /// <inheritdoc/>
    public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
        ExecuteAsync(call, new ToolContext(Guid.Empty), cancellationToken);

    /// <inheritdoc/>
    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // A registered tool is found by its exact name and nothing else: whatever else the model names is no tool. (The orchestrator has
        // tidied the name the model wrote, a stray space or a "functions." prefix, before a call gets here.)
        if (!_tools.TryGetValue(call.ToolName ?? string.Empty, out var tool))
        {
            // A connected app's tool is one only if it was offered to this conversation for this request, and it must meet the same rules.
            tool = _dynamicTools?.Find(context, call.ToolName ?? string.Empty);
            if (tool is null || ToolDefinitionGuard.Problem(tool.Definition, tool.Timeout) is not null)
            {
                return Failed(call, ToolErrors.UnknownTool, "That is not a tool you have. Use only the tools you were given.");
            }
        }

        // The arguments as one tidy JSON object; they are looked at only after the permission has been checked.
        var parsed = ToolCallParser.Parse(call);
        var named = parsed.IsValid ? call with { ArgumentsJson = parsed.Call.ArgumentsJson } : call;

        var definition = tool.Definition;
        if (definition.RiskLevel == RiskLevel.Destructive)
        {
            // Destructive Actions is refused by this build whatever the settings say (PermissionAvailability.AlwaysOff, the Permissions page shows it as always off): this is
            // decided by the tool's risk level, in code, before any switch is read, so no setting, file or model can make a tool that destroys data run.
            return Failed(named, ToolErrors.NotAllowed, "That tool is not allowed.");
        }

        // The permission the tool needs is checked here, in code, before its arguments are looked at. A capability set to ask every time is asked about now (the question is
        // put in the conversation), and the yes lasts for this one call: it is opened below as an approval the services the tool calls can see.
        var approved = false;
        if (definition.RequiredPermission is { } capability)
        {
            var gate = await PermissionGateAsync(tool, named, context, capability, cancellationToken).ConfigureAwait(false);
            if (gate.Refusal is { } refusal)
            {
                return refusal;
            }

            approved = gate.Approved;
        }

        // Opened here, in the method that goes on to run the tool, since an approval opened inside a method that has returned is gone.
        using var approval = approved ? PermissionApprovals.Approve(definition.RequiredPermission!.Value) : null;

        // Only now are the arguments looked at: a tool the user has not allowed never learns what it was asked.
        if (parsed.Problem is { } unreadable)
        {
            return Failed(named, unreadable.Code, unreadable.Message, ToolUsage.Describe(definition));
        }

        if (ToolSchemaValidator.Validate(definition.InputSchemaJson, parsed.Arguments, rejectUnknown: definition.RiskLevel != RiskLevel.ReadOnly)
            is { } problem)
        {
            return Failed(named, ToolErrors.InvalidArguments, problem, ToolUsage.Describe(definition));
        }

        // The one place where a call that changes something is gated, in code and not in a prompt: only a read-only tool runs without the user's yes, and every
        // other risk level, one this version does not know included, goes through the question. A connected app's tool is no exception. The one way past the
        // question is fixed in code too: one of the Assistant's own tools that does a small thing the user can switch back at once (Do not disturb, the sound, a
        // note it keeps) says so in its definition, and no connected app's tool can.
        if (definition.RiskLevel != RiskLevel.ReadOnly && !RunsWithoutAsking(definition))
        {
            return await RunConfirmedAsync(tool, named, parsed.Arguments, context, cancellationToken).ConfigureAwait(false);
        }

        return await RunLimitedAsync(tool, named, token => tool.RunAsync(named, parsed.Arguments, context, token), cancellationToken).ConfigureAwait(false);
    }

    // Whether the tool is one of the Assistant's own small switches that the user is not asked about: never a tool that could destroy something, and never a connected app's.
    internal static bool RunsWithoutAsking(ToolDefinition definition) =>
        definition.RunsWithoutAsking && definition.RiskLevel == RiskLevel.SideEffect && !ConnectedAppTools.IsConnectedAppTool(definition.Name);

    // A call that changes something (step 115): the tool works out what it would do (its target resolved, nothing changed), the user is shown exactly that and
    // asked, and the plan is run only on a yes. Whatever else happens (no one to ask, no answer, a no, a stop) nothing is done, and the model is told in words.
    private async Task<ToolResult> RunConfirmedAsync(
        ITool tool, ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var definition = tool.Definition;
        if (_permissions is null)
        {
            Observe(context, observer => observer.Answered(ConfirmationDecision.CouldNotAsk));
            return Failed(call, ToolErrors.CouldNotAsk, CouldNotAskText, status: ToolResultStatus.Declined);
        }

        ToolPlan plan;
        try
        {
            plan = await WithinAsync(tool.Timeout, cancellationToken, token => tool.PlanAsync(call, arguments, context, token)).ConfigureAwait(false);
        }
        catch (LimitExceededException)
        {
            return Failed(call, ToolErrors.TimedOut, TimedOutText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(call, ToolErrors.TimedOut, TimedOutText);
        }
        catch (Exception)
        {
            return Failed(call, ToolErrors.Failed, "The tool failed.");
        }

        // A call that cannot be done (no such file, an application name that fits two) is told to the model, and the user is not asked about it.
        if (plan.Refusal is { } refusal)
        {
            return Checked(call, definition, refusal);
        }

        // What is asked is what the tool said it would do, and when it said nothing, the call's own arguments, all of them. A call with more in it than a
        // person can be asked to read is not made: the user is never asked to approve what they cannot see.
        var confirmation = plan.Confirmation ?? ToolConfirmations.ForCall(definition, arguments);
        if (confirmation is null)
        {
            return Failed(call, ToolErrors.InvalidArguments, "That call has more in it than the user can be asked to read, so it was not made. Use shorter values.");
        }

        ConfirmationDecision decision;
        Observe(context, observer => observer.Asking());
        try
        {
            // The run's clock stands still while the user reads: their time is not the model's.
            using (context.RunPause?.Pause())
            {
                decision = await _permissions.ConfirmToolCallAsync(definition, call, context, confirmation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A question that could not be asked is not a yes.
            decision = ConfirmationDecision.CouldNotAsk;
        }

        // The activity log learns that the user was asked and what they answered, never what was asked (step 117).
        Observe(context, observer => observer.Answered(decision));

        switch (decision)
        {
            case ConfirmationDecision.Approved:
                break;
            case ConfirmationDecision.NoAnswer:
                return Failed(call, ToolErrors.NoAnswer, NoAnswerText, status: ToolResultStatus.Declined);
            case ConfirmationDecision.CouldNotAsk:
                return Failed(call, ToolErrors.CouldNotAsk, CouldNotAskText, status: ToolResultStatus.Declined);
            default:
                return Failed(call, ToolErrors.Declined, DeclinedText, status: ToolResultStatus.Declined);
        }

        // A stop that came with the yes wins: nothing is done after the user has stopped the answer.
        cancellationToken.ThrowIfCancellationRequested();
        return await RunLimitedAsync(tool, call, plan.RunAsync, cancellationToken).ConfigureAwait(false);
    }

    // Tells whoever follows the call that the user was asked, or what they answered. What follows a call is never its concern: it cannot fail a call.
    private static void Observe(ToolContext context, Action<IConfirmationObserver> tell)
    {
        if (context.Confirmations is not { } observer)
        {
            return;
        }

        try
        {
            tell(observer);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The log of what was done is not what is done.
        }
    }

    // Runs the work of the tool and gives it up when its time is out, even if it never looks at its cancellation token: it runs on a thread of its own, so
    // one that blocks (a call into Windows that does not return) cannot hold the caller past its time limit. Work that is given up on may go on until it
    // notices; its result, or its failure, is then ignored.
    private static async Task<ToolResult> RunLimitedAsync(
        ITool tool, ToolCall call, Func<CancellationToken, Task<ToolResult>> work, CancellationToken cancellationToken)
    {
        try
        {
            var result = await WithinAsync(tool.Timeout, cancellationToken, work).ConfigureAwait(false);
            return Checked(call, tool.Definition, result);
        }
        catch (LimitExceededException)
        {
            return Failed(call, ToolErrors.TimedOut, TimedOutText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(call, ToolErrors.TimedOut, TimedOutText);
        }
        catch (Exception)
        {
            return Failed(call, ToolErrors.Failed, "The tool failed.");
        }
    }

    private static async Task<T> WithinAsync<T>(TimeSpan limit, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> work)
    {
        using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeLimit.CancelAfter(limit);
        var running = Task.Run(() => work(timeLimit.Token), CancellationToken.None);

        // The clock is a task of its own that the caller's cancellation also ends, so that stopping is not held up by work that does not look at its token either.
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeUp = Task.Delay(limit, clock.Token);
        var finished = await Task.WhenAny(running, timeUp).ConfigureAwait(false);
        if (finished != running)
        {
            // The caller stopped, or the time is up. Either way the work is told, and what it does after that is nobody's concern.
            await timeLimit.CancelAsync().ConfigureAwait(false);
            _ = running.ContinueWith(
                static earlier => _ = earlier.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw new LimitExceededException();
        }

        await clock.CancelAsync().ConfigureAwait(false);
        return await running.ConfigureAwait(false);
    }

    // A result is of the call that was made, by the tool that was called, and of a size the conversation can carry.
    private static ToolResult Checked(ToolCall call, ToolDefinition definition, ToolResult? result)
    {
        if (result is null || result.OutputJson is null)
        {
            return Failed(call, ToolErrors.Failed, "The tool gave no result.");
        }

        if (result.OutputJson.Length > MaxResultLength)
        {
            return Failed(call, ToolErrors.ResultTooLarge, "The result was too large to be given to you. Ask for less.");
        }

        return result with { ToolCallId = call.Id, ToolName = definition.Name };
    }

    // Whether the permission a tool needs lets the call go ahead (PROJECT_SPEC §4.9, step 119): allowed by the switch; off or not available, which is a refusal the model passes on;
    // or set to ask every time, which asks the user, in the conversation, whether this call may use it. Only a yes goes ahead, and it is for this call.
    private async Task<(ToolResult? Refusal, bool Approved)> PermissionGateAsync(
        ITool tool, ToolCall call, ToolContext context, PermissionCapability capability, CancellationToken cancellationToken)
    {
        if (_policy is null)
        {
            return (Failed(call, ToolErrors.PermissionOff, "That tool cannot run: the permission it needs cannot be checked. Tell the user."), false);
        }

        var decision = await _policy.CheckAsync(capability, cancellationToken).ConfigureAwait(false);
        if (decision.IsAllowed)
        {
            return (null, false);
        }

        if (!decision.NeedsAsking)
        {
            return (Failed(call, ToolErrors.PermissionOff, PermissionTexts.ForModel(decision)), false);
        }

        // A call that changes something is asked about in full right after, and the question about the permission comes first: what the tool would read is not
        // what it would then do, and the first yes does not stand for the second.
        if (_permissions is null)
        {
            Observe(context, observer => observer.Answered(ConfirmationDecision.CouldNotAsk));
            return (Failed(call, ToolErrors.CouldNotAsk, PermissionTexts.ForModel(new PermissionDecision(capability, PermissionDecisionReason.CouldNotAsk)),
                status: ToolResultStatus.Declined), false);
        }

        var definition = tool.Definition;
        var title = PermissionCatalog.Get(capability).AskQuestion;
        var confirmation = new ToolConfirmation(
            ConfirmationKind.Access,
            title.Length > 0 ? title : $"Let the Assistant use {PermissionCatalog.Get(capability).Title}?",
            [
                new ConfirmationDetail("Needed for", ConfirmationText.Short(definition.Name, 60)),
                new ConfirmationDetail("Setting", "Ask every time. You can change this in Settings, under Permissions."),
            ],
            approveLabel: "Allow once");

        ConfirmationDecision answer;
        Observe(context, observer => observer.Asking());
        try
        {
            using (context.RunPause?.Pause())
            {
                answer = await _permissions.ConfirmToolCallAsync(definition, call, context, confirmation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A question that could not be asked is not a yes.
            answer = ConfirmationDecision.CouldNotAsk;
        }

        Observe(context, observer => observer.Answered(answer));
        switch (answer)
        {
            case ConfirmationDecision.Approved:
                // A stop that came with the yes wins.
                cancellationToken.ThrowIfCancellationRequested();
                return (null, true);
            case ConfirmationDecision.NoAnswer:
                return (Failed(call, ToolErrors.NoAnswer, NoAnswerText, status: ToolResultStatus.Declined), false);
            case ConfirmationDecision.CouldNotAsk:
                return (Failed(call, ToolErrors.CouldNotAsk, CouldNotAskText, status: ToolResultStatus.Declined), false);
            default:
                return (Failed(call, ToolErrors.Declined, PermissionTexts.ForModel(new PermissionDecision(capability, PermissionDecisionReason.Declined)),
                    status: ToolResultStatus.Declined), false);
        }
    }

    private static ToolResult Failed(
        ToolCall call, string code, string message, string? usage = null, ToolResultStatus status = ToolResultStatus.Failed) =>
        ToolErrors.Result(call, status, code, message, usage);

    // The time limit of a piece of work was reached; no tool throws it.
    private sealed class LimitExceededException : Exception
    {
    }
}
