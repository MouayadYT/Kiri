using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Tools.Mcp;

/// <summary>
/// Cleans text that a connected app wrote before a model reads it (PROJECT_SPEC §3.1 P9, step 104). A tool's description and the names of its
/// arguments are third-party text that goes into the prompt, so it is the way an app could try to talk to the model: it is cut to a length, kept to
/// one line of plain printable text, and loses the angle brackets that delimit the Assistant's own markers. It is still data, and the prompt says so.
/// </summary>
internal static partial class McpText
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex LowerUpper();

    [GeneratedRegex("([A-Z]+)([A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex UpperRun();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotWord();

    /// <summary>
    /// <paramref name="text"/> as one line of at most <paramref name="maxLength"/> characters: control characters and the angle brackets
    /// (<c>&lt;</c> and <c>&gt;</c>) become spaces, runs of space become one, and the ends are trimmed. <see langword="null"/> when nothing is left.
    /// </summary>
    public static string? Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength * 2));
        foreach (var character in text)
        {
            builder.Append(char.IsControl(character) || character is '<' or '>' ? ' ' : character);
            if (builder.Length >= maxLength * 2)
            {
                break;
            }
        }

        var cleaned = Whitespace().Replace(builder.ToString(), " ").Trim();
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// <paramref name="text"/> as lower snake_case of letters and digits: <c>getUser</c>, <c>get-user</c> and <c>get.user</c> are all <c>get_user</c>,
    /// and <c>HTTPServer</c> is <c>http_server</c>. Empty when no letter or digit is in it.
    /// </summary>
    public static string Snake(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var split = UpperRun().Replace(LowerUpper().Replace(text, "$1_$2"), "$1_$2");
        return NotWord().Replace(split.ToLowerInvariant(), "_").Trim('_');
    }
}
