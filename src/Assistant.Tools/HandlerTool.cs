using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Tools;

/// <summary>
/// What a tool runs (PROJECT_SPEC §4.8): the call, its arguments (already checked against the tool's schema), and the conversation
/// it was made in. A call that cannot do what it was asked is a failed <see cref="ToolResult"/> that tells the model why, not an exception.
/// </summary>
public delegate Task<ToolResult> ToolHandler(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken);

/// <summary>
/// A tool made of a typed definition and a handler: the way to register a tool that needs no class of its own. The definition says
/// what it is, what it takes, how much it can change, which permission it needs and how long it may run; the handler is what it does.
/// </summary>
public sealed class HandlerTool : ITool
{
    private readonly ToolHandler _handler;
    private readonly Func<ToolContext, bool>? _isOffered;
    private readonly Func<JsonElement, ToolConfirmation?>? _confirmation;
    private readonly bool _focused;

    /// <summary>Creates a tool from <paramref name="definition"/> that runs <paramref name="handler"/>.</summary>
    /// <param name="definition">What the tool is.</param>
    /// <param name="handler">What it does.</param>
    /// <param name="isOffered">Whether it is worth offering in a conversation; always, when not given.</param>
    /// <param name="confirmation">
    /// For a tool that changes something: the question the user is asked, from the call's arguments (step 115). Without it the user is asked about the
    /// arguments themselves, all of them.
    /// </param>
    /// <param name="focused">Whether the tool is offered only for a request that is about it, and so is kept first when tools have to be left out.</param>
    public HandlerTool(
        ToolDefinition definition, ToolHandler handler, Func<ToolContext, bool>? isOffered = null, Func<JsonElement, ToolConfirmation?>? confirmation = null,
        bool focused = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        Definition = definition;
        _handler = handler;
        _isOffered = isOffered;
        _confirmation = confirmation;
        _focused = focused;
    }

    /// <inheritdoc/>
    public ToolDefinition Definition { get; }

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context) => _isOffered?.Invoke(context) ?? true;

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => _focused && IsOffered(context);

    /// <inheritdoc/>
    public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        _handler(call, arguments, context, cancellationToken);

    /// <inheritdoc/>
    public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ToolPlan.Do(token => _handler(call, arguments, context, token), _confirmation?.Invoke(arguments)));
}
