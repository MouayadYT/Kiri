using System.Text;

namespace Assistant.Core.Voice;

/// <summary>How <see cref="SpeechSegmenter"/> cuts a streamed answer into pieces to say.</summary>
public sealed record SpeechSegmenterOptions
{
    /// <summary>
    /// The fewest letters and digits the first piece may have when it ends at a clause (a comma, a colon, a dash): a first piece that is
    /// short starts the voice sooner. A sentence end cuts the first piece whatever its length.
    /// </summary>
    public int FirstClauseMinChars { get; init; } = 14;

    /// <summary>When the answer has gone this many letters without any punctuation, the first piece is cut at a word, so the voice starts.</summary>
    public int FirstForceChars { get; init; } = 72;

    /// <summary>The fewest letters and digits a piece after the first needs before a clause may end it; sentences end the others.</summary>
    public int ClauseMinChars { get; init; } = 60;

    /// <summary>The fewest letters and digits a sentence after the first needs to be said on its own: a shorter one waits to join the next.</summary>
    public int SentenceMinChars { get; init; } = 22;

    /// <summary>The most characters in one piece: a longer run with no punctuation is cut at a word.</summary>
    public int MaxChars { get; init; } = 240;
}

/// <summary>
/// Cuts an answer that is still being written into natural pieces to say, as soon as each is whole (PROJECT_SPEC §4.2, step 125). The voice must start
/// while the model is still writing, and it sounds right only when it reads sentences and clauses, never half of one; so the first piece is cut as early
/// as a clause allows, and the rest at sentence ends. It reads Markdown as the answer is written: a fenced block of code is left out, a list item or a
/// heading is a piece of its own, and each piece is made plain (<see cref="SpeechTextCleaner"/>) before it is handed over.
/// </summary>
/// <remarks>It holds a few hundred characters at most, belongs to one answer and one thread at a time, and never keeps what it has handed over.</remarks>
public sealed class SpeechSegmenter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "inc", "ltd", "co", "no", "fig", "approx", "dept", "est", "vol", "mt",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec", "gen", "col", "capt", "lt", "sgt", "rev", "hon",
    };

    private readonly SpeechSegmenterOptions _options;
    private readonly StringBuilder _buffer = new();
    private bool _inFence;
    private bool _firstEmitted;

    /// <summary>Creates a segmenter with <paramref name="options"/>, or the defaults.</summary>
    public SpeechSegmenter(SpeechSegmenterOptions? options = null) => _options = options ?? new SpeechSegmenterOptions();

    /// <summary>How many pieces it has handed over.</summary>
    public int Emitted { get; private set; }

    /// <summary>Whether it holds text that has not been handed over yet.</summary>
    public bool HasPending => _buffer.Length > 0 && !_inFence;

    /// <summary>Adds the next words of the answer and returns the pieces that are whole now, in order. Usually none or one.</summary>
    public IReadOnlyList<string> Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        _buffer.Append(text);
        return Drain(final: false);
    }

    /// <summary>The answer has ended: returns whatever is left as the last piece, and starts over.</summary>
    public IReadOnlyList<string> Complete()
    {
        var rest = Drain(final: true);
        _buffer.Clear();
        _inFence = false;
        return rest;
    }

    /// <summary>Forgets everything, for another answer.</summary>
    public void Reset()
    {
        _buffer.Clear();
        _inFence = false;
        _firstEmitted = false;
        Emitted = 0;
    }

    private List<string> Drain(bool final)
    {
        var pieces = new List<string>();
        while (_buffer.Length > 0)
        {
            var text = _buffer.ToString();
            if (_inFence)
            {
                var close = IndexOfFence(text, 0);
                if (close < 0)
                {
                    // A closing fence may be split across two parts of the stream: keep a few characters to see it whole.
                    _buffer.Remove(0, Math.Max(0, text.Length - 4));
                    break;
                }

                var endOfLine = text.IndexOf('\n', close);
                if (endOfLine < 0)
                {
                    if (!final)
                    {
                        _buffer.Remove(0, close);
                        break;
                    }

                    _buffer.Clear();
                    _inFence = false;
                    break;
                }

                _buffer.Remove(0, endOfLine + 1);
                _inFence = false;
                continue;
            }

            var open = IndexOfFence(text, 0);
            var scan = open >= 0 ? text[..open] : text;
            var cut = FindCut(scan, final || open >= 0);
            if (cut > 0)
            {
                Take(pieces, text[..cut]);
                _buffer.Remove(0, cut);
                continue;
            }

            if (open >= 0)
            {
                // The code starts here; its words are not said.
                _buffer.Remove(0, open + 3);
                _inFence = true;
                continue;
            }

            break;
        }

        return pieces;
    }

    private void Take(List<string> pieces, string raw)
    {
        var spoken = SpeechTextCleaner.Clean(raw);
        if (spoken.Length > 0)
        {
            pieces.Add(spoken);
            Emitted++;
            _firstEmitted = true;
        }
    }

    // A fence is three backticks at the start of a line.
    private static int IndexOfFence(string text, int from)
    {
        for (var i = text.IndexOf("```", from, StringComparison.Ordinal); i >= 0; i = text.IndexOf("```", i + 1, StringComparison.Ordinal))
        {
            if (i == 0 || text[i - 1] == '\n' || IsOnlySpaceBefore(text, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsOnlySpaceBefore(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (text[i] == '\n')
            {
                return true;
            }

            if (text[i] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    // Where the first piece of <scan> ends (exclusive), or 0 when it does not end yet. A decision that needs the next character waits for it,
    // unless <final>: then everything is said.
    private int FindCut(string scan, bool final)
    {
        var length = scan.Length;
        var speakable = 0;
        var lastClause = -1;
        for (var i = 0; i < length; i++)
        {
            var c = scan[i];
            if (char.IsLetterOrDigit(c))
            {
                speakable++;
            }

            if (c == '\n')
            {
                if (speakable == 0)
                {
                    continue;
                }

                // A line, a heading or a list item is said by itself.
                return i + 1;
            }

            if (c is '.' or '!' or '?' or '…')
            {
                var end = i + 1;
                while (end < length && IsCloser(scan[end]))
                {
                    end++;
                }

                if (end >= length)
                {
                    return final ? length : 0;
                }

                if (!char.IsWhiteSpace(scan[end]))
                {
                    // 3.5, example.com, "...": still inside the sentence.
                    i = end - 1;
                    continue;
                }

                var next = end;
                while (next < length && scan[next] is ' ' or '\t')
                {
                    next++;
                }

                // Only spaces follow when the next word has not arrived: the sentence is judged without it (an abbreviation, an initial and a list
                // number do not need it), because waiting for it would only delay the voice.
                var nextKnown = next < length;
                if ((!nextKnown || scan[next] != '\n') && c == '.' && IsNotAnEnd(scan, i, next))
                {
                    i = end - 1;
                    continue;
                }

                if (!_firstEmitted || SpeakableCount(scan, end) >= _options.SentenceMinChars || (nextKnown && scan[next] == '\n'))
                {
                    return end;
                }

                // A short sentence is joined with the next, which sounds better than two small pieces.
                i = end - 1;
                continue;
            }

            if (c is ',' or ';' or ':' or '—' or '–')
            {
                if (i + 1 >= length)
                {
                    if (!final)
                    {
                        // 1,000 or 12:30 look the same until the next character is here.
                        break;
                    }
                }
                else if (char.IsWhiteSpace(scan[i + 1]))
                {
                    if (!_firstEmitted)
                    {
                        if (speakable >= _options.FirstClauseMinChars)
                        {
                            return i + 1;
                        }
                    }
                    else if (speakable >= _options.ClauseMinChars)
                    {
                        lastClause = i + 1;
                    }
                }
            }

            if (i >= _options.MaxChars)
            {
                if (lastClause > 0)
                {
                    return lastClause;
                }

                var space = scan.LastIndexOf(' ', i);
                return space > 0 ? space + 1 : i + 1;
            }
        }

        if (final)
        {
            return length;
        }

        if (!_firstEmitted && speakable >= _options.FirstForceChars)
        {
            var space = scan.LastIndexOf(' ');
            if (space > 0)
            {
                return space + 1;
            }
        }

        if (_firstEmitted && lastClause > 0 && speakable >= _options.ClauseMinChars * 2)
        {
            return lastClause;
        }

        return 0;
    }

    private static bool IsCloser(char c) => c is '"' or '\'' or ')' or ']' or '”' or '’' or '*' or '_' or '`' or '!' or '?' or '.';

    private static int SpeakableCount(string text, int end)
    {
        var count = 0;
        for (var i = 0; i < end; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                count++;
            }
        }

        return count;
    }

    // Whether the full stop at <dot>, followed by a word that starts at <next>, only ends an abbreviation, an initial, a list number or a
    // sentence that goes on in lower case.
    private static bool IsNotAnEnd(string text, int dot, int next)
    {
        var start = dot;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '.'))
        {
            start--;
        }

        var word = text[start..dot];
        if (word.Length == 0)
        {
            // A number: "1." at the start of a line is a list marker, and "3." followed by a lower case word is a continued sentence.
            var numberStart = dot;
            while (numberStart > 0 && char.IsDigit(text[numberStart - 1]))
            {
                numberStart--;
            }

            if (numberStart < dot && dot - numberStart <= 3 && IsOnlySpaceBefore(text, numberStart))
            {
                return true;
            }

            return next < text.Length && char.IsLower(text[next]);
        }

        if (Abbreviations.Contains(word) || (word.Contains('.', StringComparison.Ordinal) && word.Length <= 6))
        {
            return true;
        }

        if (word.Length == 1 && char.IsLetter(word[0]) && word[0] is not ('I' or 'A' or 'a') && (next >= text.Length || char.IsUpper(text[next])))
        {
            // An initial: "J. Smith".
            return true;
        }

        return next < text.Length && char.IsLower(text[next]);
    }
}
