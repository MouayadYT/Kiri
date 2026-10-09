using Assistant.Core.Documents;

namespace Assistant.Documents.Extraction;

/// <summary><see cref="DocumentReadOptions"/> with every value made usable: zero or less is the default, never "no limit".</summary>
internal readonly record struct DocumentReadLimits(long MaxFileBytes, int MaxCharacters, int MaxUnits, long MaxExpandedBytes)
{
    /// <summary>The most entries a package may list. A package of millions of tiny entries is slow to open whatever its size.</summary>
    public const int MaxPackageEntries = 20_000;

    public static DocumentReadLimits From(DocumentReadOptions? options)
    {
        var source = options ?? DocumentReadOptions.Default;
        return new DocumentReadLimits(
            source.MaxFileBytes > 0 ? source.MaxFileBytes : DocumentReadOptions.DefaultMaxFileBytes,
            source.MaxCharacters > 0 ? source.MaxCharacters : DocumentReadOptions.DefaultMaxCharacters,
            source.MaxUnits > 0 ? source.MaxUnits : DocumentReadOptions.DefaultMaxUnits,
            source.MaxExpandedBytes > 0 ? source.MaxExpandedBytes : DocumentReadOptions.DefaultMaxExpandedBytes);
    }
}
