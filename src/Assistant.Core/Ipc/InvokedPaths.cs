namespace Assistant.Core.Ipc;

/// <summary>
/// Writes the paths File Explorer and other senders give in one form (PROJECT_SPEC §4.4, §5.7), so that the same file named two
/// ways is one file: quotes and outer space dropped, <c>/</c> as <c>\</c>, the long-path prefix taken off an ordinary path
/// (<c>\\?\C:\a</c> is <c>C:\a</c>, <c>\\?\UNC\server\share\a</c> is <c>\\server\share\a</c>), dots resolved and no trailing
/// separator. What is not an ordinary full path on a drive or a share, such as a relative path, another device path or a path with
/// a stream name (<c>plan.docx:notes.txt</c>), is not a path. Nothing is read from the file system.
/// </summary>
public static class InvokedPaths
{
    private static readonly char[] InvalidCharacters = System.IO.Path.GetInvalidPathChars();

    /// <summary>The normal form of <paramref name="path"/>, or <see langword="null"/> when it is not an ordinary full path.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var text = path.Trim().Trim('"').Trim().Replace('/', '\\');
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            text = @"\\" + text[@"\\?\UNC\".Length..];
        }
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal)
                 && text.Length >= 7 && char.IsAsciiLetter(text[4]) && text[5] == ':' && text[6] == '\\')
        {
            text = text[4..];
        }

        if (text.Length == 0
            || text.Length > InvocationProtocol.MaxPathLength
            || text.IndexOfAny(InvalidCharacters) >= 0
            || !System.IO.Path.IsPathFullyQualified(text)
            || text.IndexOf(':', 2) >= 0
            || text.StartsWith(@"\\?\", StringComparison.Ordinal)
            || text.StartsWith(@"\\.\", StringComparison.Ordinal)
            || text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(text);
            var root = System.IO.Path.GetPathRoot(full) ?? "";
            return full.Length > root.Length ? full.TrimEnd('\\') : full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
