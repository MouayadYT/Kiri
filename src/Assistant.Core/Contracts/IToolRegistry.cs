using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>The catalog of tools the model may call.</summary>
public interface IToolRegistry
{
    /// <summary>Every registered tool, in a stable order.</summary>
    IReadOnlyList<ToolDefinition> Tools { get; }

    /// <summary>
    /// The tools worth offering to the model for a turn of the conversation <paramref name="context"/> names: a tool that is of no use
    /// in the conversation (one about a screenshot, in a conversation that has none) is left out, so its description does not take room
    /// in the prompt or draw the model to a tool it cannot use. Without an override, every registered tool.
    /// </summary>
    IReadOnlyList<ToolDefinition> ToolsFor(ToolContext context) => Tools;

    /// <summary>
    /// The names of the tools of <see cref="ToolsFor"/> that are offered because the request is about them (the clock's tools for a timer, the messaging
    /// tools in a conversation about a message, the tools of the connected app the request names), as opposed to the ones that are always there. When
    /// not every tool can be offered, these are kept first. Without an override, none.
    /// </summary>
    IReadOnlySet<string> FocusedFor(ToolContext context) => new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets ready the tools that are loaded by what the user asks (the tools of connected apps, PROJECT_SPEC §4.8), before
    /// <see cref="ToolsFor"/> is asked for the turn <paramref name="context"/> names: loading one can mean starting or reaching an app, which
    /// takes time and so is asynchronous, while <see cref="ToolsFor"/> stays instant. It gives up on what is slow, so the turn is never held
    /// up for long, and what it cannot load is left out of the tools offered; it throws only when <paramref name="cancellationToken"/> is
    /// cancelled. Without an override, nothing is loaded lazily.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task PrepareToolsAsync(ToolContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Returns the tool named <paramref name="name"/>, or <see langword="null"/> when none is registered.</summary>
    ToolDefinition? Find(string name);
}
