namespace Assistant.Windows.Shell;

/// <summary>
/// Reads an internet shortcut (a <c>.url</c> file), which is how game launchers put their games on the Desktop and in Start: Epic's
/// <c>com.epicgames.launcher://apps/...</c>, Steam's <c>steam://rungameid/...</c>, Ubisoft's <c>uplay://launch/...</c>. Such a shortcut starts an
/// application, as PowerToys Run counts it; one to a web page (<c>http</c>, <c>https</c>) is a page, not an application. Nothing read is logged.
/// </summary>
internal static class InternetShortcut
{
    // An internet shortcut is a few lines of text; anything longer is not one.
    private const int MaxLength = 64 * 1024;

    // What a link to something other than an application starts with: pages, files and mail.
    private static readonly HashSet<string> NotApplicationSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ftp", "ftps", "file", "mailto", "news", "about", "javascript", "data",
    };

    /// <summary>Whether <paramref name="path"/> is an internet shortcut that starts an application, as a game launcher's is.</summary>
    public static bool StartsAnApplication(string path) => IsApplicationLink(ReadUrl(path));

    /// <summary>The address an internet shortcut points to (its <c>URL=</c> line), or <see langword="null"/> when it cannot be read.</summary>
    public static string? ReadUrl(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxLength)
            {
                return null;
            }

            var inShortcut = false;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inShortcut = string.Equals(line, "[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inShortcut && line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = line[4..].Trim();
                    return url.Length > 0 ? url : null;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A shortcut that cannot be read is not listed.
        }

        return null;
    }

    /// <summary>Whether <paramref name="url"/> starts an application: a link of its own kind, not a page, a file or mail.</summary>
    public static bool IsApplicationLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var colon = url.IndexOf(':', StringComparison.Ordinal);

        // A drive letter ("C:\...") is a file, not a scheme.
        if (colon < 2)
        {
            return false;
        }

        var scheme = url[..colon];
        return scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') && !NotApplicationSchemes.Contains(scheme);
    }
}
