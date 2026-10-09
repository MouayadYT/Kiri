using System.Text;

namespace Assistant.Core.Domain;

/// <summary>The outcome of a <see cref="ToolCall"/>, fed back to the model.</summary>
/// <param name="ToolCallId">The <see cref="ToolCall.Id"/> this result answers.</param>
/// <param name="ToolName">Name of the tool that was called.</param>
/// <param name="Status">How the call ended.</param>
/// <param name="OutputJson">
/// JSON returned to the model: the tool's typed output on success, otherwise a description of the failure.
/// </param>
public sealed record ToolResult(string ToolCallId, string ToolName, ToolResultStatus Status, string OutputJson)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ToolCallId = {ToolCallId}, ToolName = {ToolName}, Status = {Status}");
        return true;
    }
}
