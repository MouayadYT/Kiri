namespace Assistant.Core.Documents;

/// <summary>
/// How many passages of a document go into a prompt (PROJECT_SPEC §4.7): the most passages and the most characters, which keep
/// what the model reads to what it can read, however long the document is. A value of zero or less is the default.
/// </summary>
public sealed record PassageSelectionOptions
{
    /// <summary>The default for <see cref="MaxPassages"/>.</summary>
    public const int DefaultMaxPassages = 8;

    /// <summary>The default for <see cref="MaxCharacters"/>: about two and a half thousand tokens of prose.</summary>
    public const int DefaultMaxCharacters = 8_000;

    /// <summary>The options that apply when none are given.</summary>
    public static PassageSelectionOptions Default { get; } = new();

    /// <summary>The most passages selected.</summary>
    public int MaxPassages { get; init; } = DefaultMaxPassages;

    /// <summary>
    /// The most characters of passages selected, counted once where two passages that follow each other overlap. A passage that
    /// does not fit is skipped and a shorter one may take its place; when even the best one is longer than this, it is cut
    /// to fit at a paragraph, sentence or word end.
    /// </summary>
    public int MaxCharacters { get; init; } = DefaultMaxCharacters;

    /// <summary>The options with every value in its range.</summary>
    public PassageSelectionOptions Resolve() => new()
    {
        MaxPassages = MaxPassages > 0 ? MaxPassages : DefaultMaxPassages,
        MaxCharacters = MaxCharacters > 0 ? MaxCharacters : DefaultMaxCharacters,
    };
}
