using Assistant.Core.Documents;

namespace Assistant.Documents.Extraction;

/// <summary>
/// Gathers a document's pieces under the character limit. Text arrives in the order of the document and is cleaned here
/// (<see cref="TextCleaner"/>); a piece with no text is dropped; the piece that crosses the limit is cut where a line or word
/// ends and the collector is full, which is what tells a reader to stop reading.
/// </summary>
internal sealed class SegmentCollector
{
    private readonly List<DocumentSegment> _segments = [];
    private int _remaining;

    public SegmentCollector(int maxCharacters)
    {
        _remaining = maxCharacters;
    }

    /// <summary><see langword="true"/> once text has been left out: nothing more can be added.</summary>
    public bool IsFull { get; private set; }

    /// <summary><see langword="true"/> when text was left out because of the limit.</summary>
    public bool Truncated { get; private set; }

    public IReadOnlyList<DocumentSegment> Segments => _segments;

    /// <summary>Marks the document as cut short for a reason other than characters (the most pages or slides), and stops.</summary>
    public void Stop()
    {
        Truncated = true;
        IsFull = true;
    }

    /// <summary>
    /// Adds <paramref name="text"/> (any raw text; it is cleaned) as a piece at <paramref name="location"/>.
    /// Returns <see langword="false"/> when the collector is full and the reader should read no more.
    /// </summary>
    public bool Add(DocumentLocation location, string? text)
    {
        var clean = TextCleaner.Clean(text);
        if (clean.Length == 0)
        {
            return !IsFull;
        }

        if (IsFull)
        {
            Truncated = true;
            return false;
        }

        if (clean.Length > _remaining)
        {
            clean = CutAtBoundary(clean, _remaining);
            Truncated = true;
            IsFull = true;
        }

        if (clean.Length > 0)
        {
            _segments.Add(new DocumentSegment(location, clean));
            _remaining -= clean.Length;
        }

        if (_remaining <= 0)
        {
            // Exactly full is not yet truncated: the next piece with text, if there is one, says so.
            IsFull = true;
        }

        return !IsFull;
    }

    private static string CutAtBoundary(string text, int limit)
    {
        if (limit <= 0)
        {
            return string.Empty;
        }

        if (char.IsHighSurrogate(text[limit - 1]))
        {
            limit--;
        }

        // Back to the end of a line or a word when one is near (within a tenth of the piece), so the cut does not split a word.
        var window = Math.Max(1, limit / 10);
        for (var i = limit; i > limit - window && i > 0; i--)
        {
            if (text[i - 1] is '\n' or ' ' or '\t')
            {
                limit = i;
                break;
            }
        }

        return text[..limit].TrimEnd();
    }
}
