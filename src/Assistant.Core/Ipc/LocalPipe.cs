using System.IO.Pipes;

namespace Assistant.Core.Ipc;

/// <summary>Conventions shared by the Assistant's named pipes (PROJECT_SPEC §3.4, §5.7).</summary>
public static class LocalPipe
{
    /// <summary>
    /// Options for both ends of a pipe: asynchronous I/O, and only the current Windows user may connect, or be
    /// connected to.
    /// </summary>
    public const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    /// <summary>The longest name <see cref="IsValidName"/> accepts.</summary>
    public const int MaxNameLength = 128;

    /// <summary>
    /// Returns a name no other pipe has, for a pipe that lives only as long as one connection: <paramref name="prefix"/>
    /// followed by a random suffix, so the name cannot be guessed ahead of time.
    /// </summary>
    public static string CreateUniqueName(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var name = $"{prefix}.{Guid.NewGuid():N}";
        if (!IsValidName(name))
        {
            throw new ArgumentException(
                "The prefix must be a short name of letters, digits, dots, dashes and underscores.", nameof(prefix));
        }

        return name;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a plain pipe name: 1 to <see cref="MaxNameLength"/> ASCII letters, digits,
    /// dots, dashes or underscores, with no path.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
