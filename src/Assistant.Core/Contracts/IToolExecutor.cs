using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>Runs tool calls requested by the model.</summary>
public interface IToolExecutor
{
    /// <summary>
    /// Validates the arguments against the tool's schema, applies its <see cref="RiskLevel"/> (a call of any tool that is not read-only is
    /// confirmed through <see cref="IPermissionService"/> first, as exactly what it would do, and runs only on a yes), then runs the tool
    /// with its timeout.
    /// </summary>
    /// <returns>
    /// The result. Unknown tools, invalid arguments, declined confirmations, tool errors and timeouts are returned
    /// as results, not thrown.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="ExecuteAsync(ToolCall, CancellationToken)"/>, for a call the model made in the conversation of
    /// <paramref name="context"/>, which a tool that works on the conversation's files needs. An executor that does not care
    /// which conversation it is runs the call without it.
    /// </summary>
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default) =>
        ExecuteAsync(call, cancellationToken);
}
