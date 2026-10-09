using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>A fully assembled prompt for one generation.</summary>
/// <param name="Instructions">System instructions that precede the conversation.</param>
/// <param name="Messages">
/// The conversation, oldest first. Untrusted context is already delimited inside the message text.
/// </param>
public sealed record ModelRequest(string Instructions, IReadOnlyList<Message> Messages)
{
    /// <summary>Tools the model may call. Empty when tool calling is off for this generation.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    /// <summary>Encoded images (for example PNG) that accompany the last user message. Requires a vision model.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Images { get; init; } = [];

    /// <summary>Tokens reserved for the output, or <see langword="null"/> for the model's default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// How freely the model chooses its words, from 0 (always the likeliest) to 2, or <see langword="null"/> for the engine's default.
    /// A request that offers tools asks for a low one, since a call to a tool should be the same call every time.
    /// </summary>
    public double? Temperature { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"Messages = {Messages.Count}, Tools = {Tools.Count}, Images = {Images.Count}, " +
            $"MaxOutputTokens = {MaxOutputTokens}, Temperature = {Temperature}");
        return true;
    }
}
