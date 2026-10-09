using System.Globalization;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Search.Planning;

/// <summary>
/// Reads the requests that ask for the most recent pictures and nothing else: "show me the last 5 screenshots I took", "find
/// the image I took yesterday", "my photos from last week". They have a fixed shape (a count, a noun, a few filler words and a
/// period), so fixed rules answer them at once and the same way every time, with no model and no chance of a wrong date.
/// Anything with a word the rules do not know, such as a topic ("screenshots of the error"), is not read here: it is read word by
/// word (<see cref="RequestReader"/>).
/// </summary>
internal sealed class RecentMediaTemplate(PlanCalendar calendar)
{
    /// <summary>How many pictures a gallery shows when the request does not say: three rows of three.</summary>
    public const int DefaultCount = 9;

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["twelve"] = 12, ["fifteen"] = 15, ["twenty"] = 20,
    };

    private static readonly HashSet<string> Recency = new(StringComparer.Ordinal)
    {
        "last", "latest", "newest", "recent", "recently",
    };

    // The pictures' names in the singular and the plural.
    private static readonly HashSet<string> Singular = new(StringComparer.Ordinal)
    {
        "screenshot", "photo", "picture", "pic", "image",
    };

    private static readonly HashSet<string> Plural = new(StringComparer.Ordinal)
    {
        "screenshots", "photos", "pictures", "pics", "images",
    };

    // Words that may follow the noun without saying anything more about what is asked for.
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "i", "ive", "have", "has", "took", "taken", "take", "made", "make", "captured", "capture", "saved", "save",
        "created", "downloaded", "that", "which", "of", "mine", "on", "this", "pc", "computer", "laptop", "recently",
        "lately", "from", "in", "the", "my", "me", "please", "for", "to",
    };

    // Filler that says the pictures' creation time is what counts, rather than when they last changed.
    private static readonly HashSet<string> MeansCreated = new(StringComparer.Ordinal)
    {
        "created", "made", "downloaded",
    };

    /// <summary>The plan for <paramref name="request"/>, or <see langword="null"/> when it is not of this shape.</summary>
    public PlannedFileSearch? TryPlan(string request)
    {
        var words = RequestText.Words(request);
        if (words.Count == 0)
        {
            return null;
        }

        var index = RequestGrammar.SkipLeading(words, out _);
        SkipDeterminers(words, ref index);

        var recent = false;
        int? count = null;
        while (index < words.Count)
        {
            if (Recency.Contains(words[index]) && !IsPeriodStart(words, index))
            {
                recent = true;
                index++;
            }
            else if (words[index] == "most" && index + 1 < words.Count && words[index + 1] == "recent")
            {
                recent = true;
                index += 2;
            }
            else if (count is null && TryReadCount(words[index], out var number))
            {
                count = number;
                index++;
            }
            else
            {
                break;
            }

            SkipDeterminers(words, ref index);
        }

        if (index >= words.Count || !TryReadNoun(words, ref index, out var isPlural, out var isScreenshot))
        {
            return null;
        }

        (DateOnly First, DateOnly Last)? period = null;
        var useCreated = false;
        while (index < words.Count)
        {
            if (period is null && TryReadPeriod(words, ref index) is { } read)
            {
                period = read;
            }
            else if (Filler.Contains(words[index]))
            {
                useCreated |= MeansCreated.Contains(words[index]);
                index++;
            }
            else
            {
                return null;
            }
        }

        if (count is 0)
        {
            return null;
        }

        var limit = count is { } given
            ? Math.Min(given, WindowsFileSearchService.MaxResultsPerType)
            : recent && !isPlural ? 1 : DefaultCount;
        var range = period is { } days ? calendar.Range(days.First, days.Last) : default;

        var query = new FileSearchQuery
        {
            Filename = isScreenshot ? "Screenshot" : null,
            Extensions = ImageFileTypes.Extensions,
            Types = [SearchResultItemType.File],
            MaxResultsPerType = limit,
            Order = useCreated ? FileSearchOrder.CreatedDescending : FileSearchOrder.ModifiedDescending,
            Created = useCreated ? range : default,
            Modified = useCreated ? default : range,
        };
        return new PlannedFileSearch(query, FileSearchPlanSource.Template)
        {
            KindName = isScreenshot ? ("screenshot", "screenshots") : ("image", "images"),
        };
    }

    private static void SkipDeterminers(IReadOnlyList<string> words, ref int index)
    {
        while (index < words.Count && RequestGrammar.IsDeterminer(words[index]))
        {
            index++;
        }
    }

    private static bool TryReadCount(string word, out int count)
    {
        if (NumberWords.TryGetValue(word, out count))
        {
            return true;
        }

        return word.All(char.IsAsciiDigit) && word.Length <= 4
            && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }

    // "screenshot", "photos", "screen shots": the noun, and whether it says several and whether it is a screenshot.
    private static bool TryReadNoun(IReadOnlyList<string> words, ref int index, out bool isPlural, out bool isScreenshot)
    {
        var noun = words[index];
        isPlural = false;
        isScreenshot = false;
        if (noun == "screen" && index + 1 < words.Count && words[index + 1] is "shot" or "shots")
        {
            isPlural = words[index + 1] == "shots";
            isScreenshot = true;
            index += 2;
            return true;
        }

        if (Singular.Contains(noun) || Plural.Contains(noun))
        {
            isPlural = Plural.Contains(noun);
            isScreenshot = noun is "screenshot" or "screenshots";
            index++;
            return true;
        }

        return false;
    }

    // "last week" and "last 3 days" are periods, not the recency of "the last 5 screenshots".
    private bool IsPeriodStart(IReadOnlyList<string> words, int index)
    {
        var at = index;
        return TryReadPeriod(words, ref at) is not null;
    }

    // A period the words begin with at the index: moves past it and returns the days it covers, or returns null.
    private (DateOnly First, DateOnly Last)? TryReadPeriod(IReadOnlyList<string> words, ref int index)
    {
        var word = words[index];
        if (word is "today" or "yesterday")
        {
            index++;
            return calendar.Period(word);
        }

        if (word is "this" or "last" && index + 1 < words.Count && words[index + 1] is "week" or "month" or "year")
        {
            var period = calendar.Period($"{word} {words[index + 1]}");
            index += 2;
            return period;
        }

        // "past 3 days", "last 2 weeks", "in the last 6 months": the "in" and "the" are filler before it.
        var unit = index + 2 < words.Count ? words[index + 2] : null;
        if (word is "past" or "last" && index + 2 < words.Count && TryReadCount(words[index + 1], out var amount)
            && amount > 0 && unit is "day" or "days" or "week" or "weeks" or "month" or "months")
        {
            var period = unit switch
            {
                "day" or "days" => calendar.LastDays(amount),
                "week" or "weeks" => calendar.LastWeeks(amount),
                _ => calendar.LastMonths(amount),
            };
            index += 3;
            return period;
        }

        return null;
    }
}
