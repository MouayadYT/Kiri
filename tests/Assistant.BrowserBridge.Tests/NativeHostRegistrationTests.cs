using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.BrowserBridge.Registration;
using Microsoft.Win32;
using Xunit;

namespace Assistant.BrowserBridge.Tests;

/// <summary>
/// The host's registration for the current user (PROJECT_SPEC §4.5, §5.7), against a registry key and a folder of the test's own: the
/// user's real registry is never touched.
/// </summary>
public sealed class NativeHostRegistrationTests : IDisposable
{
    private const string OtherExtension = "abcdefghijklmnopabcdefghijklmnop";

    private readonly string _keyPath = $@"Software\Assistant.Tests.NativeHost.{Guid.NewGuid():N}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "assistant-native-host-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _software;
    private readonly string _executable = @"C:\Program Files\Assistant\Assistant.BrowserBridge.exe";

    public NativeHostRegistrationTests()
    {
        _software = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
    }

    public void Dispose()
    {
        _software.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private NativeHostRegistration Registration() => new(_software, _folder);

    private string? ValueOf(string hostsKey)
    {
        using var key = _software.OpenSubKey($@"{hostsKey}\{NativeHostRegistration.HostName}");
        return key?.GetValue("") as string;
    }

    [Fact]
    public void TheHostNameIsOneAChromiumBrowserAccepts()
    {
        Assert.Matches(new Regex("^[a-z0-9_]+(\\.[a-z0-9_]+)*$"), NativeHostRegistration.HostName);
        Assert.Equal(NativeHostRegistration.HostName + ".json", NativeHostRegistration.ManifestFileName);
    }

    [Fact]
    public void RegisteringWritesTheManifestAndPointsEveryBrowserAtIt()
    {
        var registration = Registration();
        Assert.False(registration.IsRegistered());

        registration.Register(_executable, [ExtensionIdentity.Id]);

        Assert.True(registration.IsRegistered());
        Assert.Equal(Path.Combine(_folder, "assistant.browser_bridge.json"), registration.ManifestPath);
        foreach (var browser in ChromiumBrowser.Supported)
        {
            Assert.Equal(registration.ManifestPath, ValueOf(browser.HostsKey));
        }

        Assert.Equal(
            new[] { "Brave", "Chrome", "Chromium", "Edge" },
            ChromiumBrowser.Supported.Select(browser => browser.Name).Order(StringComparer.Ordinal).ToArray());

        using var manifest = JsonDocument.Parse(File.ReadAllText(registration.ManifestPath));
        var root = manifest.RootElement;
        Assert.Equal("assistant.browser_bridge", root.GetProperty("name").GetString());
        Assert.Equal(_executable, root.GetProperty("path").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));
        Assert.Equal(
            new[] { $"chrome-extension://{ExtensionIdentity.Id}/" },
            root.GetProperty("allowed_origins").EnumerateArray().Select(origin => origin.GetString()!).ToArray());
        Assert.Equal(5, root.EnumerateObject().Count());
    }

    [Fact]
    public void TheManifestIsUtf8WithoutABom_AndHoldsTheExecutablePathWhateverItsCharacters()
    {
        var executable = @"C:\Users\Zoë O'Brien\AppData\My ""Apps""\Assistant.BrowserBridge.exe";

        Registration().Register(executable, [ExtensionIdentity.Id]);

        var bytes = File.ReadAllBytes(Registration().ManifestPath);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        using var manifest = JsonDocument.Parse(bytes);
        Assert.Equal(executable, manifest.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void RegisteringAgainChangesNothing_AndRegisteringAnotherPathOrExtensionReplacesWhatWasThere()
    {
        var registration = Registration();
        registration.Register(_executable, [ExtensionIdentity.Id]);
        var first = File.ReadAllText(registration.ManifestPath);

        registration.Register(_executable, [ExtensionIdentity.Id]);
        Assert.Equal(first, File.ReadAllText(registration.ManifestPath));
        Assert.All(Directory.GetFiles(_folder), file => Assert.DoesNotContain(".tmp", file, StringComparison.Ordinal));

        registration.Register(@"D:\Moved\Assistant.BrowserBridge.exe", [OtherExtension, ExtensionIdentity.Id, OtherExtension]);
        using var manifest = JsonDocument.Parse(File.ReadAllText(registration.ManifestPath));
        Assert.Equal(@"D:\Moved\Assistant.BrowserBridge.exe", manifest.RootElement.GetProperty("path").GetString());
        Assert.Equal(
            new[] { $"chrome-extension://{OtherExtension}/", $"chrome-extension://{ExtensionIdentity.Id}/" }.Order(StringComparer.Ordinal),
            manifest.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(origin => origin.GetString()!).Order(StringComparer.Ordinal));
        Assert.True(registration.IsRegistered());
    }

    [Fact]
    public void UnregisteringRemovesOnlyTheHost_TheKeysThatHeldNothingElse_AndTheManifest()
    {
        // Another host registered for Chrome, and a value Chrome keeps of its own.
        using (var other = _software.CreateSubKey(@"Google\Chrome\NativeMessagingHosts\com.other.host", writable: true))
        {
            other.SetValue("", @"C:\other\host.json");
        }

        using (var chrome = _software.CreateSubKey(@"Google\Chrome", writable: true))
        {
            chrome.SetValue("Own", 1);
        }

        var registration = Registration();
        registration.Register(_executable, [ExtensionIdentity.Id]);

        registration.Unregister();

        Assert.False(registration.IsRegistered());
        Assert.False(File.Exists(registration.ManifestPath));
        foreach (var browser in ChromiumBrowser.Supported)
        {
            Assert.Null(ValueOf(browser.HostsKey));
        }

        using (var other = _software.OpenSubKey(@"Google\Chrome\NativeMessagingHosts\com.other.host"))
        {
            Assert.Equal(@"C:\other\host.json", other!.GetValue("") as string);
        }

        using (var chrome = _software.OpenSubKey(@"Google\Chrome"))
        {
            Assert.Equal(1, chrome!.GetValue("Own"));
        }

        // The keys that existed only for the host are gone, so nothing is left behind for a browser that is not installed.
        Assert.Null(_software.OpenSubKey("Microsoft"));
        Assert.Null(_software.OpenSubKey("BraveSoftware"));
        Assert.Null(_software.OpenSubKey("Chromium"));

        // Removing what is not there is not an error.
        registration.Unregister();
    }

    [Fact]
    public void ARegistrationWhoseManifestWasDeletedOrWhoseBrowserKeyPointsElsewhereIsNotRegistered()
    {
        var registration = Registration();
        registration.Register(_executable, [ExtensionIdentity.Id]);

        using (var edge = _software.OpenSubKey($@"Microsoft\Edge\NativeMessagingHosts\{NativeHostRegistration.HostName}", writable: true))
        {
            edge!.SetValue("", @"C:\elsewhere.json");
        }

        Assert.False(registration.IsRegistered());

        registration.Register(_executable, [ExtensionIdentity.Id]);
        Assert.True(registration.IsRegistered());
        File.Delete(registration.ManifestPath);
        Assert.False(registration.IsRegistered());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Assistant.BrowserBridge.exe")]
    [InlineData(@"..\Assistant.BrowserBridge.exe")]
    public void ARelativeHostPathIsRefused(string executable)
    {
        Assert.Throws<ArgumentException>(() => Registration().Register(executable, [ExtensionIdentity.Id]));
        Assert.False(Directory.Exists(_folder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")]
    [InlineData("abcdefghijklmnopabcdefghijklmnoq")]
    [InlineData("abcdefghijklmnopabcdefghijklmnopa")]
    [InlineData("*")]
    public void AnythingButAnExtensionIdIsRefused(string id)
    {
        Assert.False(NativeHostRegistration.IsExtensionId(id));
        Assert.Throws<ArgumentException>(() => Registration().Register(_executable, [id]));
        Assert.Throws<ArgumentException>(() => Registration().Register(_executable, []));
        Assert.Null(ValueOf(ChromiumBrowser.Supported[0].HostsKey));
    }

    [Fact]
    public void TheManifestFolderMustBeAFullPath()
    {
        Assert.Throws<ArgumentException>(() => new NativeHostRegistration(_software, "relative"));
        Assert.Throws<ArgumentException>(() => new NativeHostRegistration(_software, ""));
    }
}
