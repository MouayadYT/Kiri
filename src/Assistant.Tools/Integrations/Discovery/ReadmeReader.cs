using System.Text.RegularExpressions;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Reads a repository's README as data (PROJECT_SPEC §4.8, step 106): the tool names it lists, the keys it asks for, and what has to be installed to run it. The text is
/// the web's and so is untrusted; it is only searched for these few fixed shapes, nothing in it is followed, run or passed on as it is, and every
/// name taken from it is checked against the shape of a tool name.
/// </summary>
internal static partial class ReadmeReader
{
    private const int MaxTools = 60;
    private const int MaxSecrets = 8;

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex("`(?<name>[A-Za-z][A-Za-z0-9_.-]{2,63})`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeName();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]+(?:_[A-Z0-9]+)*_(?:API_KEY|ACCESS_TOKEN|API_TOKEN|CLIENT_SECRET|CLIENT_ID|SECRET|TOKEN|PASSWORD|KEY)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    /// <summary>
    /// The names of the tools the README lists: the names in code that stand in its "Tools" section (a list, a table or a heading per tool). A name must
    /// look like a tool's (snake_case, kebab-case, dotted or camelCase). None when the README has no such section.
    /// </summary>
    public static IReadOnlyList<string> ToolNames(string? markdown)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return names;
        }

        var sectionLevel = 0;
        var inFence = false;
        foreach (var raw in markdown.Split((char)10))
        {
            var line = raw.TrimEnd((char)13);
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                var level = heading.Groups["hashes"].Length;
                var isTools = heading.Groups["title"].Value.Contains("tool", StringComparison.OrdinalIgnoreCase);
                if (sectionLevel == 0 && isTools)
                {
                    sectionLevel = level;
                }
                else if (sectionLevel != 0 && level <= sectionLevel && !isTools)
                {
                    sectionLevel = 0;
                }
                else if (sectionLevel != 0 && level <= sectionLevel && isTools)
                {
                    sectionLevel = level;
                }

                if (sectionLevel == 0)
                {
                    continue;
                }
            }

            if (sectionLevel == 0)
            {
                continue;
            }

            foreach (Match match in CodeName().Matches(line))
            {
                var name = match.Groups["name"].Value;
                if (LooksLikeToolName(name) && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                    if (names.Count >= MaxTools)
                    {
                        return names;
                    }
                }
            }
        }

        return names;
    }

    /// <summary>The names of the keys, tokens and secrets the README asks the user to set (<c>TODOIST_API_TOKEN</c>); names only, at most eight.</summary>
    public static IReadOnlyList<string> SecretNames(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        return [.. SecretName().Matches(markdown).Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(MaxSecrets)];
    }

    /// <summary>What has to be on the PC to run what the README describes, judged from the commands it shows; <see cref="CandidateRuntime.Unknown"/> when it shows none it knows.</summary>
    public static CandidateRuntime RuntimeOf(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return CandidateRuntime.Unknown;
        }

        if (markdown.Contains("npx ", StringComparison.Ordinal) || markdown.Contains("npm install", StringComparison.Ordinal) || markdown.Contains("pnpm ", StringComparison.Ordinal))
        {
            return CandidateRuntime.NodeJs;
        }

        if (markdown.Contains("uvx ", StringComparison.Ordinal) || markdown.Contains("pip install", StringComparison.Ordinal) || markdown.Contains("pipx ", StringComparison.Ordinal))
        {
            return CandidateRuntime.Python;
        }

        if (markdown.Contains("docker run", StringComparison.Ordinal))
        {
            return CandidateRuntime.Container;
        }

        return markdown.Contains("dotnet ", StringComparison.Ordinal) ? CandidateRuntime.DotNet : CandidateRuntime.Unknown;
    }

    // A tool's name: lower-case words joined by _, - or ., or camelCase; not a file name or a plain word.
    private static bool LooksLikeToolName(string name)
    {
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.Any(character => char.IsAsciiLetterUpper(character)) && name.Any(character => character is '_' or '-' or '.'))
        {
            // CONSTANT_CASE is an environment variable, not a tool.
            return !name.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character is '_' or '-' or '.');
        }

        var hasSeparator = name.Any(character => character is '_' or '-' or '.');
        var hasInteriorCapital = name.Skip(1).Any(char.IsAsciiLetterUpper) && name.Any(char.IsAsciiLetterLower);
        return char.IsAsciiLetterLower(name[0]) && (hasSeparator || hasInteriorCapital);
    }
}
