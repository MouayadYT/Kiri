using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Search.Planning;

/// <summary>What a request to find files was read as: its kind of file, its days, its order, its place and its words.</summary>
internal sealed record RequestReading
{
    /// <summary>The words the file's name should hold, in the order they were asked; a quoted phrase is one of them.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Whether the words are a topic ("about biology"), so they may be inside the file as well as in its name.</summary>
    public bool About { get; init; }

    /// <summary>A phrase that must be inside the file: "that mention quarterly revenue".</summary>
    public string? ContentPhrase { get; init; }

    /// <summary>The kind of file asked for, if one was named.</summary>
    public FileType? Type { get; init; }

    /// <summary>Whether folders, rather than files, were asked for.</summary>
    public bool Folders { get; init; }

    /// <summary>The days asked about, both included, in the user's own time zone.</summary>
    public (DateOnly First, DateOnly Last)? Period { get; init; }

    /// <summary>Whether the days and the order are about when the file was made (made, downloaded, took), not last changed.</summary>
    public bool ByCreation { get; init; }

    /// <summary>The order asked for, if one was: newest, oldest, biggest, smallest.</summary>
    public FileSearchOrder? Order { get; init; }

    /// <summary>How many were asked for, if a number was said or "the last" named one thing.</summary>
    public int? Limit { get; init; }

    /// <summary>The library asked about, by its name ("downloads"), and where it is on this PC.</summary>
    public (string Name, string Path)? Folder { get; init; }

    /// <summary>The size asked for.</summary>
    public SizeRange Size { get; init; }

    /// <summary>Whether anything at all was read that a search can look for.</summary>
    public bool HasCriterion => Keywords.Count > 0 || ContentPhrase is not null || Type is not null || Period is not null
        || Folder is not null || !Size.IsUnbounded;
}

/// <summary>
/// Reads a request to find files ("find the PDF about biology I edited last Tuesday", "the last doc I made", "fidn annas arhciv
/// pdff") with fixed rules, and never a model: the kind of file (<see cref="FileTypeWords"/>), the days, whether they are when the
/// file was made or changed, newest or oldest, how many, a library ("in my downloads"), a size, a phrase inside the file, and what
/// is left, which are the words of the name. The days are worked out here from the clock, so they are always right; the words
/// a person types by mistake ("fidn", "tuseday") are read as meant when they are long enough to tell.
/// </summary>
internal sealed partial class RequestReader(PlanCalendar calendar, IPlanFolders folders)
{
    /// <summary>The most words of a name that are kept.</summary>
    public const int MaxKeywords = 6;

    /// <summary>How many files are shown when the request does not say.</summary>
    public const int DefaultLimit = 10;

    private static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private static readonly string[] Months =
    [
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
    ];

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["twelve"] = 12, ["fifteen"] = 15, ["twenty"] = 20, ["a"] = 1, ["an"] = 1,
    };

    // The verbs a request begins with.
    private static readonly string[] FindVerbs =
    [
        "find", "search", "show", "locate", "look", "looking", "fetch", "list", "display", "bring", "where", "grab", "pull",
        "open", "give", "need", "want", "get", "see",
    ];

    // The words that may come before a request's verb.
    private static readonly HashSet<string> Courtesies = new(StringComparer.Ordinal)
    {
        "please", "hey", "hi", "ok", "okay", "can", "could", "would", "will", "you", "u", "i", "want", "need", "to", "go", "ahead", "and",
    };

    // The verbs that ask to find wherever they stand; the others ("list", "open", "show") only where a verb stands, as they are
    // names too ("the zip code list", "the open day flyer").
    private static readonly HashSet<string> AnywhereVerbs = new(StringComparer.Ordinal)
    {
        "find", "search", "locate", "where", "fetch", "looking",
    };

    // The verbs long enough to be read when mistyped where a verb stands.
    private static readonly string[] MistypableVerbs = ["find", "search", "locate", "fetch", "display"];

    // Kind words that are everyday words too: a kind only when nothing that names more follows ("the zip I downloaded", not
    // "the zip code list"; "my music", not "the music theory notes").
    private static readonly HashSet<string> EverydayKinds = new(StringComparer.Ordinal)
    {
        "zip", "zips", "sheet", "sheets", "deck", "decks", "clip", "clips", "track", "tracks", "music", "recording", "recordings",
        "movie", "movies", "slides", "excel", "audio", "song", "songs",
    };

    // Words that say it was made then: created, downloaded, taken.
    private static readonly HashSet<string> MadeWords = new(StringComparer.Ordinal)
    {
        "made", "make", "created", "create", "downloaded", "download", "took", "taken", "take", "received", "got", "shot",
        "captured", "recorded", "scanned", "exported",
    };

    // Words that say it was changed then.
    private static readonly HashSet<string> ChangedWords = new(StringComparer.Ordinal)
    {
        "edited", "edit", "changed", "modified", "updated", "saved", "worked", "touched", "wrote", "written",
    };

    private static readonly HashSet<string> NewestWords = new(StringComparer.Ordinal)
    {
        "last", "latest", "newest", "recent", "recently", "lately",
    };

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "please", "hey", "hi", "ok", "okay", "can", "could", "would", "will", "you", "u", "i", "ive", "im", "id", "me", "my", "mine",
        "our", "we", "us", "the", "a", "an", "all", "any", "some", "of", "in", "on", "at", "to", "from", "for", "with", "by",
        "that", "this", "these", "those", "which", "what", "who", "whose", "is", "are", "was", "were", "be", "been", "have", "has",
        "had", "do", "did", "does", "and", "or", "file", "files", "one", "ones", "thing", "things", "stuff", "called", "named",
        "titled", "it", "its", "there", "here", "up", "out", "put", "store", "stored", "keep", "kept", "leave", "left", "just",
        "again", "also", "somewhere", "most", "into", "inside", "under", "during", "since", "ago", "like", "am", "folder",
        "folders", "directory", "directories", "anywhere", "whole", "entire", "opened", "open", "viewed", "used", "looked",
        "said", "meant", "told", "asked", "wanted", "typed", "wrote", "no", "not", "instead", "actually", "yeah", "yep", "sorry",
        "thanks", "thank", "already", "um", "uh", "pls",
    };

    // The places a request says it means the whole PC by: "on my computer".
    private static readonly HashSet<string> WholePc = new(StringComparer.Ordinal)
    {
        "computer", "pc", "laptop", "machine", "device", "drive", "disk", "disc", "harddrive",
    };

    private static readonly HashSet<string> ContentTriggers = new(StringComparer.Ordinal)
    {
        "mention", "mentions", "mentioning", "mentioned", "contain", "contains", "containing", "says", "saying", "say",
    };

    /// <summary>Reads <paramref name="request"/>.</summary>
    public RequestReading Read(string? request)
    {
        var words = RequestText.Words(request).ToList();
        var used = new bool[words.Count];
        var quoted = Quoted(request);

        var period = ReadPeriods(words, used);
        var folder = ReadFolder(words, used);
        MarkWholePc(words, used);
        var size = ReadSize(words, used);
        var content = ReadContent(words, used, quoted);
        var about = ReadAbout(words, used);
        var (type, plural, folders, hasNoun) = ReadType(words, used);
        var (order, byCreation, newest) = ReadOrder(words, used);
        var limit = ReadLimit(words, used, newest);
        MarkVerbs(words, used);
        var keywords = Keywords(words, used, quoted, content is not null);

        // "the last doc I made": one, the newest made. A plural or no noun at all keeps the usual count.
        if (limit is null && newest && hasNoun && !plural)
        {
            limit = 1;
        }

        return new RequestReading
        {
            Keywords = keywords,
            About = about && keywords.Count > 0,
            ContentPhrase = content,
            Type = type,
            Folders = folders && type is null,
            Period = period,
            ByCreation = byCreation,
            Order = order,
            Limit = limit,
            Folder = folder,
            Size = size,
        };
    }

    /// <summary>
    /// The query for <paramref name="reading"/>: its kind as extensions, its days, its place and size, its order, and its words in
    /// names (and inside files, for a topic). A request that names only an order ("my latest files") looks at the last 30 days.
    /// </summary>
    public FileSearchQuery ToQuery(RequestReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var period = reading.Period;
        if (!reading.HasCriterion && reading.Order is FileSearchOrder.ModifiedDescending or FileSearchOrder.CreatedDescending)
        {
            period = calendar.LastDays(30);
        }

        var range = period is { } days ? calendar.Range(days.First, days.Last) : default;
        var hasWords = reading.Keywords.Count > 0;
        var order = reading.Order ?? (hasWords
            ? FileSearchOrder.Relevance
            : reading.ByCreation ? FileSearchOrder.CreatedDescending : FileSearchOrder.ModifiedDescending);
        var images = reading.Type is { IsImages: true };
        var fileOnly = reading.Type is not null || reading.ContentPhrase is not null || !reading.Size.IsUnbounded || period is not null;
        return new FileSearchQuery(hasWords ? string.Join(' ', reading.Keywords.Select(Term)) : null)
        {
            MatchContents = reading.About,
            Filename = reading.Type is { IsScreenshot: true } ? "Screenshot" : null,
            ContentTerm = reading.ContentPhrase,
            Extensions = reading.Type?.Extensions ?? [],
            Types = reading.Folders ? [SearchResultItemType.Folder]
                : fileOnly || !hasWords ? [SearchResultItemType.File]
                : [SearchResultItemType.File, SearchResultItemType.Folder],
            Modified = reading.ByCreation ? default : range,
            Created = reading.ByCreation ? range : default,
            Folder = reading.Folder?.Path,
            Size = reading.Size,
            Order = order,
            MaxResultsPerType = Math.Min(
                reading.Limit ?? (images ? RecentMediaTemplate.DefaultCount : DefaultLimit),
                WindowsFileSearchService.MaxResultsPerType),
        };
    }

    // A word goes to the search as it is; a phrase in quotes, so its words are found together.
    private static string Term(string keyword) => keyword.Contains(' ', StringComparison.Ordinal) ? $"\"{keyword}\"" : keyword;

    // -- The days. --

    private (DateOnly First, DateOnly Last)? ReadPeriods(List<string> words, bool[] used)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            if (TryPeriodAt(words, i, out var length) is { } period)
            {
                for (var j = i; j < i + length; j++)
                {
                    used[j] = true;
                }

                // "from", "on", "in", "since" and "during" before the days only lead into them.
                if (i > 0 && words[i - 1] is "from" or "on" or "in" or "since" or "during" or "the")
                {
                    used[i - 1] = true;
                }

                return period;
            }
        }

        return null;
    }

    private (DateOnly First, DateOnly Last)? TryPeriodAt(List<string> words, int i, out int length)
    {
        length = 1;
        var word = words[i];
        var next = i + 1 < words.Count ? words[i + 1] : null;
        var third = i + 2 < words.Count ? words[i + 2] : null;
        var today = calendar.Today;

        if (Fuzzy.IsWord(word, "today") || word is "tonight")
        {
            return (today, today);
        }

        if (Fuzzy.IsWord(word, "yesterday"))
        {
            return calendar.Period("yesterday");
        }

        if (word == "this" && next is "morning" or "afternoon" or "evening")
        {
            length = 2;
            return (today, today);
        }

        if (word == "last" && next == "night")
        {
            length = 2;
            return calendar.Period("yesterday");
        }

        // "last week", and "last week's" (read as "last weeks").
        if (word is "this" or "last" or "past" && next is "week" or "month" or "year" or "weeks" or "months" or "years"
            && !(next.EndsWith('s') && third is not null && TryNumber(third, out _)))
        {
            length = 2;
            next = next.TrimEnd('s');
            return word == "past"
                ? next switch
                {
                    "week" => calendar.LastDays(7),
                    "month" => calendar.LastMonths(1),
                    _ => calendar.LastMonths(12),
                }
                : calendar.Period($"{word} {next}");
        }

        // "last 3 days", "past two weeks".
        if (word is "last" or "past" && next is not null && TryNumber(next, out var amount) && amount > 0 && Unit(third) is { } unit)
        {
            length = 3;
            return Span(unit, amount);
        }

        // "3 days ago", "a week ago", "two months ago": the day, week or month that far back.
        if (TryNumber(word, out var back) && back > 0 && Unit(next) is { } backUnit && third == "ago")
        {
            length = 3;
            return backUnit switch
            {
                "day" => (today.AddDays(-back), today.AddDays(-back)),
                "week" => WeekOf(today.AddDays(-7 * back)),
                "month" => MonthOf(today.AddMonths(-back)),
                _ => (new DateOnly(today.Year - back, 1, 1), new DateOnly(today.Year - back, 12, 31)),
            };
        }

        // "last tuesday", "on tuesday", "tuesday", "this monday", with room for a mistake ("tuseday").
        var weekdayAt = word is "last" or "this" or "on" && next is not null ? i + 1 : i;
        if (Weekday(words[weekdayAt]) is { } weekday)
        {
            length = weekdayAt - i + 1;
            if (word == "this")
            {
                var start = calendar.StartOfWeek(today);
                var day = start.AddDays(((int)weekday - (int)start.DayOfWeek + 7) % 7);
                return day <= today ? (day, day) : null;
            }

            var last = calendar.LastWeekday(weekday);
            return (last, last);
        }

        // "in september", "september 2025", "from march": a month needs a word before it or a year after it ("may" is a verb).
        if (Month(word) is { } month && ((i > 0 && words[i - 1] is "in" or "from" or "during" or "since" or "of") || (next is not null && Year(next) is not null)))
        {
            var year = next is not null && Year(next) is { } given ? given : today.Month >= month ? today.Year : today.Year - 1;
            length = next is not null && Year(next) is not null ? 2 : 1;
            var first = new DateOnly(year, month, 1);
            return (first, first.AddMonths(1).AddDays(-1));
        }

        // "in 2025", "from 2024": a year alone is a year only after such a word; "budget 2026" is a name.
        if (i > 0 && words[i - 1] is "in" or "from" or "during" && Year(word) is { } onlyYear)
        {
            return (new DateOnly(onlyYear, 1, 1), new DateOnly(onlyYear, 12, 31));
        }

        return null;
    }

    private (DateOnly First, DateOnly Last) Span(string unit, int amount) => unit switch
    {
        "day" => calendar.LastDays(amount),
        "week" => calendar.LastWeeks(amount),
        "month" => calendar.LastMonths(amount),
        _ => calendar.LastMonths(12 * amount),
    };

    private (DateOnly First, DateOnly Last) WeekOf(DateOnly day)
    {
        var start = calendar.StartOfWeek(day);
        return (start, start.AddDays(6));
    }

    private static (DateOnly First, DateOnly Last) MonthOf(DateOnly day)
    {
        var first = new DateOnly(day.Year, day.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    private static string? Unit(string? word) => word switch
    {
        "day" or "days" => "day",
        "week" or "weeks" => "week",
        "month" or "months" => "month",
        "year" or "years" => "year",
        _ => null,
    };

    private static DayOfWeek? Weekday(string word)
    {
        for (var day = 0; day < Weekdays.Length; day++)
        {
            if (Fuzzy.IsWord(word, Weekdays[day]))
            {
                return (DayOfWeek)day;
            }
        }

        return null;
    }

    private static int? Month(string word)
    {
        for (var month = 0; month < Months.Length; month++)
        {
            var name = Months[month];
            if (word == name || (name.Length >= 7 && Fuzzy.IsWord(word, name)) || (word.Length == 3 && word != "may" && word == name[..3] && word != "mar"))
            {
                return month + 1;
            }
        }

        return null;
    }

    private int? Year(string word) =>
        word.Length == 4 && word.All(char.IsAsciiDigit) && int.Parse(word, CultureInfo.InvariantCulture) is var year
        && year >= 1980 && year <= calendar.Today.Year
            ? year
            : null;

    // -- The place. --

    private (string Name, string Path)? ReadFolder(List<string> words, bool[] used)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            var word = words[i];
            var before = i > 0 ? words[i - 1] : null;
            var beforeThat = i > 1 ? words[i - 2] : null;
            var next = i + 1 < words.Count ? words[i + 1] : null;
            var leadsIn = before is "in" or "from" or "inside" or "under" or "on"
                || (before is "my" or "the" && beforeThat is "in" or "from" or "inside" or "under" or "on");
            var name = word switch
            {
                "downloads" => "downloads",
                "download" when next is "folder" || before is "my" || leadsIn => "downloads",
                "desktop" => "desktop",
                "documents" when leadsIn || next is "folder" => "documents",
                "pictures" when leadsIn || next is "folder" => "pictures",
                "music" when leadsIn || next is "folder" => "music",
                "videos" when leadsIn || next is "folder" => "videos",
                "screenshots" when next is "folder" => "screenshots",
                _ => null,
            };
            if (name is null || folders.Resolve(name) is not { } path)
            {
                continue;
            }

            used[i] = true;
            if (next is "folder")
            {
                used[i + 1] = true;
            }

            return (name, path);
        }

        return null;
    }

    // "on my computer", "on my pc", "anywhere on the laptop": the whole PC, which is where a search looks anyway.
    private static void MarkWholePc(List<string> words, bool[] used)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (!WholePc.Contains(words[i]) || used[i])
            {
                continue;
            }

            used[i] = true;

            // The words that lead into it, including a mistyped "my" ("myu").
            for (var j = i - 1; j >= 0 && j >= i - 3 && !used[j]; j--)
            {
                if (words[j] is "on" or "in" or "from" or "across" or "my" or "the" or "this" or "whole" or "entire" or "any" or "anywhere"
                    || (words[j].Length <= 3 && Fuzzy.Distance(words[j], "my", 1) <= 1))
                {
                    used[j] = true;
                }
                else
                {
                    break;
                }
            }
        }
    }

    // -- The size. --

    private static SizeRange ReadSize(List<string> words, bool[] used)
    {
        long? min = null;
        long? max = null;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            bool? atLeast = word switch
            {
                "bigger" or "larger" or "over" or "above" or "greater" or "more" or "exceeding" => true,
                "smaller" or "under" or "below" or "less" or "tinier" => false,
                _ => null,
            };
            if (atLeast is null)
            {
                continue;
            }

            var at = i + 1;
            if (at < words.Count && words[at] == "than")
            {
                at++;
            }

            if (at >= words.Count || !TryAmount(words, at, out var bytes, out var length))
            {
                continue;
            }

            for (var j = i; j < at + length; j++)
            {
                used[j] = true;
            }

            if (atLeast == true)
            {
                min = bytes;
            }
            else
            {
                max = bytes;
            }
        }

        return new SizeRange(min, max);
    }

    // "10 mb", "10mb", "1.5 gb", "500 kb": the number of bytes, and how many words it took.
    private static bool TryAmount(List<string> words, int at, out long bytes, out int length)
    {
        bytes = 0;
        length = 0;
        var match = Amount().Match(words[at]);
        if (!match.Success)
        {
            return false;
        }

        var unitText = match.Groups[2].Value;
        length = 1;
        if (unitText.Length == 0 && at + 1 < words.Count)
        {
            unitText = words[at + 1];
            length = 2;
        }

        long? scale = unitText switch
        {
            "kb" or "kilobytes" or "k" => 1024,
            "mb" or "megabytes" or "meg" or "megs" or "m" => 1024 * 1024,
            "gb" or "gigabytes" or "gig" or "gigs" or "g" => 1024L * 1024 * 1024,
            _ => null,
        };
        if (scale is null
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            || amount < 0 || amount > 100_000)
        {
            return false;
        }

        bytes = (long)Math.Round(amount * scale.Value);
        return true;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)([a-z]*)$")]
    private static partial Regex Amount();

    // -- What is inside the file. --

    private static string? ReadContent(List<string> words, bool[] used, IReadOnlyList<string> quoted)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var trigger = ContentTriggers.Contains(words[i]) ? 1
                : words[i] == "with" && i + 2 < words.Count && words[i + 1] == "the" && words[i + 2] is "words" or "word" or "text" or "phrase" ? 3
                : 0;
            if (trigger == 0 || used[i])
            {
                continue;
            }

            for (var j = i; j < i + trigger; j++)
            {
                used[j] = true;
            }

            if (i > 0 && words[i - 1] is "that" or "which")
            {
                used[i - 1] = true;
            }

            // A phrase in quotes is the phrase; otherwise the words that follow, up to the days or the end.
            var phrase = new List<string>();
            for (var j = i + trigger; j < words.Count && !used[j]; j++)
            {
                used[j] = true;
                phrase.Add(words[j]);
            }

            if (quoted.Count > 0)
            {
                return quoted[0];
            }

            while (phrase.Count > 0 && phrase[0] is "the" or "a" or "an")
            {
                phrase.RemoveAt(0);
            }

            return phrase.Count > 0 ? string.Join(' ', phrase) : null;
        }

        return null;
    }

    // "about biology", "on the topic of": the words after it are a topic, found in names or inside files.
    private static bool ReadAbout(List<string> words, bool[] used)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (!used[i] && words[i] is "about" or "regarding" or "concerning")
            {
                used[i] = true;
                return true;
            }
        }

        return false;
    }

    // -- The kind. --

    private static (FileType? Type, bool Plural, bool Folders, bool HasNoun) ReadType(List<string> words, bool[] used)
    {
        FileType? type = null;
        var plural = false;
        var folders = false;
        var hasNoun = false;
        for (var i = 0; i < words.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            var word = words[i];
            var next = i + 1 < words.Count ? words[i + 1] : null;

            // "screen shots", "word document", "text file", "excel sheet": two words for one kind.
            if (word == "screen" && next is "shot" or "shots")
            {
                used[i] = used[i + 1] = true;
                type ??= FileTypeWords.Screenshot;
                plural = next == "shots";
                hasNoun = true;
                continue;
            }

            if (word == "word" && next is not null && FileTypeWords.Read(next) is { Type: var following } && following == FileTypeWords.Document)
            {
                used[i] = true;
                type ??= FileTypeWords.WordDocument;
                continue;
            }

            if (word == "text" && next is "file" or "files")
            {
                used[i] = true;
                type ??= FileTypeWords.Text;
                continue;
            }

            // An everyday kind word followed by a word of the name is part of the name.
            if (EverydayKinds.Contains(word) && next is not null && !used[i + 1] && !Filler.Contains(next)
                && FileTypeWords.Read(next) is null && next is not ("file" or "files"))
            {
                continue;
            }

            if (FileTypeWords.Read(word) is { } read)
            {
                used[i] = true;
                if (type is null)
                {
                    type = read.Type;
                    plural = read.Plural;
                }

                hasNoun = true;
                continue;
            }

            if (word is "file" or "files")
            {
                hasNoun = true;
                plural |= word == "files" && type is null;
            }
            else if (word is "folder" or "folders" or "directory" or "directories")
            {
                folders = true;
                hasNoun = true;
                plural |= word is "folders" or "directories";
            }
        }

        return (type, plural, folders, hasNoun);
    }

    // -- The order. --

    private static (FileSearchOrder? Order, bool ByCreation, bool Newest) ReadOrder(List<string> words, bool[] used)
    {
        var byCreation = false;
        var changed = false;
        FileSearchOrder? order = null;
        var newest = false;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (MadeWords.Contains(word) && !used[i])
            {
                byCreation = true;
                used[i] = true;
            }
            else if (ChangedWords.Contains(word) && !used[i])
            {
                changed = true;
                used[i] = true;

                // "worked on": the "on" goes with it.
                if (word == "worked" && i + 1 < words.Count && words[i + 1] == "on")
                {
                    used[i + 1] = true;
                }
            }
            else if (used[i])
            {
                continue;
            }
            else if (NewestWords.Contains(word) || (word == "most" && i + 1 < words.Count && words[i + 1] == "recent"))
            {
                newest = true;
                used[i] = true;
            }
            else if (word is "oldest" or "earliest")
            {
                order = FileSearchOrder.ModifiedAscending;
                used[i] = true;
            }
            else if (word is "biggest" or "largest" or "heaviest")
            {
                order = FileSearchOrder.SizeDescending;
                used[i] = true;
            }
            else if (word is "smallest" or "tiniest")
            {
                order = FileSearchOrder.SizeAscending;
                used[i] = true;
            }
        }

        byCreation &= !changed;
        if (newest)
        {
            order ??= byCreation ? FileSearchOrder.CreatedDescending : FileSearchOrder.ModifiedDescending;
        }
        else if (order == FileSearchOrder.ModifiedAscending && byCreation)
        {
            order = FileSearchOrder.CreatedAscending;
        }

        return (order, byCreation, newest);
    }

    // "the last 5", "5 latest", "top 3", "my 3 newest videos": a small number next to the order or before the kind.
    private static int? ReadLimit(List<string> words, bool[] used, bool newest)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (used[i] || words[i] is "a" or "an" || !TryNumber(words[i], out var count) || count < 1 || count > WindowsFileSearchService.MaxResultsPerType)
            {
                continue;
            }

            var before = i > 0 ? words[i - 1] : null;
            var after = i + 1 < words.Count ? words[i + 1] : null;
            var nearOrder = (before is not null && (NewestWords.Contains(before) || before is "top" or "first" or "oldest" or "biggest" or "largest" or "smallest"))
                || (after is not null && (NewestWords.Contains(after) || after is "oldest" or "biggest" or "largest" or "smallest"));
            // "5 pdfs", "find 3 docs": a count. "milestone 3 doc" is a name with a number in it: a single kind noun after a number that
            // follows a word of the name is the end of the name, not how many were asked for.
            var afterKind = FileTypeWords.Read(after ?? "");
            var kindFollows = after is not null && (afterKind is not null || after is "files" or "file" or "folders" or "screen");
            var plural = after is "files" or "folders" || afterKind is { Plural: true };
            var leads = before is null || Filler.Contains(before) || FindVerbs.Contains(before) || Courtesies.Contains(before)
                || NewestWords.Contains(before) || before is "top" or "first" or "oldest" or "biggest" or "largest" or "smallest";
            var beforeKind = kindFollows && (plural || leads);
            if (nearOrder || beforeKind || newest)
            {
                used[i] = true;
                if (before == "top")
                {
                    used[i - 1] = true;
                }

                return count;
            }
        }

        return null;
    }

    private static bool TryNumber(string word, out int number)
    {
        if (NumberWords.TryGetValue(word, out number))
        {
            return true;
        }

        return word.Length <= 3 && word.All(char.IsAsciiDigit) && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    // -- The verbs, and what is left. --

    // A verb is read anywhere as it is written, and where a request's verb stands when mistyped ("fidn the", "can you serach"):
    // anywhere else a word one letter from a verb is more likely a name ("shoe" is one letter from "show").
    private static void MarkVerbs(List<string> words, bool[] used)
    {
        var verbAt = 0;
        while (verbAt < words.Count && Courtesies.Contains(words[verbAt]))
        {
            verbAt++;
        }

        for (var i = 0; i < words.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            if (AnywhereVerbs.Contains(words[i])
                || (i == verbAt && (FindVerbs.Contains(words[i]) || MistypableVerbs.Any(verb => Fuzzy.IsWord(words[i], verb)))))
            {
                used[i] = true;
            }
        }
    }

    private static List<string> Keywords(List<string> words, bool[] used, IReadOnlyList<string> quoted, bool quoteIsContent)
    {
        var keywords = new List<string>();
        if (!quoteIsContent)
        {
            keywords.AddRange(quoted.Take(MaxKeywords));
        }

        var inQuotes = new HashSet<string>(
            quoteIsContent ? [] : quoted.SelectMany(phrase => RequestText.Words(phrase)),
            StringComparer.Ordinal);
        for (var i = 0; i < words.Count && keywords.Count < MaxKeywords; i++)
        {
            var word = words[i];
            if (used[i] || Filler.Contains(word) || inQuotes.Contains(word))
            {
                continue;
            }

            if (!keywords.Contains(word, StringComparer.Ordinal))
            {
                keywords.Add(word);
            }
        }

        return keywords;
    }

    // What the request has in quotes, straight or curly.
    private static IReadOnlyList<string> Quoted(string? request)
    {
        if (string.IsNullOrEmpty(request))
        {
            return [];
        }

        return
        [
            .. QuotedText().Matches(request)
                .Select(match => RequestText.Clean(match.Groups[1].Value))
                .Where(phrase => phrase.Any(char.IsLetterOrDigit)),
        ];
    }

    [GeneratedRegex("[\"“”]([^\"“”]{1,100})[\"“”]")]
    private static partial Regex QuotedText();
}
