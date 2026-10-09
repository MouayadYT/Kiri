namespace Assistant.Core.Documents;

/// <summary>
/// The limits a reader works within (PROJECT_SPEC §4.7). A file past them is not read, or is read only as far as they allow:
/// the Assistant reads the user's own files, but it does not spend the machine on a pathological one. A value of zero or
/// less means the default, never "no limit".
/// </summary>
public sealed record DocumentReadOptions
{
    /// <summary>The default for <see cref="MaxFileBytes"/>: 50 MiB, the same as for an attached picture.</summary>
    public const long DefaultMaxFileBytes = 50L * 1024 * 1024;

    /// <summary>The default for <see cref="MaxCharacters"/>: two million characters, far more than any prompt can hold.</summary>
    public const int DefaultMaxCharacters = 2_000_000;

    /// <summary>The default for <see cref="MaxUnits"/>.</summary>
    public const int DefaultMaxUnits = 2_000;

    /// <summary>The default for <see cref="MaxExpandedBytes"/>: 128 MiB.</summary>
    public const long DefaultMaxExpandedBytes = 128L * 1024 * 1024;

    /// <summary>The options that apply when none are given.</summary>
    public static DocumentReadOptions Default { get; } = new();

    /// <summary>
    /// The largest file, in bytes on disk, that is opened. A larger one has the status <see cref="DocumentReadStatus.TooLarge"/>
    /// and is not read at all.
    /// </summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    /// <summary>
    /// The most characters of text returned. The text that follows is left out and <see cref="DocumentReadResult.Truncated"/>
    /// says so; the reader stops reading there rather than reading the rest to throw it away.
    /// </summary>
    public int MaxCharacters { get; init; } = DefaultMaxCharacters;

    /// <summary>The most pages or slides read. Those that follow are left out and <see cref="DocumentReadResult.Truncated"/> says so.</summary>
    public int MaxUnits { get; init; } = DefaultMaxUnits;

    /// <summary>
    /// For a file that is a package of compressed parts (DOCX, PPTX): the most the parts that hold text may unpack to, counted
    /// from what the package declares before anything is unpacked. A package that says more has the status
    /// <see cref="DocumentReadStatus.TooLarge"/>, so a small file that expands a thousandfold is not opened.
    /// </summary>
    public long MaxExpandedBytes { get; init; } = DefaultMaxExpandedBytes;
}
