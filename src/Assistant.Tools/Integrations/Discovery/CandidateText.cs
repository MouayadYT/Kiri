using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Cleans what a place on the web says before the Assistant keeps, shows or passes it on (PROJECT_SPEC §4.8, step 106; P9: web text is data, never instructions). A text
/// becomes one line of printable characters, cut to a length; an address must be <c>https</c> without credentials; nothing is trusted to be
/// what it says it is.
/// </summary>
internal static partial class CandidateText
{
    /// <summary>The longest address that is kept.</summary>
    public const int MaxUrlLength = 300;

    [GeneratedRegex(@"(?<![\p{L}])official(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfficialWord();

    [GeneratedRegex(@"^(?:(?:git\+)?(?:https?|git|ssh)://(?:git@)?|git@)github\.com[/:](?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100}?)(?:\.git)?(?:/.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRepository();

    /// <summary>One line: control characters and runs of blanks become one space, the text is trimmed and cut to <paramref name="maxLength"/>; <see langword="null"/> when nothing is left.</summary>
    public static string? Line(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var lastSpace = true;
        foreach (var character in text)
        {
            var blank = char.IsControl(character) || char.IsWhiteSpace(character) || IsInvisible(character);
            if (blank)
            {
                if (!lastSpace)
                {
                    builder.Append(' ');
                    lastSpace = true;
                }
            }
            else
            {
                builder.Append(character);
                lastSpace = false;
            }

            if (builder.Length > maxLength + 1)
            {
                break;
            }
        }

        var line = builder.ToString().Trim();
        if (line.Length > maxLength)
        {
            var end = char.IsHighSurrogate(line[maxLength - 1]) ? maxLength - 1 : maxLength;
            line = line[..end].TrimEnd() + "…";
        }

        return line.Length == 0 ? null : line;
    }

    // Characters that print nothing and break a line or join words: line and paragraph separators, a zero-width space, a byte-order mark.
    private static bool IsInvisible(char character) => character == (char)0x2028 || character == (char)0x2029 || character == (char)0x200B || character == (char)0xFEFF;

    /// <summary>An address worth keeping: absolute, <c>https</c>, no user name, password or fragment, no blanks, at most <see cref="MaxUrlLength"/> characters; otherwise <see langword="null"/>.</summary>
    public static string? Https(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength || url.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment) && uri.Host.Contains('.', StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>The GitHub repository an address names (a git address in any of its forms), as <c>https://github.com/owner/repo</c>; <see langword="null"/> when it is not one.</summary>
    public static string? GitHubRepositoryUrl(string? url) =>
        TryGitHubRepository(url, out var owner, out var repo) ? "https://github.com/" + owner + "/" + repo : null;

    /// <summary>Reads a GitHub repository's owner and name from an address.</summary>
    public static bool TryGitHubRepository(string? url, out string owner, out string repo)
    {
        owner = repo = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
        {
            return false;
        }

        var match = GitHubRepository().Match(url.Trim());
        if (!match.Success)
        {
            return false;
        }

        owner = match.Groups["owner"].Value;
        repo = match.Groups["repo"].Value;
        return repo.Length > 0 && repo is not ("." or "..");
    }

    /// <summary>Whether a text says its subject is official ("the official Todoist MCP server") and not "unofficial".</summary>
    public static bool ClaimsOfficial(string? text) => !string.IsNullOrEmpty(text) && OfficialWord().IsMatch(text);

    /// <summary>A package or image name: no blanks or control characters, at most 200 characters; otherwise <see langword="null"/>.</summary>
    public static string? Identifier(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Length > 200 || text.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ? null : text;

    /// <summary>A word for a search: lower-case letters and digits only, 2 to 30 of them; otherwise <see langword="null"/>.</summary>
    public static string? SearchWord(string? text)
    {
        var word = new string([.. (text ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);
        return word.Length is >= 2 and <= 30 ? word : null;
    }
}
