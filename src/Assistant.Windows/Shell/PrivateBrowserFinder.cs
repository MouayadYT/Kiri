using Microsoft.Win32;

namespace Assistant.Windows.Shell;

/// <summary>Where <see cref="PrivateBrowserFinder"/> reads what Windows knows about browsers; replaced in tests.</summary>
internal interface IBrowserRegistry
{
    /// <summary>The ProgId of the user's default browser for <c>https</c> (<c>MSEdgeHTM</c>, <c>ChromeHTML</c>, <c>FirefoxURL-...</c>), or <see langword="null"/>.</summary>
    string? DefaultBrowserProgId();

    /// <summary>The command that opens a page for <paramref name="progId"/>, as registered (a quoted program path and its arguments), or <see langword="null"/>.</summary>
    string? OpenCommandOf(string progId);

    /// <summary>The full path registered for <paramref name="programFileName"/> (<c>msedge.exe</c>), or <see langword="null"/>.</summary>
    string? RegisteredPath(string programFileName);

    /// <summary>Whether a file is there.</summary>
    bool FileExists(string path);
}

/// <summary>The real registry.</summary>
internal sealed class WindowsBrowserRegistry : IBrowserRegistry
{
    /// <inheritdoc/>
    public string? DefaultBrowserProgId() => Read(
        Registry.CurrentUser, @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice", "ProgId");

    /// <inheritdoc/>
    public string? OpenCommandOf(string progId) => Read(Registry.ClassesRoot, progId + @"\shell\open\command", null);

    /// <inheritdoc/>
    public string? RegisteredPath(string programFileName)
    {
        var key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + programFileName;
        return Read(Registry.CurrentUser, key, null) ?? Read(Registry.LocalMachine, key, null);
    }

    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(path);

    private static string? Read(RegistryKey root, string subKey, string? name)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>A browser program and the switch that makes it open a page in a private window.</summary>
/// <param name="Executable">The program's full path.</param>
/// <param name="Switch">The switch: <c>--inprivate</c>, <c>--incognito</c>, <c>--private</c> or <c>-private-window</c>.</param>
internal sealed record PrivateBrowser(string Executable, string Switch);

/// <summary>
/// Finds a browser that can open a page in a private window (InPrivate, incognito), which has none of the accounts the user's everyday browser is signed in to. It is for
/// signing in to a service as another account than the one the browser already uses. The user's default browser is used when its program is one whose private switch is
/// known; otherwise Microsoft Edge (which comes with Windows 11), then Chrome, then Firefox. Only a switch of a program that is known is ever passed: nothing is guessed.
/// </summary>
internal static class PrivateBrowserFinder
{
    private static readonly string[] Fallbacks = ["msedge.exe", "chrome.exe", "firefox.exe"];

    /// <summary>The private-window switch for the program called <paramref name="fileName"/>, or <see langword="null"/> when it is not one that is known.</summary>
    public static string? SwitchFor(string fileName) => fileName.ToLowerInvariant() switch
    {
        "msedge.exe" => "--inprivate",
        "chrome.exe" or "brave.exe" or "vivaldi.exe" or "chromium.exe" or "comet.exe" or "browseros.exe" => "--incognito",
        "opera.exe" => "--private",
        "firefox.exe" or "librewolf.exe" or "waterfox.exe" => "-private-window",
        _ => null,
    };

    /// <summary>The program in a registered open command: <c>"C:\path\chrome.exe" --single-argument %1</c> gives the path, or <see langword="null"/> when there is none.</summary>
    public static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        string path;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
            {
                return null;
            }

            path = text[1..end];
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0)
            {
                return null;
            }

            path = text[..(exe + 4)];
        }

        return Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>The browser to open a private window in, or <see langword="null"/> when this PC has none that can.</summary>
    public static PrivateBrowser? Find(IBrowserRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.DefaultBrowserProgId() is { Length: > 0 } progId
            && ProgramOf(registry.OpenCommandOf(progId)) is { } program
            && SwitchFor(Path.GetFileName(program)) is { } own
            && registry.FileExists(program))
        {
            return new PrivateBrowser(program, own);
        }

        foreach (var name in Fallbacks)
        {
            if (registry.RegisteredPath(name)?.Trim('"') is { Length: > 0 } path && registry.FileExists(path) && SwitchFor(name) is { } found)
            {
                return new PrivateBrowser(path, found);
            }
        }

        return null;
    }
}
