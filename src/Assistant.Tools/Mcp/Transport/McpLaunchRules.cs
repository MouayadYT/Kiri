using System.Text.RegularExpressions;

namespace Assistant.Tools.Mcp;

/// <summary>
/// What a local MCP server may be started as (PROJECT_SPEC §4.8, P7, step 104). The program is one the code that installed the integration
/// named, started directly with its arguments given one by one, never through a shell, so nothing is ever interpreted as a command line.
/// The program must be an <c>.exe</c> on a local drive, and not a shell, a script host or a program that runs what it is given by name
/// (<c>cmd</c>, <c>powershell</c>, <c>pwsh</c>, <c>bash</c>, <c>wscript</c>, <c>mshta</c>, <c>rundll32</c> and the like), so that adding such a thing is
/// a change to this list, which is seen, and not a side effect of adding an integration. A runtime such as <c>node.exe</c> or <c>python.exe</c> is
/// fine: it is given the file to run as an argument, by whoever installed the integration. The model never writes any of this.
/// </summary>
internal static partial class McpLaunchRules
{
    /// <summary>The most arguments a program is given.</summary>
    public const int MaxArguments = 64;

    /// <summary>The longest argument or value, in characters.</summary>
    public const int MaxValueLength = 4096;

    /// <summary>The most environment variables an integration adds.</summary>
    public const int MaxEnvironmentVariables = 64;

    // Programs (their names without the extension) that run a command line, a script or a library function that they are given.
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "powershell_ise", "pwsh", "bash", "sh", "zsh", "fish", "dash", "wsl", "wslhost", "wscript", "cscript", "mshta",
        "rundll32", "regsvr32", "conhost", "forfiles", "pcalua", "msiexec", "cmstp", "installutil", "msbuild", "regasm", "regsvcs",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableName();

    /// <summary>
    /// What is wrong with a launch of <paramref name="command"/>, in words with no value in them, or <see langword="null"/> when it is fine. The file
    /// need not exist yet: it is looked for when the program is started.
    /// </summary>
    public static string? Problem(
        string? command,
        IReadOnlyList<string>? arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "The program to start is missing.";
        }

        if (command.Length > MaxValueLength || command.Any(char.IsControl) || !IsLocalFullPath(command))
        {
            return "The program must be given by its full path on a local drive.";
        }

        if (!string.Equals(Path.GetExtension(command), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return "The program must be an .exe file.";
        }

        if (Interpreters.Contains(Path.GetFileNameWithoutExtension(command)))
        {
            return "A shell or script host cannot be started as an integration.";
        }

        if (arguments is { Count: > MaxArguments })
        {
            return "Too many arguments.";
        }

        if (arguments is not null && arguments.Any(argument => argument is null || argument.Length > MaxValueLength || argument.Any(IsForbiddenCharacter)))
        {
            return "An argument is too long or has a control character in it.";
        }

        if (workingDirectory is not null
            && (workingDirectory.Length > MaxValueLength || workingDirectory.Any(char.IsControl) || !IsLocalFullPath(workingDirectory)))
        {
            return "The working folder must be given by its full path on a local drive.";
        }

        if (environment is not null)
        {
            if (environment.Count > MaxEnvironmentVariables)
            {
                return "Too many environment variables.";
            }

            foreach (var (name, value) in environment)
            {
                if (!IsValidVariableName(name) || value is null || value.Length > MaxValueLength || value.Any(IsForbiddenCharacter))
                {
                    return "An environment variable has a name or a value that is not accepted.";
                }
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="name"/> can name an environment variable.</summary>
    public static bool IsValidVariableName(string? name) => name is not null && VariableName().IsMatch(name);

    // A full path on a drive: not relative, not a network share, not a device path.
    private static bool IsLocalFullPath(string path) =>
        Path.IsPathFullyQualified(path)
        && path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] is '\\' or '/';

    // Control characters are refused, apart from a tab.
    private static bool IsForbiddenCharacter(char character) => char.IsControl(character) && character != '\t';
}
