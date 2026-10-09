using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Assistant.BrowserBridge.Registration;

/// <summary>A Chromium browser the host can be registered for, by where it looks for native-messaging hosts in the user's registry.</summary>
/// <param name="Name">The browser's name, for tests and messages.</param>
/// <param name="HostsKey">The key under <c>HKCU\Software</c> that holds the hosts, such as <c>Google\Chrome\NativeMessagingHosts</c>.</param>
internal sealed record ChromiumBrowser(string Name, string HostsKey)
{
    /// <summary>The browsers the Assistant's extension is made for (PROJECT_SPEC §4.5): Edge, Chrome and Brave, and Chromium itself.</summary>
    public static IReadOnlyList<ChromiumBrowser> Supported { get; } =
    [
        new("Edge", @"Microsoft\Edge\NativeMessagingHosts"),
        new("Chrome", @"Google\Chrome\NativeMessagingHosts"),
        new("Brave", @"BraveSoftware\Brave-Browser\NativeMessagingHosts"),
        new("Chromium", @"Chromium\NativeMessagingHosts"),
    ];
}

/// <summary>
/// Adds and removes the native-messaging host's registration for the current user (PROJECT_SPEC §4.5, §5.7). A Chromium browser finds a
/// host through a registry key named for it, under the browser's own key in the user's registry, whose value is the full path of a JSON
/// manifest; the manifest names the host's executable and the extensions that may start it. Both are written for the current user, so no
/// administrator is needed and nothing is installed system-wide. The manifest is a file in the app's own folder
/// (<c>AppPaths.BrowserBridgeDirectory</c>); the keys are written for every <see cref="ChromiumBrowser"/>, installed or not, so a browser
/// installed later finds the host too.
/// </summary>
internal sealed partial class NativeHostRegistration
{
    /// <summary>
    /// The host's name, which the extension names when it connects. Chromium allows lowercase letters, digits, underscores and dots,
    /// with no dot at either end or two in a row.
    /// </summary>
    public const string HostName = "assistant.browser_bridge";

    /// <summary>What the manifest says the host is for.</summary>
    public const string Description = "Carries the text selected in the browser to the Assistant app on this PC.";

    /// <summary>The manifest's file name, in the manifest folder.</summary>
    public const string ManifestFileName = HostName + ".json";

    private readonly RegistryKey _software;
    private readonly string _manifestDirectory;
    private readonly IReadOnlyList<ChromiumBrowser> _browsers;

    /// <summary>Registers under <paramref name="software"/>: the user's <c>Software</c> key, or a test's own.</summary>
    /// <param name="software">The registry key the browsers' keys are under.</param>
    /// <param name="manifestDirectory">The folder the manifest file is written in.</param>
    /// <param name="browsers">The browsers to register for; all supported ones by default.</param>
    public NativeHostRegistration(RegistryKey software, string manifestDirectory, IReadOnlyList<ChromiumBrowser>? browsers = null)
    {
        _software = software ?? throw new ArgumentNullException(nameof(software));
        if (string.IsNullOrWhiteSpace(manifestDirectory) || !Path.IsPathFullyQualified(manifestDirectory))
        {
            throw new ArgumentException("The manifest folder must be a full path.", nameof(manifestDirectory));
        }

        _manifestDirectory = manifestDirectory;
        _browsers = browsers ?? ChromiumBrowser.Supported;
    }

    /// <summary>The full path of the manifest file.</summary>
    public string ManifestPath => Path.Combine(_manifestDirectory, ManifestFileName);

    /// <summary>The origin a browser knows an extension by: <c>chrome-extension://&lt;id&gt;/</c>.</summary>
    public static string OriginOf(string extensionId) => $"chrome-extension://{extensionId}/";

    /// <summary>Whether <paramref name="extensionId"/> is an extension id: 32 letters from a to p.</summary>
    public static bool IsExtensionId(string? extensionId) => extensionId is not null && ExtensionIdPattern().IsMatch(extensionId);

    /// <summary>
    /// The manifest's JSON for the host at <paramref name="executable"/>, which only the extensions <paramref name="extensionIds"/> may start.
    /// </summary>
    public static string ManifestJson(string executable, IEnumerable<string> extensionIds)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", HostName);
            writer.WriteString("description", Description);
            writer.WriteString("path", executable);
            writer.WriteString("type", "stdio");
            writer.WriteStartArray("allowed_origins");
            foreach (var id in extensionIds)
            {
                writer.WriteStringValue(OriginOf(id));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(buffer.ToArray());
    }

    /// <summary>
    /// Writes the manifest for the host at <paramref name="executable"/> and points each browser at it. Running it again with the same
    /// arguments changes nothing; with others, it replaces what was there.
    /// </summary>
    /// <param name="executable">The full path of the host.</param>
    /// <param name="extensionIds">The ids of the extensions allowed to start it, at least one.</param>
    /// <exception cref="ArgumentException">The path is not a full path, or an id is not an extension id.</exception>
    public void Register(string executable, IEnumerable<string> extensionIds)
    {
        ArgumentNullException.ThrowIfNull(extensionIds);
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("The host must be a full path.", nameof(executable));
        }

        var ids = extensionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0 || ids.Any(id => !IsExtensionId(id)))
        {
            throw new ArgumentException("Each extension id must be 32 letters from a to p, and at least one is needed.", nameof(extensionIds));
        }

        Directory.CreateDirectory(_manifestDirectory);
        WriteAtomically(ManifestPath, ManifestJson(executable, ids));
        foreach (var browser in _browsers)
        {
            using var key = _software.CreateSubKey($@"{browser.HostsKey}\{HostName}", writable: true);
            key.SetValue("", ManifestPath);
        }
    }

    /// <summary>
    /// Removes the manifest and each browser's key for the host, and the keys above them that held nothing else. Other hosts' keys are
    /// left alone.
    /// </summary>
    public void Unregister()
    {
        foreach (var browser in _browsers)
        {
            _software.DeleteSubKey($@"{browser.HostsKey}\{HostName}", throwOnMissingSubKey: false);
            RemoveEmptyParents(browser.HostsKey);
        }

        File.Delete(ManifestPath);
    }

    /// <summary>Whether every browser points at this manifest and the manifest file is there.</summary>
    public bool IsRegistered()
    {
        if (!File.Exists(ManifestPath))
        {
            return false;
        }

        foreach (var browser in _browsers)
        {
            using var key = _software.OpenSubKey($@"{browser.HostsKey}\{HostName}");
            if (key?.GetValue("") is not string value || !string.Equals(value, ManifestPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // A browser's keys may exist only because the host was registered for it: remove them while they hold nothing.
    private void RemoveEmptyParents(string hostsKey)
    {
        var path = hostsKey;
        while (path.Length > 0)
        {
            using (var key = _software.OpenSubKey(path))
            {
                if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
                {
                    return;
                }
            }

            _software.DeleteSubKey(path, throwOnMissingSubKey: false);
            var cut = path.LastIndexOf('\\');
            path = cut < 0 ? "" : path[..cut];
        }
    }

    // The browser reads the manifest when it starts a host, so it is never seen half written.
    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex ExtensionIdPattern();
}
