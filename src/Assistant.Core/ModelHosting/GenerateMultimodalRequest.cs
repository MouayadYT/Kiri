using System.Text;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Generates a text answer for a prompt that includes images, such as a screenshot region. A model that cannot see
/// answers with <see cref="ModelHostErrorCode.VisionNotSupported"/>.
/// </summary>
/// <inheritdoc cref="GenerationRequest"/>
/// <param name="ModelId">The model to generate with. It must support vision.</param>
/// <param name="Instructions">System instructions that precede the conversation.</param>
/// <param name="Messages">The conversation, oldest first.</param>
/// <param name="Images">
/// Encoded images (for example PNG) that accompany the last user message. They cross the pipe in base64 and are never
/// written to disk (PROJECT_SPEC §3.1, P7).
/// </param>
public sealed record GenerateMultimodalRequest(
    string ModelId,
    string Instructions,
    IReadOnlyList<PromptMessage> Messages,
    IReadOnlyList<ReadOnlyMemory<byte>> Images)
    : GenerationRequest(ModelId, Instructions, Messages)
{
    internal override bool IsWellFormed() =>
        base.IsWellFormed() && Images is { Count: > 0 } && Images.All(image => !image.IsEmpty);

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append($", Images = {Images?.Count}");
        return true;
    }
}
