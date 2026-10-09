namespace Assistant.Core.Documents;

/// <summary>
/// How a document's text is cut into passages (PROJECT_SPEC §4.7). The same text and the same options always give the same
/// passages. A value of zero or less is the default, except for <see cref="OverlapCharacters"/>, where zero means no overlap.
/// </summary>
public sealed record DocumentChunkingOptions
{
    /// <summary>The default for <see cref="TargetCharacters"/>: about a fifth of a page, which is a few hundred tokens.</summary>
    public const int DefaultTargetCharacters = 1_200;

    /// <summary>The least a <see cref="TargetCharacters"/> is taken to be: a passage of a few characters is no passage.</summary>
    public const int SmallestTargetCharacters = 20;

    /// <summary>The default for <see cref="OverlapCharacters"/>: about two sentences.</summary>
    public const int DefaultOverlapCharacters = 150;

    /// <summary>The default for <see cref="MinCharacters"/>.</summary>
    public const int DefaultMinCharacters = 250;

    /// <summary>The default for <see cref="MaxPassages"/>.</summary>
    public const int DefaultMaxPassages = 5_000;

    /// <summary>The options that apply when none are given.</summary>
    public static DocumentChunkingOptions Default { get; } = new();

    /// <summary>
    /// About how long a passage is: a passage of a long part of the document ends at the last paragraph, line or sentence end
    /// (or, failing that, word) before this many characters, so it is never much longer and is usually a little shorter.
    /// </summary>
    public int TargetCharacters { get; init; } = DefaultTargetCharacters;

    /// <summary>
    /// How many characters of the end of a passage begin the next one of the same part of the document, cut at a word, so that
    /// what falls on a cut is whole in one of them. Zero is no overlap; a negative value is the default. Never more than a
    /// third of <see cref="TargetCharacters"/>.
    /// </summary>
    public int OverlapCharacters { get; init; } = DefaultOverlapCharacters;

    /// <summary>
    /// The least a passage is when it can be helped. A part of the document shorter than this (a slide, a short section) is
    /// gathered with the ones after it while they fit in <see cref="TargetCharacters"/>, and what is left over at the end of a
    /// long part is joined to the passage before it, which is then longer than the target by at most this much. Never more than
    /// half of <see cref="TargetCharacters"/>.
    /// </summary>
    public int MinCharacters { get; init; } = DefaultMinCharacters;

    /// <summary>The most passages made; the text after them is left out and <see cref="DocumentContext.Truncated"/> says so.</summary>
    public int MaxPassages { get; init; } = DefaultMaxPassages;

    /// <summary>The options with every value in its range: the defaults for what is unset, and the limits that depend on each other applied.</summary>
    public DocumentChunkingOptions Resolve()
    {
        var target = TargetCharacters > 0 ? Math.Max(TargetCharacters, SmallestTargetCharacters) : DefaultTargetCharacters;
        var overlap = OverlapCharacters < 0 ? DefaultOverlapCharacters : OverlapCharacters;
        var min = MinCharacters > 0 ? MinCharacters : DefaultMinCharacters;
        return new DocumentChunkingOptions
        {
            TargetCharacters = target,
            OverlapCharacters = Math.Min(overlap, target / 3),
            MinCharacters = Math.Min(min, target / 2),
            MaxPassages = MaxPassages > 0 ? MaxPassages : DefaultMaxPassages,
        };
    }
}
