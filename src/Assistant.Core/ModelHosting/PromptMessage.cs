using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.ModelHosting;

/// <summary>One turn of the conversation in a <see cref="GenerationRequest"/>.</summary>
/// <param name="Role">Who authored the turn.</param>
/// <param name="Text">The turn's text. It may be empty, for example for an assistant turn that only calls tools.</param>
public sealed record PromptMessage(MessageRole Role, string Text)
{
    private readonly IReadOnlyList<ToolCall> _toolCalls = [];

    /// <summary>Tool calls the model requested in a <see cref="MessageRole.Assistant"/> turn.</summary>
    /// <remarks>A missing or null list reads as empty, as the JSON source generator sets every init property.</remarks>
    public IReadOnlyList<ToolCall> ToolCalls
    {
        get => _toolCalls;
        init => _toolCalls = value ?? [];
    }

    /// <summary>
    /// For a <see cref="MessageRole.Tool"/> turn, the <see cref="ToolCall.Id"/> whose result <see cref="Text"/>
    /// holds; otherwise <see langword="null"/>.
    /// </summary>
    public string? ToolCallId { get; init; }

    internal bool IsWellFormed() =>
        Enum.IsDefined(Role)
        && Text is not null
        && ToolCalls.All(call => call is { Id: not null, ToolName: not null, ArgumentsJson: not null });

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Role = {Role}, ToolCalls = {ToolCalls.Count}");
        return true;
    }
}
