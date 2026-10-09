using Assistant.Core.Domain;

namespace Assistant.UI.Messages;

/// <summary>
/// A piece of text the user attached to a conversation to ask about (PROJECT_SPEC §4.2), such as a selection: shown by its
/// first words as a chip above the follow-up composer until the question is asked, and then above the user's message. The text
/// lives in memory only: it goes to the model with the question, and is never saved or logged.
/// </summary>
public sealed class TextAttachment
{
    // A name longer than this would not fit its chip, so the first words are cut here.
    private const int MaxNameLength = 48;

    // More than the Ask panel's selection card has room for (three lines), which cuts it again at the end of its last line.
    private const int MaxPreviewLength = 200;
    private const char Ellipsis = (char)0x2026;

    /// <summary>Creates an attachment of <paramref name="text"/>.</summary>
    /// <param name="text">What was selected or pasted.</param>
    /// <param name="name">What the chip is called, or <see langword="null"/> for the text's first words.</param>
    /// <param name="webPage">The web page and browser the text was selected in, or <see langword="null"/> for text from anywhere else.</param>
    public TextAttachment(string text, string? name = null, WebPageOrigin? webPage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Text = text;
        WebPage = webPage is { IsEmpty: false } ? webPage : null;
        Name = string.IsNullOrWhiteSpace(name) ? FirstWords(text) : name;
        Preview = PreviewOf(text);
    }

    /// <summary>The attached text, as it is given to the model.</summary>
    public string Text { get; }

    /// <summary>Where the text was selected, for text that came from a web page in a browser; otherwise <see langword="null"/>.</summary>
    public WebPageOrigin? WebPage { get; }

    /// <summary>Whether page text around the selection came with it (step 88), which the conversation says so the user knows more than the selection goes to the model.</summary>
    public bool HasNearbyContext => WebPage is { HasNearbyContext: true };

    /// <summary>
    /// What says that page text around the selection is included, such as "Plus 1,204 characters of nearby page text", or empty when it is
    /// not. Culture-sensitive only in the digits' grouping.
    /// </summary>
    public string NearbyNotice =>
        WebPage is { HasNearbyContext: true } page
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Plus {page.NearbyLength:N0} {(page.NearbyLength == 1 ? "character" : "characters")} of nearby page text")
            : "";

    /// <summary>What the chip shows: the text's first words on one line, cut with an ellipsis when they are long.</summary>
    public string Name { get; }

    /// <summary>
    /// The start of the text for the Ask panel to show the user what was selected: its runs of space and its line breaks made single
    /// spaces, and cut with an ellipsis when it is long.
    /// </summary>
    public string Preview { get; }

    /// <summary>Whether <paramref name="other"/> holds the same text, apart from the space around it.</summary>
    public bool IsSameText(TextAttachment other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Text.Trim(), other.Text.Trim(), StringComparison.Ordinal);
    }

    // Everything on one line, cut at a word's end when that is near enough.
    private static string PreviewOf(string text)
    {
        var words = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (words.Length <= MaxPreviewLength)
        {
            return words;
        }

        var cut = words[..MaxPreviewLength];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > MaxPreviewLength - 20)
        {
            cut = cut[..lastSpace];
        }

        // A surrogate pair is not split.
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1];
        }

        return cut.TrimEnd() + Ellipsis;
    }

    // The first non-blank line, its runs of space made one; the text is non-blank, so there is always one.
    private static string FirstWords(string text)
    {
        var line = text.Split('\n', '\r').First(candidate => !string.IsNullOrWhiteSpace(candidate));
        var words = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return words.Length <= MaxNameLength ? words : words[..(MaxNameLength - 1)].TrimEnd() + Ellipsis;
    }
}
