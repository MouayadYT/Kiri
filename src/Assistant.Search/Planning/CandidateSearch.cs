using Assistant.Core.Contracts;

namespace Assistant.Search.Planning;

/// <summary>
/// The searches that gather candidates when no name holds every word of a request: each word alone, the start of each longer
/// word (so "biolgy" still finds "Biology" through "biol"), and, when the request has other limits, the newest files within them
/// (so a word mistyped at its start, "arhciv", still meets "Archi"). Every other limit of the plan (the kind, the days, the folder,
/// the size and the words inside the file) is kept and never loosened. What they find is scored by <see cref="NameMatch"/>.
/// </summary>
internal static class CandidateSearch
{
    /// <summary>The most words looked for one at a time.</summary>
    public const int MaxWords = 5;

    /// <summary>The most of each word's matches kept.</summary>
    public const int MaxPerWord = 15;

    /// <summary>The most of the newest files within the plan's other limits kept.</summary>
    public const int MaxNewest = 30;

    /// <summary>The most candidates, all searches together.</summary>
    public const int MaxCandidates = 80;

    /// <summary>How long a word must be for its start to be looked for too, and how long that start is.</summary>
    public const int StemFrom = 6;

    /// <summary>How many letters of a long word's start are looked for.</summary>
    public const int StemLength = 4;

    /// <summary>
    /// The candidate searches for <paramref name="original"/>, whose name words are <paramref name="words"/>. None when it has
    /// no words.
    /// </summary>
    public static IReadOnlyList<FileSearchQuery> Queries(FileSearchQuery original, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(words);
        var usable = words
            .Select(word => word.Trim())
            .Where(word => word.Count(char.IsLetterOrDigit) >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxWords)
            .ToArray();
        if (usable.Length == 0)
        {
            return [];
        }

        var patterns = new List<string>();
        foreach (var word in usable)
        {
            Add(patterns, word);
        }

        // A number is looked for in the other way it is written too: "three" in "Milestone 3", "3" in "Milestone Three".
        foreach (var word in usable.Take(MaxWords))
        {
            foreach (var form in NumberForms.OtherForms(word.ToLowerInvariant()).Where(form => form.All(char.IsLetterOrDigit) && !IsOrdinal(form)))
            {
                Add(patterns, form);
            }
        }

        foreach (var word in usable)
        {
            if (word.Length >= StemFrom && !word.Contains(' ', StringComparison.Ordinal))
            {
                Add(patterns, word[..StemLength]);
            }
        }

        var queries = patterns
            .Select(pattern => original with
            {
                Text = pattern.Contains(' ', StringComparison.Ordinal) ? $"\"{pattern}\"" : pattern,
                MatchContents = false,
                MaxResultsPerType = MaxPerWord,
            })
            .ToList();

        if (HasOtherLimits(original))
        {
            queries.Add(original with
            {
                Text = null,
                MatchContents = false,
                MaxResultsPerType = MaxNewest,
                Order = original.Created.IsUnbounded ? FileSearchOrder.ModifiedDescending : FileSearchOrder.CreatedDescending,
            });
        }

        return queries;
    }

    // A limit other than the words: a kind, days, a folder, a size, or words inside the file.
    private static bool HasOtherLimits(FileSearchQuery query) =>
        query.Extensions.Count > 0 || query.Kind is not null || !query.Modified.IsUnbounded || !query.Created.IsUnbounded
        || query.Folder is not null || !query.Size.IsUnbounded || query.ContentTerm is not null;

    // "3rd", "third": only the plain digit and word forms are looked for.
    private static bool IsOrdinal(string form) =>
        form.EndsWith("th", StringComparison.Ordinal) || form.EndsWith("st", StringComparison.Ordinal)
        || form.EndsWith("nd", StringComparison.Ordinal) || form.EndsWith("rd", StringComparison.Ordinal);

    private static void Add(List<string> patterns, string pattern)
    {
        if (!patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            patterns.Add(pattern);
        }
    }
}
