using System.Text;

namespace Assistant.Core.Domain;

/// <summary>The model's structured request to run a tool.</summary>
/// <param name="Id">Identifier that correlates the call with its <see cref="ToolResult"/>.</param>
/// <param name="ToolName">The tool's stable snake_case name.</param>
/// <param name="ArgumentsJson">
/// Arguments as JSON, exactly as the model emitted them. They are unvalidated and may be malformed.
/// </param>
public sealed record ToolCall(string Id, string ToolName, string ArgumentsJson)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}, ToolName = {ToolName}");
        return true;
    }
}
