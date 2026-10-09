using Assistant.Core.Ipc;

namespace Assistant.ExplorerExtension;

/// <summary>
/// The paths the entry point sends, in the form the app expects (PROJECT_SPEC §4.4, §5.7): <see cref="InvokedPaths"/>'s normal
/// form, with an 8.3 short name (<c>PROGRA~1</c>) written out in full by Windows, so that a file selected through two routes is one
/// path. A path Windows cannot expand is sent as it is.
/// </summary>
internal static class ExplorerPaths
{
    /// <summary>The normal form of <paramref name="path"/> with its short names expanded, or <see langword="null"/> when it is not an ordinary full path.</summary>
    public static string? Prepare(string? path) => Prepare(path, ExpandShortNames);

    /// <summary>As <see cref="Prepare(string?)"/>, with the expansion of short names given.</summary>
    public static string? Prepare(string? path, Func<string, string?> expand)
    {
        ArgumentNullException.ThrowIfNull(expand);
        if (InvokedPaths.Normalize(path) is not { } normal)
        {
            return null;
        }

        // Only a name with a tilde can be a short name; asking for others would only touch the disk.
        return normal.Contains('~') && expand(normal) is { Length: > 0 } expanded
            ? InvokedPaths.Normalize(expanded) ?? normal
            : normal;
    }

    private static string? ExpandShortNames(string path)
    {
        var buffer = new char[path.Length + 64];
        var length = NativeMethods.GetLongPathName(path, buffer, buffer.Length);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = NativeMethods.GetLongPathName(path, buffer, buffer.Length);
        }

        return length > 0 && length <= buffer.Length ? new string(buffer, 0, length) : null;
    }
}
