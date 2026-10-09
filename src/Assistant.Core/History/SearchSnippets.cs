using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Core.Domain;

namespace Assistant.Core.History;

/// <summary>
/// Cuts the piece of a message that a search result shows: the words around the first match, on one line, with every
/// match in it marked. It only reads the text it is given and keeps none of it.
/// </summary>
public static partial class SearchSnippets
{
    /// <summary>The most characters a snippet's text has, not counting the ellipses at its ends.</summary>
    public const int DefaultMaxLength = 160;

    // The most matches a snippet marks; a word that fills the text would otherwise mark it all.
    private const int MaxMatches = 12;

    private const string Ellipsis = "…";
    private const CompareOptions Comparison = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    [GeneratedRegex(@"^[ \t]*(```|~~~)[^\n]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingMarks();

    [GeneratedRegex(@"^[ \t]*(?:[-*+]|\d{1,3}[.)])[ \t]+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ListMarks();

    [GeneratedRegex(@"\[([^\]\n]*)\]\([^)\n]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Links();

    [GeneratedRegex(@"\*\*|__|~~|`", RegexOptions.CultureInvariant)]
    private static partial Regex EmphasisMarks();

    /// <summary>
    /// Makes the snippet of <paramref name="text"/>, message <paramref name="messageId"/>, for a search for
    /// <paramref name="terms"/>, or returns <see langword="null"/> when none of the terms is in it. Words are matched
    /// without regard to case or accents, and a match at the start of a word is preferred over one inside it.
    /// </summary>
    /// <param name="messageId">The message the text belongs to.</param>
    /// <param name="text">The message's text, Markdown and all.</param>
    /// <param name="terms">The words and phrases searched for.</param>
    /// <param name="maxLength">About how many characters the snippet has.</param>
    public static SearchSnippet? Build(
        Guid messageId, string text, IReadOnlyList<SearchTerm> terms, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 20);
        if (terms.Count == 0)
        {
            return null;
        }

        var words = terms.Select(term => term.Text).ToArray();
        var flat = ConversationTitle.OneLine(ToPlainText(text));
        var first = FindAll(flat, words, wordStartsOnly: true, limit: 1);
        if (first.Count == 0)
        {
            first = FindAll(flat, words, wordStartsOnly: false, limit: 1);
        }

        if (first.Count == 0)
        {
            return null;
        }

        var (start, end) = Window(flat, first[0], maxLength);
        var window = flat[start..end];
        var matches = FindAll(window, words, wordStartsOnly: true, limit: MaxMatches);
        if (matches.Count == 0)
        {
            matches = FindAll(window, words, wordStartsOnly: false, limit: MaxMatches);
        }

        var leading = start > 0 ? Ellipsis : string.Empty;
        var trailing = end < flat.Length ? Ellipsis : string.Empty;
        var shifted = matches.Select(match => new TextMatch(match.Start + leading.Length, match.Length)).ToArray();
        return new SearchSnippet(messageId, leading + window + trailing, shifted);
    }

    /// <summary>The words of <paramref name="text"/> without the marks that make its headings, lists, emphasis, code and links.</summary>
    public static string ToPlainText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.ReplaceLineEndings("\n");
        text = CodeFence().Replace(text, string.Empty);
        text = HeadingMarks().Replace(text, string.Empty);
        text = ListMarks().Replace(text, string.Empty);
        text = Links().Replace(text, "$1");
        return EmphasisMarks().Replace(text, string.Empty);
    }

    /// <summary>Where each of <paramref name="words"/> is in <paramref name="text"/>, in order, without overlaps.</summary>
    /// <param name="text">The text to look in.</param>
    /// <param name="words">The words and phrases to look for.</param>
    /// <param name="wordStartsOnly">Whether only a match at the start of a word counts.</param>
    /// <param name="limit">The most matches to return.</param>
    public static IReadOnlyList<TextMatch> FindAll(string text, IReadOnlyList<string> words, bool wordStartsOnly, int limit)
    {
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        var found = new List<TextMatch>();
        foreach (var word in words)
        {
            if (word.Length == 0)
            {
                continue;
            }

            var position = 0;
            while (position < text.Length)
            {
                var index = compare.IndexOf(text.AsSpan(position), word, Comparison, out var length);
                if (index < 0)
                {
                    break;
                }

                var absolute = position + index;
                if (!wordStartsOnly || absolute == 0 || !char.IsLetterOrDigit(text[absolute - 1]))
                {
                    found.Add(new TextMatch(absolute, Math.Max(length, 1)));
                }

                position = absolute + Math.Max(length, 1);
            }
        }

        found.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<TextMatch>(found.Count);
        foreach (var match in found)
        {
            if (merged.Count > 0 && match.Start < merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = new TextMatch(last.Start, Math.Max(last.End, match.End) - last.Start);
            }
            else
            {
                merged.Add(match);
            }
        }

        return merged.Count > limit ? merged.GetRange(0, limit) : merged;
    }

    // The part of the text to show: about maxLength characters with the first match a quarter of the way in, moved to
    // whole words where there is a space near enough to do it.
    private static (int Start, int End) Window(string text, TextMatch first, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return (0, text.Length);
        }

        var start = Math.Clamp(first.Start - (maxLength / 4), 0, text.Length - maxLength);
        if (start > 0)
        {
            // Begin at a word, unless the match itself would then be cut off.
            var space = text.IndexOf(' ', start, Math.Min(24, text.Length - start));
            if (space >= 0 && space + 1 <= first.Start)
            {
                start = space + 1;
            }
            else if (char.IsLowSurrogate(text[start]))
            {
                start++;
            }
        }

        var end = Math.Min(text.Length, start + maxLength);
        if (end < text.Length)
        {
            var space = text.LastIndexOf(' ', end - 1, Math.Min(24, end - start));
            if (space > 0 && space >= first.End)
            {
                end = space;
            }
            else if (char.IsHighSurrogate(text[end - 1]))
            {
                end--;
            }
        }

        if (first.End > end)
        {
            end = Math.Min(text.Length, first.End);
        }

        return (start, end);
    }
}
