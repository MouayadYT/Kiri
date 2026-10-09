using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Tools;

/// <summary>
/// One tool the model may call (PROJECT_SPEC §4.8): what it is called and does, the schema its arguments must follow, and what it
/// runs. The executor has already checked the arguments against the schema and applied the risk level when it runs.
/// </summary>
public interface ITool
{
    /// <summary>
    /// The tool's name, description, input schema and risk level, as the model is told them, and the permission it needs and the time
    /// it may take, which the registry and the executor apply.
    /// </summary>
    ToolDefinition Definition { get; }

    /// <summary>How long a call may run before it is given up as a failure: the time the definition gives, or the default.</summary>
    TimeSpan Timeout => Definition.EffectiveTimeout;

    /// <summary>
    /// Whether the tool is worth offering to the model in the conversation <paramref name="context"/> names. A tool may still be called
    /// when it is not offered only if the model was given it; this decides what is offered. Always, unless an override says otherwise.
    /// </summary>
    bool IsOffered(ToolContext context) => true;

    /// <summary>
    /// Whether the user's message is their answer to a question this tool had the model ask (which of two devices they meant). The request is then this
    /// tool's: the registry offers nothing beside the tools that claim it, so that a few words that name a device are not taken for something to look
    /// up in files or on the web. Never, unless an override says otherwise.
    /// </summary>
    bool Claims(ToolContext context) => false;

    /// <summary>
    /// Whether the tool is offered for this request because the request is about it (its topic was named in the conversation), and not because it is
    /// always there. When the tools have to be cut down to what a small model can take in, these are kept first. Never, unless an override says otherwise.
    /// </summary>
    bool IsFocused(ToolContext context) => false;

    /// <summary>
    /// Works out, before the model is offered tools for a turn, what <see cref="IsOffered"/> will say for it (step 116): a question that needs the settings, the permissions or
    /// the connected apps to be asked cannot be answered inside <see cref="IsOffered"/>, which is not asynchronous. The registry calls it once for a turn, before it lists the tools
    /// (<see cref="Assistant.Core.Contracts.IToolRegistry.PrepareToolsAsync"/>); a tool that has nothing to find out does nothing, which is what the default does.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task PrepareAsync(ToolContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs the call. <paramref name="arguments"/> follow the schema. A call that cannot do what it was asked is a failed
    /// <see cref="ToolResult"/> that tells the model why, not an exception.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Works out what the call would do, without doing it (step 115). The executor asks it only for a tool whose risk is <see cref="RiskLevel.SideEffect"/>,
    /// shows the user the plan's <see cref="ToolPlan.Confirmation"/> and runs the plan only when they say yes. A tool that has a target to resolve (a person, a
    /// file, an application) resolves it here, so that what the user approves is what is done and a call that cannot be done is refused before anyone is
    /// asked. The default plans <see cref="RunAsync"/> as it is, and the user is asked about the call's own arguments, every one of them; no tool can
    /// give a plan that is not asked about.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ToolPlan.Do(token => RunAsync(call, arguments, context, token)));
}
