using System.IO.Pipes;
using Assistant.Core.Ipc;
using Assistant.Core.Settings;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Browser;
using Assistant.UI.Explorer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The browser bridge's native-messaging host (PROJECT_SPEC §4.5, §5.7) ----------------------------------------------------

    private static readonly BrowserSelection SampleBrowserSelection =
        new("The quick brown fox", IsTruncated: false, PageTitle: "A page about foxes", PageUrl: "https://example.test/foxes?q=1");

    [Fact]
    public void TheBrowserBridgeSwitchRegistersAndRemovesTheHost_AndIsSavedOnlyWhenThatWorked() => RunSta(() =>
    {
        var bridge = new FakeBrowserBridge();
        var kit = CreateSettingsKit(browserBridge: bridge);
        var page = kit.Model.Integrations;

        page.BrowserBridge = true;
        kit.Settle();
        Assert.Equal(1, bridge.Installs);
        Assert.True(kit.Saved.Integrations.BrowserBridgeEnabled);
        Assert.False(kit.Model.HasNotice);

        page.BrowserBridge = false;
        kit.Settle();
        Assert.Equal(1, bridge.Removals);
        Assert.False(kit.Saved.Integrations.BrowserBridgeEnabled);

        // A registry that cannot be written: the switch goes back, nothing is saved, and the window says so.
        bridge.Works = false;
        page.BrowserBridge = true;
        kit.Settle();
        Assert.False(page.BrowserBridge);
        Assert.False(kit.Saved.Integrations.BrowserBridgeEnabled);
        Assert.Equal("The browser bridge couldn't be turned on.", kit.Model.Notice);
        Assert.Equal(2, bridge.Installs);

        // Showing the saved settings never registers anything.
        var shown = CreateSettingsKit(
            new AppSettings { Integrations = new IntegrationSettings { BrowserBridgeEnabled = true } }, browserBridge: bridge);
        Assert.True(shown.Model.Integrations.BrowserBridge);
        Assert.Equal(2, bridge.Installs);
        Assert.Equal(1, bridge.Removals);
    });

    [Fact]
    public async Task ASelectionSentOverThePipeReachesTheBrowserSink_AndFileRequestsAreNotTouched()
    {
        var sink = new RecordingBrowserSink();
        var requests = new ExplorerFileRequests();
        var opened = new System.Collections.Concurrent.BlockingCollection<ExplorerFiles>();
        requests.Connect(opened.Add, action => action());
        var pipe = LocalPipe.CreateUniqueName("Assistant.Tests.Browser");
        using var integration = new ExplorerIntegration(
            new ExplorerIntegrationOptions(pipe), requests, new FakeExplorerMenu(), new InMemorySettingsService(),
            NullLogger<InvocationServer>.Instance, NullLogger<ExplorerIntegration>.Instance, browserSelections: sink);

        await integration.StartAsync(CancellationToken.None);
        try
        {
            var reply = await SendBrowserSelectionAsync(pipe, SampleBrowserSelection);

            Assert.True(reply.IsAccepted);
            Assert.Equal(SampleBrowserSelection, Assert.Single(sink.Received));
            Assert.False(opened.TryTake(out _, TimeSpan.FromMilliseconds(600)), "A browser selection must not open the file flow.");
        }
        finally
        {
            await integration.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnAppWithoutTheBridgeAnswersABrowserSelectionAsAnUnknownAction()
    {
        var pipe = LocalPipe.CreateUniqueName("Assistant.Tests.BrowserNone");
        using var integration = new ExplorerIntegration(
            new ExplorerIntegrationOptions(pipe), new ExplorerFileRequests(), new FakeExplorerMenu(), new InMemorySettingsService(),
            NullLogger<InvocationServer>.Instance, NullLogger<ExplorerIntegration>.Instance);

        await integration.StartAsync(CancellationToken.None);
        try
        {
            var reply = await SendBrowserSelectionAsync(pipe, SampleBrowserSelection);

            Assert.Equal(InvocationErrorCode.UnknownAction, reply.Error);
        }
        finally
        {
            await integration.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void TheBrowserBridgeTakesASelectionAndLogsOnlyCounts()
    {
        var logger = new MessageLogger<BrowserBridgeIntegration>();
        using var bridge = new BrowserBridgeIntegration(new FakeBrowserBridge(), new InMemorySettingsService(), new BrowserSelectionRequests(), logger);

        var reply = ((IBrowserSelectionSink)bridge).Receive(SampleBrowserSelection);

        Assert.True(reply.IsAccepted);
        var logged = string.Join("\n", logger.Messages);
        Assert.Contains("19 characters", logged);
        Assert.DoesNotContain("quick brown fox", logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foxes", logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", logged, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheHostRegistrationIsRefreshedAtStartOnlyWhenTheBridgeIsOn_AndNothingIsTakenAfterStopping()
    {
        var off = new FakeBrowserBridge();
        using (var bridge = new BrowserBridgeIntegration(off, new InMemorySettingsService(), new BrowserSelectionRequests(), NullLogger<BrowserBridgeIntegration>.Instance))
        {
            await bridge.StartAsync(CancellationToken.None);
            await Task.Delay(300);
            Assert.Equal(0, off.Installs);
        }

        var settings = new InMemorySettingsService();
        await settings.SaveAsync(new AppSettings { Integrations = new IntegrationSettings { BrowserBridgeEnabled = true } });
        var on = new FakeBrowserBridge();
        using (var bridge = new BrowserBridgeIntegration(on, settings, new BrowserSelectionRequests(), NullLogger<BrowserBridgeIntegration>.Instance))
        {
            await bridge.StartAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (on.Installs == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.Equal(1, on.Installs);

            await bridge.StopAsync(CancellationToken.None);
            Assert.Equal(InvocationErrorCode.Unavailable, ((IBrowserSelectionSink)bridge).Receive(SampleBrowserSelection).Error);
        }
    }

    private static async Task<InvocationReply> SendBrowserSelectionAsync(string pipeName, BrowserSelection selection)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, LocalPipe.Options);
        await pipe.ConnectAsync(10_000);
        return await InvocationClient.SendAsync(pipe, InvocationRequest.ForBrowserSelection(selection));
    }

    private sealed class RecordingBrowserSink : IBrowserSelectionSink
    {
        private readonly List<BrowserSelection> _received = [];

        public IReadOnlyList<BrowserSelection> Received
        {
            get
            {
                lock (_received)
                {
                    return [.. _received];
                }
            }
        }

        public InvocationReply Receive(BrowserSelection selection)
        {
            lock (_received)
            {
                _received.Add(selection);
            }

            return InvocationReply.Accepted;
        }
    }

    private sealed class FakeBrowserBridge : IBrowserBridgeInstaller
    {
        private int _installs;
        private int _removals;

        public bool Works { get; set; } = true;

        public int Installs => Volatile.Read(ref _installs);

        public int Removals => Volatile.Read(ref _removals);

        public Task<bool> InstallAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _installs);
            return Task.FromResult(Works);
        }

        public Task<bool> RemoveAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _removals);
            return Task.FromResult(Works);
        }
    }

    private sealed class MessageLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
