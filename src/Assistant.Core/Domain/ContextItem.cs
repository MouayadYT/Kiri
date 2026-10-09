using System.Text;
using System.Text.Json.Serialization;

namespace Assistant.Core.Domain;

/// <summary>User-scoped data attached to a request, shown in the UI as a context chip.</summary>
/// <remarks>
/// Serialization writes only the descriptor (<see cref="Id"/>, <see cref="Type"/>, <see cref="DisplayName"/>,
/// <see cref="FilePath"/>). Captured content in <see cref="Text"/> and <see cref="ImageData"/> lives in memory
/// only and is never serialized (PROJECT_SPEC §3.5).
/// </remarks>
/// <param name="Id">Unique identifier of the item.</param>
/// <param name="Type">The kind of context the item holds.</param>
/// <param name="DisplayName">Label shown on the context chip.</param>
public sealed record ContextItem(Guid Id, ContextItemType Type, string DisplayName)
{
    /// <summary>Full path of a <see cref="ContextItemType.File"/> item; otherwise <see langword="null"/>.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// How the item came to be context, which ranks it when not all of the context fits the model's window. Not saved:
    /// an item read back from history ranks as the kind of context it is.
    /// </summary>
    [JsonIgnore]
    public ContextSource Source { get; init; }

    /// <summary>
    /// The web page the text was selected on and the browser it was in, for a selection that came from a browser; otherwise
    /// <see langword="null"/>. Not saved.
    /// </summary>
    [JsonIgnore]
    public WebPageOrigin? WebPage { get; init; }

    /// <summary>
    /// Captured text (selection, page text, extracted file text or OCR text), or <see langword="null"/> when not
    /// captured or no longer available. Memory only.
    /// </summary>
    [JsonIgnore]
    public string? Text { get; init; }

    /// <summary>
    /// Encoded image bytes (for example PNG or JPEG) of a <see cref="ContextItemType.Screenshot"/> region or an
    /// <see cref="ContextItemType.Image"/>, exactly as captured or read: the pipeline never changes them, and prepares a
    /// copy for the model (<see cref="Contracts.IImagePreprocessor"/>). Memory only.
    /// </summary>
    [JsonIgnore]
    public ReadOnlyMemory<byte> ImageData { get; init; }

    /// <summary>
    /// Whether the item goes with every question of the conversation until it is taken back, instead of once with the question it
    /// was supplied for: a screenshot the user is asking about, so that follow-ups such as "the second row" need no new capture
    /// (PROJECT_SPEC §4.6). The context service keeps its content, where it keeps only the descriptor of an item that has been
    /// sent. Not saved.
    /// </summary>
    [JsonIgnore]
    public bool Retained { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}, Type = {Type}");
        return true;
    }
}
