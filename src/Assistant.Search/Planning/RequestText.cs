using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Search.Planning;

/// <summary>
/// A request to find files, read into lower case words. The planner's rules and the classifier work on these words, never on
/// the raw text, so spacing, case, apostrophes and closing punctuation do not change what a request means.
/// </summary>
internal static partial class RequestText
{
    /// <summary>The most characters of a request that are read; a longer one is not a request to find a file.</summary>
    public const int MaxLength = 300;

    /// <summary>The most words of a request that are read.</summary>
    public const int MaxWords = 40;

    /// <summary>The request on one line: blanks collapsed, control characters gone, cut to <see cref="MaxLength"/>.</summary>
    public static string Clean(string? request)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            return "";
        }

        var text = new StringBuilder(Math.Min(request.Length, MaxLength));
        foreach (var character in request)
        {
            text.Append(char.IsControl(character) || char.IsWhiteSpace(character) ? ' ' : character);
        }

        var collapsed = Spaces().Replace(text.ToString(), " ").Trim();
        if (collapsed.Length <= MaxLength)
        {
            return collapsed;
        }

        var cut = char.IsHighSurrogate(collapsed[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return collapsed[..cut].TrimEnd();
    }

    /// <summary>
    /// The words of <paramref name="request"/> in lower case: runs of letters and digits, which may hold a dot, hyphen or
    /// underscore between them (<c>budget.xlsx</c>); apostrophes are dropped (<c>i've</c> is <c>ive</c>).
    /// </summary>
    public static IReadOnlyList<string> Words(string? request)
    {
        var clean = Clean(request).Replace("'", "", StringComparison.Ordinal).Replace("’", "", StringComparison.Ordinal);
        var words = new List<string>();
        foreach (Match match in Word().Matches(clean))
        {
            words.Add(match.Value.ToLowerInvariant());
            if (words.Count == MaxWords)
            {
                break;
            }
        }

        return words;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[._\-][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
