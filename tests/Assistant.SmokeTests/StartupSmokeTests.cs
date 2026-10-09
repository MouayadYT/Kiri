using System.IO;
using System.Net.Http;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Assistant.SmokeTests.Support;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>Checklist 1: the app starts on a fresh PC, offline, with nothing in the user's registry and nothing leaving the machine.</summary>
public sealed class StartupSmokeTests
{
    [Fact]
    public Task TheAppStartsOnAFreshDataFolder_WithItsSurfacesToolsAndPrivateDefaults() => Smoke.RunAsync(async app =>
    {
        Assert.True(app.FirstRun);
        Assert.True(Directory.Exists(app.Paths.RootDirectory));

        // Every surface the user can reach is built by the container, as the bootstrapper builds them.
        Assert.NotNull(app.Get<AssistantWindow>());
        Assert.Equal(AssistantWindowState.Compact, app.Get<AssistantWindowStateController>().State);
        Assert.NotNull(app.Get<ConversationViewModel>());
        Assert.NotNull(app.Get<HistoryViewModel>());
        Assert.NotNull(app.Get<SettingsViewModel>());

        // What a first run leaves the user with: private by default, the documented shortcuts, history on, the tools a request can reach.
        var settings = await app.Get<ISettingsService>().LoadAsync();
        Assert.True(settings.Privacy.LocalOnly);
        Assert.True(settings.Privacy.HistoryEnabled);
        Assert.Equal(new Hotkey(HotkeyModifiers.Alt, "A"), settings.Hotkeys.SearchOrAsk);
        Assert.False(settings.Permissions.ClipboardHistory);
        Assert.False(settings.Permissions.ExternalSearch);

        var tools = app.Get<IToolRegistry>();
        Assert.NotNull(tools.Find("calculate"));
        Assert.NotNull(tools.Find("search_files"));
        Assert.NotNull(tools.Find("read_file_text"));
        Assert.True((await app.Get<IPermissionPolicy>().CheckAsync(PermissionCapability.Files)).IsAllowed);
    });

    [Fact]
    public async Task TheNetworkWatchSeesAnAttemptToReachAnotherMachine_SoAQuietRunMeansSomething()
    {
        var watch = NetworkWatch.Start();

        // No connection is made: the handler refuses before a socket is opened, but the request is still seen by .NET's own events.
        using var handler = new SocketsHttpHandler { ConnectCallback = (_, _) => throw new IOException("A check, not a request.") };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://example.test/page"));

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(watch.AssertSilent);
        Assert.Contains("http://example.test:80", failure.Message, StringComparison.Ordinal);
    }
}
