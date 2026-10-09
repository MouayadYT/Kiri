using System.Text.RegularExpressions;

namespace Assistant.UI.Messages;

/// <summary>
/// Reads the first message of a conversation that asks for a file and something about it in one go, so that the file can be found
/// instantly (with no model asked) and the rest asked about it: it splits a request that does both ("find the milestone doc, what is it
/// about?") and finds the file a question names ("summarize my milestone three doc" is "find milestone three doc"). It does not work out
/// what "it" means later in a conversation: that is for the model, which sees the whole conversation (PROJECT_SPEC §4.8). Nothing here
/// reads a file.
/// </summary>
internal static partial class DocumentCue
{
    // The nouns people use for a file, singular: the word after "the", "my", "this".
    private const string FileNoun =
        @"(?:doc(?:ument)?|docx|file|pdf|paper|essay|report|slides?|slideshow|deck|presentation|draft|notes?|text|page|article|assignment"
        + @"|rubric|guidelines?|handout|syllabus|worksheet|spreadsheet|sheet|attachment|contract|invoice|letter|resume|proposal|memo"
        + @"|transcript|thesis|chapter|readme|manual)";

    // A word of a name between "the" and the noun; never one that joins two things.
    private const string NameWord = @"(?!(?:and|or|then|but|of|in|on|for|to|with|that|which|about)\b)[\w'’-]+";

    private const string Ordinal =
        @"(?:the\s+)?(?:first|second|third|fourth|fifth|last|top|1st|2nd|3rd|4th|5th)\s+(?:one|file|doc(?:ument)?|result|pdf|item|match)";

    private const string Named = @"(?:the|this|that|my|our)\s+(?:" + NameWord + @"\s+){0,3}?" + FileNoun;

    // What a question may point at; which of the three it is tells how near the file must have been talked about.
    private const string Explicit = @"(?<named>" + Ordinal + "|" + Named + @")(?!\w)";
    private const string Pointer =
        @"(?:" + Explicit + @"|(?<it>(?:it|its)(?!\w))|(?<this>(?:this|that)(?!\w)))";

    // A reference that is a file by what it is, or "it": not "this" and "that", which are said of anything.
    private const string Likely = @"(?:" + Explicit + @"|(?<it>(?:it|its)(?!\w)))";

    // What can be done to a file's words.
    private const string Do =
        @"(?:summari[sz]e|summari[sz]ing|explain|describe|outline|analy[sz]e|review|proofread|critique|read|skim|simplify|paraphrase"
        + @"|condense|digest|break\s+down|go\s+(?:over|through)|look\s+(?:at|over|through)|sum\s+up|walk\s+me\s+through)";

    private const string Wants = @"(?:about|say|says|said|mean|means|contain|contains|cover|covers|discuss|discusses|include|includes"
        + @"|ask|asks|require|requires|want|wants|expect|expects|need|needs|talk\s+about)";

    private const string Question =
        // "summarize it", "read the document", "go through it", "summarize the main points of the file", "summarize its content".
        @"\b" + Do + @"\b(?:\s+(?:me|us|up|out|over|through|again|quickly|briefly|for\s+me))*\s+"
        + @"(?:(?:the\s+)?(?:contents?|text|gist|(?:main|key)\s+(?:points|ideas|takeaways))\s+(?:of|in|from)\s+)?" + Pointer
        // "what is it about", "what does the document say", "what does it say about the deadline".
        + @"|\bwhat(?:'s|’s|\s+is|\s+are|\s+does|\s+do|\s+did)\s+" + Pointer + @"\s+" + Wants
        + @"|\bwhat(?:'s|’s|\s+is|\s+are)\s+(?:in|inside)\s+" + Pointer
        + @"|\b(?:tell\s+me|talk|speak)\s+(?:more\s+)?about\s+" + Pointer
        // "give me a summary of it", "key points of the file".
        + @"|\b(?:(?:give|show|write|make|get)\s+(?:me|us)\s+(?:an?|the)\s+)?(?:(?:short|quick|brief|simple)\s+)?"
        + @"(?:summary|overview|gist|rundown|synopsis|recap|key\s+points|main\s+points|main\s+ideas)\s+(?:of|for|on|from|in)\s+" + Pointer
        // "what are the requirements in the document", "which deadlines are listed in it".
        + @"|\b(?:what|which|who|when|where|how\s+(?:many|much|long)|list|extract|quote|pull\s+out|name)\b[^.?!]{0,80}?\b(?:in|inside|from|within|of|on)\s+" + Likely
        // "does it mention a word count", "does the document say when it is due".
        + @"|\b(?:does|do|did)\s+" + Pointer + @"\s+(?:also\s+)?(?:mention|say|state|include|have|contain|cover|require|ask|specify|list|talk"
        + @"|discuss|describe|explain|define|use|need|want|expect|give)\b"
        + @"|\bis\s+there\s+(?:an?|any)\s+[^.?!]{0,60}?\b(?:in|inside)\s+" + Likely
        + @"|\bhow\s+(?:long|many\s+(?:pages|words|slides|sections|questions|parts))\s+(?:is|are|does)\s+" + Explicit
        + @"|\bhow\s+long\s+is\s+it\s*[?.!]*\s*$"
        + @"|\bwhen\s+is\s+" + Likely + @"\s+due\b";

    [GeneratedRegex(Question, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Cue();

    // Where a request turns from one thing to the next: a mark, or "and", "then", "also".
    [GeneratedRegex(@"[,;.!?]|\b(?:and|then|also)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.RightToLeft)]
    private static partial Regex Break();

    // "the milestone three doc": what is said of the file between "the" and what it is.
    [GeneratedRegex(@"^(?:the|this|that|my|our)\s+(?<name>.+?)\s+(?<noun>" + FileNoun + @")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedParts();

    // Words of a name that say nothing of which file it is.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "attached", "whole", "entire", "full", "same", "last", "latest", "new", "newest", "uploaded", "found", "first", "second",
        "third", "other", "previous", "above", "main", "current", "actual", "original", "recent", "this", "that", "one", "short",
        "long", "little", "small", "big", "quick", "brief", "fourth", "fifth", "bottom", "top", "1st", "2nd", "3rd", "4th", "5th",
    };

    /// <summary>Whether <paramref name="text"/> asks something about a file, such as "what is it about?" or "summarize the document".</summary>
    public static bool IsAboutTheFile(string? text) => !string.IsNullOrWhiteSpace(text) && Cue().IsMatch(text);

    // The words a question gives of the file's name and the noun it ends with, from the first reference that gives any.
    private static IReadOnlyList<string> NameOf(string? text, out string noun)
    {
        noun = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        foreach (Match cue in Cue().Matches(text))
        {
            if (!cue.Groups["named"].Success || NamedParts().Match(cue.Groups["named"].Value) is not { Success: true } parts)
            {
                continue;
            }

            var words = parts.Groups["name"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Trim(',', ';', '.', '!', '?', '"', '“', '”'))
                .Where(word => word.Length > 0 && !Generic.Contains(word))
                .ToArray();
            if (words.Length > 0)
            {
                noun = parts.Groups["noun"].Value;
                return words;
            }
        }

        return [];
    }

    /// <summary>
    /// Splits a request that asks to find a file and, after it, something about it ("find milestone 4, what is it about?") into the
    /// request to find and the question.
    /// </summary>
    /// <returns><see langword="true"/> when the request has both parts.</returns>
    public static bool TrySplit(string? request, out string search, out string question)
    {
        search = question = "";
        if (string.IsNullOrWhiteSpace(request) || Cue().Match(request) is not { Success: true } cue)
        {
            return false;
        }

        var before = request[..cue.Index];
        if (Break().Match(before) is not { Success: true } split)
        {
            return false;
        }

        search = before[..split.Index].Trim().TrimEnd(',', ';', '.', '!', '?', ' ');
        question = request[split.Index..].TrimStart(',', ';', '.', '!', '?', ' ').Trim();
        if (question.StartsWith("and ", StringComparison.OrdinalIgnoreCase)) question = question[4..];
        else if (question.StartsWith("then ", StringComparison.OrdinalIgnoreCase)) question = question[5..];
        else if (question.StartsWith("also ", StringComparison.OrdinalIgnoreCase)) question = question[5..];
        question = question.Trim();
        return search.Length > 0 && question.Length > 0;
    }

    /// <summary>
    /// The request to find the file that <paramref name="request"/> names while asking something about it ("summarize my milestone
    /// three doc" is "find milestone three doc"), or <see langword="null"/> when it names no file.
    /// </summary>
    public static string? FindOfNamed(string? request)
    {
        var words = NameOf(request, out var noun);
        return words.Count == 0 ? null : $"find {string.Join(' ', words)} {noun}";
    }
}
