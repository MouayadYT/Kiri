using System.Text;

namespace Assistant.Core.Domain;

/// <summary>
/// A structured part of an assistant <see cref="Message"/> that is not prose, such as a calculation's result card, a
/// block of code, a gallery of images or a list of files, saved with the message so it can be drawn again.
/// </summary>
/// <remarks>
/// A card holds only what its presenter needs to draw it, described as data: never file contents, recognized text or
/// image bytes (PROJECT_SPEC §3.5). An image or a file is a reference to where it is. Which kinds exist, and what their
/// JSON holds, is the presenter's business; storage keeps the kind and the JSON as they are, so a new kind needs no
/// change to the database.
/// </remarks>
public sealed record CardMetadata
{
    /// <summary>The longest kind name a card can have.</summary>
    public const int MaxKindLength = 64;

    /// <summary>Creates a card.</summary>
    /// <param name="kind">The card's kind, a short snake_case name such as <c>rich_answer_card</c>.</param>
    /// <param name="dataJson">What the presenter needs to draw the card, as a JSON document.</param>
    /// <param name="textOffset">How many characters of the message's text come before the card.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not a lowercase snake_case name, or the JSON is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="textOffset"/> is negative.</exception>
    public CardMetadata(string kind, string dataJson, int textOffset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataJson);
        ArgumentOutOfRangeException.ThrowIfNegative(textOffset);
        if (kind.Length > MaxKindLength || !kind.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            throw new ArgumentException("A card's kind is a lowercase snake_case name of at most 64 characters.", nameof(kind));
        }

        Kind = kind;
        DataJson = dataJson;
        TextOffset = textOffset;
    }

    /// <summary>The card's kind, which says how its data is read and which presenter draws it.</summary>
    public string Kind { get; }

    /// <summary>The card's data as JSON.</summary>
    public string DataJson { get; }

    /// <summary>
    /// Where the card sits among the message's prose: the number of characters of <see cref="Message.Text"/> that come
    /// before it. Zero puts it first, and the length of the text puts it last.
    /// </summary>
    public int TextOffset { get; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}");
        return true;
    }
}
