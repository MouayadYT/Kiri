using Xunit;
using System.Text.Json;

namespace Assistant.BrowserBridge.Tests;

/// <summary>
/// Guards the browser extension (steps 83-85, 88, 90): a Manifest V3 extension installed with only three permissions, the
/// context menu, <c>activeTab</c> (the tab the user chose the entry in, only then) and native messaging (the one way out: the
/// Assistant's own host on this PC), and one optional one, <c>scripting</c>, which the browser asks the user for only when they choose
/// "Selection + Nearby Context" and which is the only thing that setting is made of. No host permissions, no storage, no network, no
/// content scripts and no remote code (PROJECT_SPEC §3.3, §5.7).
/// </summary>
public sealed class ExtensionManifestTests
{
    private static readonly string ExtensionFolder = Path.Combine(AppContext.BaseDirectory, "extension");

    private static JsonElement Manifest()
    {
        using var stream = File.OpenRead(Path.Combine(ExtensionFolder, "manifest.json"));
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(ExtensionFolder, file));

    private static string ServiceWorkerSource() => Source("service-worker.js");

    private static readonly string[] ScriptFiles =
    {
        "service-worker.js", "selection-request.js", "hand-over.js", "browser-name.js",
        "settings.js", "nearby-page.js", "nearby-context.js", "options.js",
    };

    private static JsonElement Messages()
    {
        using var stream = File.OpenRead(Path.Combine(ExtensionFolder, "_locales", "en", "messages.json"));
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    [Fact]
    public void ManifestIsVersion3WithANameAndVersionTakenFromTheMessages()
    {
        var manifest = Manifest();

        Assert.Equal(3, manifest.GetProperty("manifest_version").GetInt32());
        Assert.Equal("en", manifest.GetProperty("default_locale").GetString());
        Assert.Equal("__MSG_extensionName__", manifest.GetProperty("name").GetString());
        Assert.Equal("__MSG_extensionDescription__", manifest.GetProperty("description").GetString());
        Assert.True(Version.TryParse(manifest.GetProperty("version").GetString(), out _));

        var messages = Messages();
        foreach (var key in new[] { "extensionName", "extensionDescription", "menuTitle" })
        {
            Assert.False(string.IsNullOrWhiteSpace(messages.GetProperty(key).GetProperty("message").GetString()), key);
        }
    }

    [Fact]
    public void InstalledWithOnlyTheMenuTheTabTheUserChoseItInAndTheNativeHost_AndScriptingIsOnlyOptional()
    {
        var manifest = Manifest();

        var permissions = manifest.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Equal(new[] { "contextMenus", "activeTab", "nativeMessaging" }, permissions);

        // scripting reads the text around the selection in the one frame the user chose the entry in (activeTab, which grants no
        // standing access), only in "Selection + Nearby Context", and only after the browser has asked the user for it. It is not a host
        // permission, and nothing is stored: the mode is whether the permission is held.
        var optional = manifest.GetProperty("optional_permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Equal(new[] { "scripting" }, optional);
        Assert.False(manifest.TryGetProperty("optional_host_permissions", out _));
        Assert.DoesNotContain("storage", permissions.Concat(optional));
    }

    [Fact]
    public void TheOptionsPageIsTheExtensionsOwnAndItsFilesExist()
    {
        var options = Manifest().GetProperty("options_ui");

        Assert.Equal("options.html", options.GetProperty("page").GetString());
        Assert.False(options.GetProperty("open_in_tab").GetBoolean());
        foreach (var file in new[] { "options.html", "options.css", "options.js" })
        {
            Assert.True(File.Exists(Path.Combine(ExtensionFolder, file)), file);
        }

        // Its words come from the messages, so the page holds none of its own, and it loads nothing from outside the extension.
        var html = Source("options.html");
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.Contains("<script type=\"module\" src=\"options.js\"></script>", html, StringComparison.Ordinal);
        var messages = Messages();
        foreach (System.Text.RegularExpressions.Match key in System.Text.RegularExpressions.Regex.Matches(html, "data-i18n=\"(\\w+)\""))
        {
            Assert.False(string.IsNullOrWhiteSpace(messages.GetProperty(key.Groups[1].Value).GetProperty("message").GetString()), key.Groups[1].Value);
        }

        foreach (var key in new[] { "optionsTitle", "optionsSaved", "optionsNotSaved" })
        {
            Assert.False(string.IsNullOrWhiteSpace(messages.GetProperty(key).GetProperty("message").GetString()), key);
        }

        Assert.Equal("Selection Only", messages.GetProperty("modeSelectionTitle").GetProperty("message").GetString());
        Assert.Equal("Selection + Nearby Context", messages.GetProperty("modeNearbyTitle").GetProperty("message").GetString());
    }

    [Fact]
    public void TheSettingIsTheOptionalPermissionItself_AndDefaultsToSelectionOnly()
    {
        var settings = Source("settings.js");

        Assert.Contains("export const MODE_SELECTION_ONLY = \"selection\";", settings);
        Assert.Contains("export const MODE_NEARBY = \"nearby\";", settings);
        Assert.Contains("export const NEARBY_PERMISSION = \"scripting\";", settings);

        // Read from the grant, never from anything stored; anything the browser cannot say leaves the page unread.
        Assert.Equal(1, CountOf(settings, "chrome.permissions.contains("));
        Assert.Equal(1, CountOf(settings, "chrome.permissions.request("));
        Assert.Equal(1, CountOf(settings, "chrome.permissions.remove("));
        Assert.Contains("? MODE_NEARBY : MODE_SELECTION_ONLY", settings);
        Assert.Contains("return MODE_SELECTION_ONLY;", settings);
        foreach (var file in ScriptFiles)
        {
            Assert.DoesNotContain("chrome.storage", Source(file), StringComparison.Ordinal);
            Assert.True(CountOf(Source(file), "chrome.permissions.request(") == (file == "settings.js" ? 1 : 0), file);
        }

        // The permission prompt is only shown for a call made inside the user's click: the options page asks first, before any await.
        var options = Source("options.js");
        var handler = options[options.IndexOf("choice.addEventListener(\"change\"", StringComparison.Ordinal)..];
        Assert.True(
            handler.IndexOf("await", StringComparison.Ordinal) == handler.IndexOf("await chooseContextMode(wanted)", StringComparison.Ordinal),
            "the first await in the click handler is the permission request");
    }

    [Fact]
    public void TheNearbyLimitsAreTheOnesTheAppPipeEnforces()
    {
        var nearby = Source("nearby-context.js");

        Assert.Contains($"export const MAX_NEARBY_LENGTH = {Assistant.Core.Ipc.BrowserSelection.MaxNearbyLength};", nearby);
        Assert.Contains($"export const MAX_WORD_LENGTH = {Assistant.Core.Domain.NearbyPageText.MaxWordLength};", nearby);
        Assert.Contains("export const GATHER_LENGTH = 2000;", nearby);
        Assert.Contains("export const MAX_NEARBY_NODES = 600;", nearby);
    }

    [Fact]
    public void OnlyTheNearbyContextReadsThePage_OnlyWhenTheUserChoseThatMode_AndOnlyInTheFrameTheyClicked()
    {
        foreach (var file in ScriptFiles)
        {
            Assert.True(CountOf(Source(file), "chrome.scripting.executeScript(") == (file == "nearby-context.js" ? 1 : 0), file);
        }

        var nearby = Source("nearby-context.js");
        Assert.Contains("(await readContextMode()) !== MODE_NEARBY", nearby);
        Assert.Contains("target.frameIds = [info.frameId];", nearby);
        Assert.Contains("func: collectNearbyText", nearby);
        Assert.DoesNotContain("allFrames", nearby, StringComparison.Ordinal);
        Assert.DoesNotContain("world:", nearby, StringComparison.Ordinal);
        Assert.DoesNotContain("files:", nearby, StringComparison.Ordinal);

        // It is asked for only from the click's handler.
        var worker = ServiceWorkerSource();
        Assert.Equal(1, CountOf(worker, "captureNearbyContext("));
        Assert.Contains("const nearby = await captureNearbyContext(info, tab);", worker);
        Assert.Contains("handOver(nearby === null ? request : { ...request, ...nearby });", worker);
    }

    [Fact]
    public void ThePageSideCodeStandsAloneAndTouchesNothingOfTheExtension()
    {
        // It is injected as source text, so it cannot import or use anything outside its own function, and it cannot write to the page.
        var page = Source("nearby-page.js");

        Assert.DoesNotContain("import ", StripComments(page), StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", page, StringComparison.Ordinal);
        Assert.DoesNotContain("appendChild", page, StringComparison.Ordinal);
        Assert.DoesNotContain("setAttribute", page, StringComparison.Ordinal);
        Assert.DoesNotContain("addEventListener", page, StringComparison.Ordinal);
        Assert.DoesNotContain("document.cookie", page, StringComparison.Ordinal);
        Assert.DoesNotContain("location", page, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(page, "export function"));
        Assert.Equal(1, CountOf(page, "window.getSelection()"));
    }

    [Fact]
    public void TheManifestsKeyGivesTheExtensionTheIdTheHostIsRegisteredFor()
    {
        // A browser derives an extension's id from the key: the first 16 bytes of the SHA-256 of its DER bytes, each hex digit as a letter a-p.
        var der = Convert.FromBase64String(Manifest().GetProperty("key").GetString()!);
        var hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(der))[..32].ToLowerInvariant();
        var id = new string(hex.Select(digit => (char)('a' + Convert.ToInt32(digit.ToString(), 16))).ToArray());

        Assert.Equal(Registration.ExtensionIdentity.Id, id);
        Assert.True(Registration.NativeHostRegistration.IsExtensionId(id));
    }

    [Fact]
    public void TheHostTheExtensionNamesAndTheProtocolItSpeaksAreTheHostsOwn()
    {
        var handOver = Source("hand-over.js");

        Assert.Contains($"export const HOST_NAME = \"{Registration.NativeHostRegistration.HostName}\";", handOver);
        Assert.Contains($"export const PROTOCOL_VERSION = {Protocol.BrowserMessageProtocol.Version};", handOver);
        Assert.Contains("chrome.runtime.sendNativeMessage(HOST_NAME, buildHostMessage(request))", handOver);
        Assert.Contains("type: \"selection\"", handOver);
    }

    [Fact]
    public void TheRequestLimitsAreTheOnesTheAppPipeEnforces()
    {
        var request = Source("selection-request.js");

        Assert.Contains($"export const MAX_SELECTION_LENGTH = {Assistant.Core.Ipc.BrowserSelection.MaxTextLength};", request);
        Assert.Contains($"export const MAX_TITLE_LENGTH = {Assistant.Core.Ipc.BrowserSelection.MaxTitleLength};", request);
        Assert.Contains($"export const MAX_URL_LENGTH = {Assistant.Core.Ipc.BrowserSelection.MaxUrlLength};", request);
        Assert.Contains($"export const MAX_BROWSER_NAME_LENGTH = {Assistant.Core.Ipc.BrowserSelection.MaxBrowserNameLength};", request);
    }

    [Fact]
    public void OnlyTheHandOverTalksToTheNativeHost_AndItDoesSoOnceWithAOneShotMessage()
    {
        foreach (var file in ScriptFiles)
        {
            var uses = CountOf(Source(file), "sendNativeMessage");
            Assert.Equal(file == "hand-over.js" ? 1 : 0, uses);
        }
    }

    [Theory]
    [InlineData("host_permissions")]
    [InlineData("optional_host_permissions")]
    [InlineData("content_scripts")]
    [InlineData("web_accessible_resources")]
    [InlineData("externally_connectable")]
    [InlineData("content_security_policy")]
    [InlineData("commands")]
    [InlineData("omnibox")]
    [InlineData("declarative_net_request")]
    [InlineData("chrome_url_overrides")]
    [InlineData("sandbox")]
    public void ManifestDeclaresNothingThatReachesPagesOrTheNetwork(string property)
    {
        Assert.False(Manifest().TryGetProperty(property, out _), property);
    }

    [Fact]
    public void ServiceWorkerAndIconsDeclaredInTheManifestExist()
    {
        var manifest = Manifest();

        var worker = manifest.GetProperty("background").GetProperty("service_worker").GetString()!;
        Assert.True(File.Exists(Path.Combine(ExtensionFolder, worker)), worker);
        Assert.Equal("module", manifest.GetProperty("background").GetProperty("type").GetString());

        var icons = manifest.GetProperty("icons").EnumerateObject().ToArray();
        Assert.NotEmpty(icons);
        foreach (var icon in icons)
        {
            var path = Path.Combine(ExtensionFolder, icon.Value.GetString()!);
            Assert.True(File.Exists(path), path);
            var header = File.ReadAllBytes(path).Take(8).ToArray();
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
        }
    }

    [Fact]
    public void ServiceWorkerRegistersOneMenuEntryForSelectedTextWithALabelFromTheMessages()
    {
        var source = ServiceWorkerSource();

        Assert.Equal(1, CountOf(source, "contextMenus.create("));
        Assert.Contains("title: chrome.i18n.getMessage(\"menuTitle\")", source);
        Assert.Contains("contexts: [\"selection\"]", source);
    }

    [Fact]
    public void NoScriptHardCodesTheProductNameOrTheMenuLabel()
    {
        var label = Messages().GetProperty("menuTitle").GetProperty("message").GetString()!;
        foreach (var file in ScriptFiles)
        {
            var code = StripComments(Source(file));
            Assert.DoesNotContain(label, code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Assistant", code.Replace("assistant.ask", ""), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClickingTheEntryCapturesTheSelectionTitleAddressAndBrowserNameOnly()
    {
        var worker = ServiceWorkerSource();
        Assert.Contains("buildSelectionRequest(info, tab, detectBrowserName(navigator))", worker);
        Assert.Contains("handOver(nearby === null ? request : { ...request, ...nearby });", worker);

        var request = Source("selection-request.js");
        Assert.Contains("info.selectionText", request);
        Assert.Contains("tab.title", request);
        Assert.Contains("info.pageUrl", request);

        // The captured request is these five fields and nothing else (no page body, no tab id, no icon).
        var returned = request[request.IndexOf("return {", StringComparison.Ordinal)..];
        var fields = System.Text.RegularExpressions.Regex.Matches(returned, @"^\s{4}(\w+):", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "selectionText", "selectionTruncated", "pageTitle", "pageUrl", "browserName" }, fields);
    }

    [Fact]
    public void SelectionLimitMatchesTheAppsOwnSelectionLimit()
    {
        Assert.Contains("export const MAX_SELECTION_LENGTH = 200000;", Source("selection-request.js"));
    }

    [Theory]
    [InlineData("fetch(")]
    [InlineData("XMLHttpRequest")]
    [InlineData("WebSocket")]
    [InlineData("EventSource")]
    [InlineData("sendBeacon")]
    [InlineData("importScripts")]
    [InlineData("import(")]
    [InlineData("eval(")]
    [InlineData("new Function")]
    [InlineData("http://")]
    [InlineData("https://")]
    [InlineData("chrome.tabs")]
    [InlineData("chrome.storage")]
    [InlineData("chrome.scripting.insertCSS")]
    [InlineData("chrome.scripting.registerContentScripts")]
    [InlineData("chrome.cookies")]
    [InlineData("chrome.history")]
    [InlineData("connectNative")]
    [InlineData("chrome.windows")]
    [InlineData("chrome.webRequest")]
    [InlineData("chrome.downloads")]
    [InlineData("chrome.runtime.sendMessage")]
    [InlineData("localStorage")]
    [InlineData("indexedDB")]
    [InlineData("console.")]
    public void NoScriptHasNetworkPageAccessStorageOrLogging(string forbidden)
    {
        foreach (var file in ScriptFiles)
        {
            Assert.DoesNotContain(forbidden, Source(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryScriptTheWorkerImportsIsPartOfTheExtension()
    {
        foreach (System.Text.RegularExpressions.Match import in System.Text.RegularExpressions.Regex.Matches(
                     ServiceWorkerSource(), @"^import .* from ""\./([\w.-]+)"";", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            Assert.True(File.Exists(Path.Combine(ExtensionFolder, import.Groups[1].Value)), import.Groups[1].Value);
        }
    }

    private static string StripComments(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
