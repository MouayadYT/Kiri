namespace Assistant.Documents.Extraction;

/// <summary>
/// The headings a section of a document is under, as one line (<c>Install &gt; Windows &gt; Requirements</c>): what tells two
/// sections of the same name apart. A heading replaces the headings of its own level and below.
/// </summary>
internal sealed class HeadingTrail
{
    /// <summary>The longest label kept; the start of it, which is the broadest heading, is what stays.</summary>
    public const int MaxLabelLength = 160;

    private const string Separator = " > ";

    private readonly List<(int Level, string Text)> _headings = [];

    /// <summary>Enters a heading of <paramref name="level"/> (1 is the broadest) and returns the trail that now applies.</summary>
    public string? Enter(int level, string heading)
    {
        _headings.RemoveAll(h => h.Level >= level);
        var text = TextCleaner.Clean(heading).Replace('\n', ' ');
        _headings.Add((level, text));
        return Current;
    }

    /// <summary>The trail that applies now, or <see langword="null"/> when no heading has text.</summary>
    public string? Current
    {
        get
        {
            var trail = string.Join(Separator, _headings.Where(h => h.Text.Length > 0).Select(h => h.Text));
            if (trail.Length == 0)
            {
                return null;
            }

            return trail.Length <= MaxLabelLength ? trail : trail[..(MaxLabelLength - 1)].TrimEnd() + "…";
        }
    }
}
