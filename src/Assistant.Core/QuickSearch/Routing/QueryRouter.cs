namespace Assistant.Core.QuickSearch.Routing;

/// <summary>
/// The app's <see cref="IQueryRouter"/> (PROJECT_SPEC §4.1): decides by fixed rules, in this order, where what is typed goes.
/// <list type="number">
/// <item>Nothing typed: the categories are browsed (instant search).</item>
/// <item>The user chose to ask (Ctrl+Enter): it goes to the model, with its tools when it is about the user's files or screen.</item>
/// <item>Straightforward arithmetic, while a calculator is available: a deterministic calculation.</item>
/// <item>Words that clearly ask to find files (the planner's own rules): a structured file search.</item>
/// <item>A question or an instruction to an assistant ("how do I", "summarize", ending with a question mark), at least three words
/// long: to the model, with its tools when it is about the user's files or screen.</item>
/// <item>Long text (nine words or more): to the model.</item>
/// <item>Anything else, a name or a short command: instant search.</item>
/// </list>
/// It leans towards instant search, which costs nothing and which Enter can always leave for a question, so that a short name is never
/// taken for a question. It never uses the model, and nothing it is given is logged.
/// </summary>
public sealed class QueryRouter : IQueryRouter
{
    /// <summary>How many words make text long enough to be a message rather than a name.</summary>
    public const int LongTextWords = 9;

    private static readonly HashSet<string> QuestionStarters = new(StringComparer.Ordinal)
    {
        "what", "whats", "why", "how", "hows", "who", "whom", "whose", "when", "where", "wheres", "which", "is", "are", "was", "were",
        "am", "can", "could", "should", "would", "will", "shall", "do", "does", "did", "has", "have", "had", "explain", "tell",
        "describe", "define", "summarize", "summarise", "translate", "write", "draft", "compose", "compare", "suggest", "recommend",
        "teach", "solve", "calculate", "convert", "rewrite", "proofread", "paraphrase", "brainstorm", "analyze", "analyse", "review",
        "make", "create", "give", "list", "help", "generate",
    };

    // Starters that are as often a short command ("create folder", "list files", "help") as a request: they need more words.
    private static readonly HashSet<string> AmbiguousStarters = new(StringComparer.Ordinal)
    {
        "make", "create", "give", "list", "help", "generate", "review", "compare", "convert", "write",
    };

    // What the user's own files and screen are called, which an answer about them needs tools for.
    private static readonly HashSet<string> FileWords = new(StringComparer.Ordinal)
    {
        "file", "files", "document", "documents", "doc", "docs", "pdf", "pdfs", "folder", "folders", "download", "downloads",
        "spreadsheet", "spreadsheets", "presentation", "presentations", "slides", "powerpoint", "excel", "docx", "pptx",
        "picture", "pictures", "photo", "photos", "image", "images", "screenshot", "screenshots", "notes",
    };

    private static readonly HashSet<string> ScreenWords = new(StringComparer.Ordinal)
    {
        "screen", "screenshot", "screenshots", "window", "monitor",
    };

    // Words that make it the user's own: "my files", "the pdf I downloaded", "this document". ("the" does not: "what is the pdf format".)
    private static readonly HashSet<string> OwnWords = new(StringComparer.Ordinal)
    {
        "my", "mine", "this", "that", "these", "those", "latest", "last", "recent", "newest", "oldest", "attached", "selected",
        "current", "i", "ive", "our",
    };

    private readonly Func<string, bool>? _isFileRequest;
    private readonly Func<bool>? _calculatorAvailable;

    /// <summary>Creates the router.</summary>
    /// <param name="isFileRequest">
    /// Says whether words clearly ask to find the user's files (the file planner's rules, <c>IFileRequestService.IsFileRequest</c>); without
    /// it nothing is routed to a file search.
    /// </param>
    /// <param name="calculatorAvailable">
    /// Says whether there is a calculator to work out arithmetic with, such as a registered <c>calculate</c> tool. Without one, or while it
    /// says no, a sum is an ordinary query, which Enter asks the model as before.
    /// </param>
    public QueryRouter(Func<string, bool>? isFileRequest = null, Func<bool>? calculatorAvailable = null)
    {
        _isFileRequest = isFileRequest;
        _calculatorAvailable = calculatorAvailable;
    }

    /// <inheritdoc/>
    public QueryRoute Route(string? query, QueryRouteOptions? options = null)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0)
        {
            return new QueryRoute(QueryRouteKind.InstantSearch, QueryRouteReason.Empty);
        }

        var words = QuickSearchText.Words(text);
        if (options?.ForceAsk == true)
        {
            return new QueryRoute(NeedsTools(words) ? QueryRouteKind.AgentRequest : QueryRouteKind.DirectAnswer, QueryRouteReason.ForcedAsk);
        }

        if (_calculatorAvailable?.Invoke() == true && ArithmeticExpression.TryRead(text, out var expression))
        {
            return new QueryRoute(QueryRouteKind.Calculation, QueryRouteReason.Arithmetic) { Expression = expression };
        }

        if (_isFileRequest?.Invoke(text) == true)
        {
            return new QueryRoute(QueryRouteKind.FileSearch, QueryRouteReason.FileRequest);
        }

        if (IsQuestionOrInstruction(text, words))
        {
            return NeedsTools(words)
                ? new QueryRoute(QueryRouteKind.AgentRequest, QueryRouteReason.NeedsFilesOrScreen)
                : new QueryRoute(QueryRouteKind.DirectAnswer, QueryRouteReason.Question);
        }

        if (words.Count >= LongTextWords)
        {
            return NeedsTools(words)
                ? new QueryRoute(QueryRouteKind.AgentRequest, QueryRouteReason.NeedsFilesOrScreen)
                : new QueryRoute(QueryRouteKind.DirectAnswer, QueryRouteReason.LongText);
        }

        return new QueryRoute(QueryRouteKind.InstantSearch, QueryRouteReason.LooksLikeAName);
    }

    // A question word or an instruction verb first with enough words after it, or a question mark at the end of three or more words.
    private static bool IsQuestionOrInstruction(string text, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return false;
        }

        if (text.EndsWith('?') && words.Count >= 3)
        {
            return true;
        }

        if (!QuestionStarters.Contains(words[0]))
        {
            return false;
        }

        return words.Count >= (AmbiguousStarters.Contains(words[0]) ? 5 : 3);
    }

    // Whether answering takes the user's files or screen: they are named, and as the user's own ("my files", "the pdf I downloaded",
    // "what is on my screen"). "What is a pdf" names a file type and nothing of the user's.
    private static bool NeedsTools(IReadOnlyList<string> words)
    {
        var namesFiles = words.Any(FileWords.Contains);
        var namesScreen = words.Any(ScreenWords.Contains);
        if (!namesFiles && !namesScreen)
        {
            return false;
        }

        return words.Any(OwnWords.Contains);
    }
}
