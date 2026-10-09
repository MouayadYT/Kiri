using Assistant.Core.Confirmation;
using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>Asks the user for consent before a side effect happens (PROJECT_SPEC §3.1, P8; §4.8, step 115).</summary>
public interface IPermissionService
{
    /// <summary>
    /// Shows the user <paramref name="confirmation"/>, which says exactly what <paramref name="call"/> of <paramref name="tool"/> would do and to what, in the
    /// conversation <paramref name="context"/> names, and waits for the answer. Only <see cref="ConfirmationDecision.Approved"/> lets the call run: a user who
    /// does not answer in time, and a question nothing could show, are as good as a no.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the question is taken back.</exception>
    Task<ConfirmationDecision> ConfirmToolCallAsync(
        ToolDefinition tool,
        ToolCall call,
        ToolContext context,
        ToolConfirmation confirmation,
        CancellationToken cancellationToken = default);
}
