namespace Assistant.Search.Planning;

/// <summary>
/// The fixed words a request to find files is made of: the courtesies and verbs that start it, and the nouns for what is
/// looked for. The rules that read a request share them, so "show me", "find" and "can you find" mean the same everywhere.
/// </summary>
internal static class RequestGrammar
{
    // Longest first, so "show me" is read before "show".
    private static readonly string[][] Courtesies =
    [
        ["i", "want", "you", "to"], ["i", "need", "you", "to"], ["go", "ahead", "and"], ["can", "you"], ["could", "you"],
        ["would", "you"], ["will", "you"], ["please"], ["hey"], ["hi"], ["ok"], ["okay"],
    ];

    private static readonly string[][] Verbs =
    [
        ["where", "did", "i", "put"], ["where", "did", "i", "save"], ["where", "did", "i", "store"],
        ["where", "did", "i", "keep"], ["where", "did", "i", "download"], ["where", "did", "i", "leave"],
        ["i", "am", "looking", "for"], ["im", "looking", "for"], ["i", "would", "like"], ["id", "like"],
        ["where", "is"], ["where", "are"], ["where", "was"], ["where", "were"],
        ["search", "for"], ["look", "for"], ["pull", "up"], ["bring", "up"],
        ["show", "me"], ["find", "me"], ["get", "me"], ["give", "me"], ["i", "need"], ["i", "want"],
        ["show"], ["find"], ["search"], ["locate"], ["list"], ["display"], ["fetch"], ["get"],
    ];

    // What a request says before what it is about, and never part of it.
    private static readonly HashSet<string> Determiners = new(StringComparer.Ordinal)
    {
        "the", "my", "mine", "our", "all", "some", "a", "an", "any", "of", "me", "those", "these", "every", "each",
    };

    // Words that turn a "find" into a question or a task that is not about files on this PC.
    private static readonly HashSet<string> QuestionWords = new(StringComparer.Ordinal)
    {
        "how", "why", "whether", "if", "when", "who", "out", "way", "ways", "reason", "meaning",
    };

    // Nouns that are files or places for them, whoever asks.
    private static readonly HashSet<string> FileNouns = new(StringComparer.Ordinal)
    {
        "file", "files", "folder", "folders", "directory", "directories", "document", "documents", "doc", "docs", "pdf",
        "pdfs", "docx", "xlsx", "pptx", "csv", "txt", "spreadsheet", "spreadsheets", "presentation", "presentations",
        "powerpoint", "powerpoints", "excel", "screenshot", "screenshots", "downloads",
    };

    // Words after "file" that make it something else: "file manager", "file format", "file system".
    private static readonly HashSet<string> NotFilesFollowers = new(StringComparer.Ordinal)
    {
        "manager", "managers", "format", "formats", "system", "systems", "explorer", "sharing", "hosting", "converter",
        "converters", "compression", "server", "servers", "transfer", "extension", "extensions", "type", "types",
    };

    // Nouns for media, which are files only when the request is about the user's own.
    private static readonly HashSet<string> MediaNouns = new(StringComparer.Ordinal)
    {
        "image", "images", "photo", "photos", "picture", "pictures", "pic", "pics", "video", "videos", "song", "songs",
        "music", "recording", "recordings", "audio",
    };

    // The extensions that make a word a file's name: "budget.xlsx".
    private static readonly HashSet<string> FileExtensions = new(StringComparer.Ordinal)
    {
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "csv", "md", "rtf", "odt", "zip", "png", "jpg", "jpeg",
        "gif", "bmp", "webp", "heic", "mp3", "mp4", "mov", "wav", "json", "xml",
    };

    // Words that say it is the user's own, or which ones: "my", "yesterday", "last", "I took".
    private static readonly HashSet<string> PersonalSignals = new(StringComparer.Ordinal)
    {
        "my", "mine", "yesterday", "today", "last", "latest", "recent", "recently", "newest", "took", "taken", "made",
        "saved", "downloaded", "recorded", "captured", "shot", "edited", "created", "modified", "uploaded", "opened",
    };

    /// <summary>
    /// The index of the first word of what is asked for: after any courtesies ("can you", "please") and the verb ("find",
    /// "show me", "where is"). When the request has no verb, the index is where its subject begins.
    /// </summary>
    /// <param name="words">The request's words.</param>
    /// <param name="hasVerb">Whether the request started with a verb.</param>
    public static int SkipLeading(IReadOnlyList<string> words, out bool hasVerb)
    {
        var index = 0;
        while (Match(words, index, Courtesies) is { } courtesy)
        {
            index += courtesy;
        }

        hasVerb = false;
        if (Match(words, index, Verbs) is { } verb)
        {
            index += verb;
            hasVerb = true;
        }
        else if (index < words.Count && IsMistypedVerb(words[index]))
        {
            // "fidn the pdf", "serach for": a verb typed with one mistake, where a request begins.
            index++;
            hasVerb = true;
            if (index < words.Count && words[index] is "for" or "me")
            {
                index++;
            }
        }

        return index;
    }

    /// <summary>Whether <paramref name="word"/> only points at a thing ("the", "my", "all") and is not part of what it is.</summary>
    public static bool IsDeterminer(string word) => Determiners.Contains(word);

    /// <summary>Whether <paramref name="word"/> turns the request into a question about something other than files.</summary>
    public static bool IsQuestionWord(string word) => QuestionWords.Contains(word);

    /// <summary>Whether <paramref name="word"/> is a file, a folder, a kind of document or a place files are kept.</summary>
    public static bool IsFileNoun(string word) => FileNouns.Contains(word);

    /// <summary>
    /// Whether <paramref name="word"/> is a file noun typed with a mistake or a letter too many ("pdff", "documnet", "spreadhseet").
    /// Pictures, videos and music are not: they are files only when they are the user's own (<see cref="IsMediaNoun"/>). Nor is a
    /// kind word as it is written ("zip", "sheet", "deck"): alone it is too often something else ("zip code", "cheat sheet").
    /// </summary>
    public static bool IsMistypedFileNoun(string word) =>
        !FileNouns.Contains(word) && !MediaNouns.Contains(word) && !FileTypeWords.IsExactWord(word)
        && FileTypeWords.Read(word) is { Type: var type }
        && (type.IsScreenshot || !(type.IsImages || type == FileTypeWords.Video || type == FileTypeWords.Audio));

    /// <summary>Whether <paramref name="word"/> is a word that, after a file noun, makes it another thing (<c>file manager</c>).</summary>
    public static bool IsNotFilesFollower(string word) => NotFilesFollowers.Contains(word);

    /// <summary>Whether <paramref name="word"/> is a file's name with a known extension: <c>budget.xlsx</c>.</summary>
    public static bool IsFileName(string word)
    {
        var dot = word.LastIndexOf('.');
        return dot > 0 && dot < word.Length - 1 && FileExtensions.Contains(word[(dot + 1)..]);
    }

    /// <summary>Whether <paramref name="word"/> names media: images, photos, videos, music.</summary>
    public static bool IsMediaNoun(string word) => MediaNouns.Contains(word);

    /// <summary>Whether <paramref name="word"/> says that what is asked for is the user's own: "my", "last", "yesterday", "took".</summary>
    public static bool IsPersonalSignal(string word) => PersonalSignals.Contains(word);

    // The verbs a request begins with, long enough to be read when mistyped; "show" is not, as "shoe" and "shot" are one letter off.
    private static bool IsMistypedVerb(string word) =>
        word.Length >= 3 && MistypableVerbs.Any(verb => word != verb && Fuzzy.IsWord(word, verb));

    private static readonly string[] MistypableVerbs = ["find", "search", "locate", "fetch", "display"];

    // How many words of the phrase that matches at the index, or null; phrases are tried in the order they are listed.
    private static int? Match(IReadOnlyList<string> words, int index, string[][] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (index + phrase.Length > words.Count)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < phrase.Length; i++)
            {
                if (!string.Equals(words[index + i], phrase[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return phrase.Length;
            }
        }

        return null;
    }
}
