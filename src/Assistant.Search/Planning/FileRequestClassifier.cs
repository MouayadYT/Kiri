namespace Assistant.Search.Planning;

/// <summary>
/// Decides, by fixed rules and never by asking the model, whether a request is clearly a request to find the user's own files
/// or pictures ("find the PDF about biology I edited last Tuesday", "where is my tax return pdf", "show me my photos from last
/// week") rather than a question for the model ("show me how to write a PDF parser", "find a picture of a sunset"). It leans
/// towards leaving a request to the model: what it accepts has a verb that asks to find or show, and a noun that is a file, or
/// a noun for media together with a sign that the media is the user's own.
/// </summary>
internal static class FileRequestClassifier
{
    /// <summary>Whether <paramref name="request"/> is clearly a request to find files.</summary>
    public static bool IsFileRequest(string? request)
    {
        var words = RequestText.Words(request);
        if (words.Count == 0)
        {
            return false;
        }

        var start = RequestGrammar.SkipLeading(words, out var hasVerb);

        // "What files did I edit yesterday", "which screenshots are from today": a question that asks for files by name.
        var asksWhich = start < words.Count && words[start] is "what" or "which";
        if (!hasVerb && !asksWhich)
        {
            return false;
        }

        if (start >= words.Count)
        {
            return false;
        }

        if (hasVerb && RequestGrammar.IsQuestionWord(words[start]))
        {
            // "show me how", "find out", "look for a way": not about files.
            return false;
        }

        var rest = words.Skip(start + (asksWhich ? 1 : 0)).ToArray();
        if (rest.Length == 0)
        {
            return false;
        }

        return NamesFiles(rest) || rest.Any(RequestGrammar.IsFileName) || NamesOwnMedia(rest);
    }

    // The verbs that ask to find, wherever they stand in what was said ("i said milestone three doc. FIND IT").
    private static readonly HashSet<string> FindAnywhere = new(StringComparer.Ordinal)
    {
        "find", "locate", "fetch", "search", "searching", "looking",
    };

    // Words that begin a question or a statement, not a name: a request that begins with one is not a bare file name.
    private static readonly HashSet<string> NotANameStart = new(StringComparer.Ordinal)
    {
        "what", "whats", "how", "why", "who", "whom", "when", "which", "is", "are", "was", "were", "does", "do", "did", "can", "could",
        "would", "will", "should", "tell", "explain", "summarize", "summarise", "describe", "write", "make", "create", "give",
        "translate", "define", "calculate", "i", "im", "ive", "my", "you", "we", "it", "that", "this", "there", "if", "so", "and",
    };

    // Words that say nothing of what a name holds, in a request that is only maybe about files.
    private static readonly HashSet<string> Empty = new(StringComparer.Ordinal)
    {
        "i", "im", "ive", "id", "me", "my", "mine", "you", "we", "it", "its", "the", "a", "an", "that", "this", "those", "these", "is", "are",
        "was", "were", "be", "to", "of", "in", "on", "for", "please", "can", "could", "would", "just", "again", "now", "said", "meant",
        "told", "asked", "wanted", "typed", "wrote", "no", "not", "instead", "actually", "yeah", "yep", "sorry", "thanks", "pls",
        "already", "so", "then", "and", "or", "but", "find", "locate", "fetch", "search", "searching", "looking", "look", "show", "get",
        "need", "want", "like", "one", "file", "files", "doc", "docs", "document", "documents", "folder", "folders",
    };

    /// <summary>
    /// Whether <paramref name="request"/> may be a request to find files although it is not clearly one: it asks to find somewhere in
    /// what was said ("i said milestone three doc. FIND IT", "find milsotne 3"), or it is only the name of a file and its kind
    /// ("milestone 3 doc"). Such a request is searched for, and is one only if the search finds something. A request that asks
    /// how, why or whether, or is a few words of a different task ("find out why", "find a way to"), is not.
    /// </summary>
    public static bool IsLikelyFileRequest(string? request)
    {
        var words = RequestText.Words(request);
        if (words.Count is 0 or > 14)
        {
            return false;
        }

        var content = words.Where(word => !Empty.Contains(word) && !IsMistypedAsk(word)).ToArray();
        if (content.Length is 0 or > 6 || content.Any(RequestGrammar.IsQuestionWord))
        {
            return false;
        }

        // "find" (or "locate") with a name after it, or before "it" as the last thing said.
        var verb = words.ToList().FindIndex(word => FindAnywhere.Contains(word) || IsMistypedAsk(word));
        if (verb >= 0)
        {
            return !(verb + 1 < words.Count && RequestGrammar.IsQuestionWord(words[verb + 1]));
        }

        // A name and the kind of file it is, and nothing else: "milestone 3 doc", "budget pdf".
        return words.Count is >= 2 and <= 5
            && !NotANameStart.Contains(words[0])
            && (RequestGrammar.IsFileNoun(words[^1]) && !RequestGrammar.IsMediaNoun(words[^1]) && words[^1] is not ("file" or "files" or "folder" or "folders" or "downloads"));
    }

    // Words that point back at something already talked about.
    private static readonly HashSet<string> BackReferences = new(StringComparer.Ordinal)
    {
        "that", "it", "this", "those", "them", "these", "same",
    };

    // The only other words a request that points back may have: asking words, and nothing that names something new.
    private static readonly HashSet<string> FollowUpWords = new(StringComparer.Ordinal)
    {
        "find", "show", "search", "locate", "look", "get", "fetch", "open", "bring", "pull", "give", "where", "is", "are", "was",
        "for", "me", "up", "can", "could", "would", "you", "please", "i", "need", "want", "again", "the", "a", "an", "my", "one",
        "file", "files", "folder", "now", "so", "ok", "okay", "then", "just", "try", "do",
    };

    /// <summary>
    /// Whether <paramref name="request"/> points back at files already asked for ("find that pdf", "show it again", "where is
    /// it"): a word that points back, and otherwise only asking words and kinds of file. A request that names anything new ("find
    /// the IT report") is a request of its own.
    /// </summary>
    public static bool IsFollowUp(string? request)
    {
        var words = RequestText.Words(request);
        if (words.Count == 0 || words.Count > 8 || !words.Any(BackReferences.Contains))
        {
            return false;
        }

        return words.All(word => BackReferences.Contains(word) || FollowUpWords.Contains(word) || RequestGrammar.IsFileNoun(word)
            || RequestGrammar.IsMistypedFileNoun(word) || FileTypeWords.IsExactWord(word) || IsMistypedAsk(word));
    }

    private static bool IsMistypedAsk(string word) => word.Length >= 4 && (Fuzzy.IsWord(word, "find") || Fuzzy.IsWord(word, "search"));

    // A file noun that is meant as files: not "this file" (something already in front of the user), and not "file manager".
    private static bool NamesFiles(IReadOnlyList<string> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (!RequestGrammar.IsFileNoun(words[i]) && !RequestGrammar.IsMistypedFileNoun(words[i]))
            {
                continue;
            }

            var pointsAtOne = i > 0 && words[i - 1] is "this" or "that";
            var isAnotherThing = i + 1 < words.Count && RequestGrammar.IsNotFilesFollower(words[i + 1]);
            if (!pointsAtOne && !isAnotherThing)
            {
                return true;
            }
        }

        return false;
    }

    // Images, photos, videos and music are files on this PC only when they are the user's own or recent ones.
    private static bool NamesOwnMedia(IReadOnlyList<string> words) =>
        words.Any(RequestGrammar.IsMediaNoun) && words.Any(RequestGrammar.IsPersonalSignal);
}
