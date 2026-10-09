using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Integrations;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>Step 109: a local integration is started only when needed, its tools are kept so that reconnecting is fast, and a crash is recovered from.</summary>
public sealed class ConnectionLifecycleTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private JsonMcpToolCache NewCache() => new(_folder.File("integration-tools.json"));

    private static InstalledIntegration LocalApp(string version = "1.0.0") =>
        Sample.Program("todoist", "Todoist", @"C:\Tools\todoist.exe") with { InstalledVersion = version, Permissions = new IntegrationPermissions { ReadOnlyTools = ["listTasks"] } };

    private static readonly string Request = "add a task to todoist and list my tasks";

    private static ConnectedAppsFixture Make(
        InstalledIntegration? app = null, IMcpToolCache? cache = null, TimeProvider? clock = null, Func<InstalledIntegration, StubMcpClient>? clients = null, MemoryIntegrationStore? store = null) =>
        new([app ?? LocalApp()], clients, clock, cache: cache, store: store);

    // ---- tools kept so that the program is not started for every request ----

    [Fact]
    public async Task TheToolsAProgramListedAreKeptForTheExactProgramAndVersion()
    {
        var cache = NewCache();
        await using var apps = Make(cache: cache);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));

        Assert.NotEmpty(offered);
        var record = (await apps.Integrations.GetAsync("todoist"))!;
        var kept = cache.TryGet("todoist", JsonMcpToolCache.KeyOf(record));
        Assert.NotNull(kept);
        Assert.Equal(["createTask", "listTasks", "deleteAllTasks"], kept!.Tools.Select(tool => tool.Name).ToArray());
        Assert.Null(cache.TryGet("todoist", JsonMcpToolCache.KeyOf(record with { InstalledVersion = "2.0.0" })));
    }

    [Fact]
    public async Task AfterARestartTheToolsAreOfferedWithoutStartingTheProgramAndItStartsWhenAToolIsCalled()
    {
        var cache = NewCache();
        await using (var first = Make(cache: cache))
        {
            await first.OfferedAsync(ConnectedAppsFixture.Context(Request));
        }

        // The app is closed and opened again: nothing is connected, but what the program listed was kept.
        await using var apps = Make(cache: NewCache());
        var context = ConnectedAppsFixture.Context(Request);
        var offered = await apps.OfferedAsync(context);

        Assert.Contains(offered, tool => tool.Name == "mcp_todoist_list_tasks");
        Assert.Equal(0, apps.Clients.CreateCalls);

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.Single(apps.Clients.Created[0].Calls);
        Assert.Equal(0, apps.Clients.Created[0].ListCalls);
    }

    [Fact]
    public async Task TheKeptToolsAreBuiltUnderTheRulesThatApplyNowSoAPermissionChangedSinceStillHolds()
    {
        var cache = NewCache();
        await using (var first = Make(cache: cache))
        {
            await first.OfferedAsync(ConnectedAppsFixture.Context(Request));
        }

        var blocked = LocalApp() with { Permissions = new IntegrationPermissions { BlockedTools = ["listTasks"] } };
        await using var apps = Make(blocked, NewCache());

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));

        Assert.DoesNotContain(offered, tool => tool.Name == "mcp_todoist_list_tasks");
        Assert.Contains(offered, tool => tool.Name == "mcp_todoist_create_task");
        Assert.DoesNotContain(offered, tool => tool.Name.Contains("delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolsThatWereKeptForAnotherVersionAreNotUsedAndTheProgramIsReadAgain()
    {
        var cache = NewCache();
        await using (var first = Make(LocalApp("1.0.0"), cache))
        {
            await first.OfferedAsync(ConnectedAppsFixture.Context(Request));
        }

        await using var apps = Make(LocalApp("1.1.0"), NewCache());
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));

        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.Equal(1, apps.Clients.Created[0].ListCalls);
    }

    [Fact]
    public async Task KeptToolsThatAreOlderThanADayAreNotUsed()
    {
        var clock = new ManualTimeProvider();
        var cache = NewCache();
        await using (var first = Make(cache: cache, clock: clock))
        {
            await first.OfferedAsync(ConnectedAppsFixture.Context(Request));
        }

        clock.Advance(TimeSpan.FromHours(25));
        await using var apps = Make(cache: NewCache(), clock: clock);
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));

        Assert.Equal(1, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task KeptToolsAreUsedForAWhileWithoutBeingReadAgain()
    {
        var clock = new ManualTimeProvider();
        var cache = NewCache();
        await using (var first = Make(cache: cache, clock: clock))
        {
            await first.OfferedAsync(ConnectedAppsFixture.Context(Request));
        }

        clock.Advance(TimeSpan.FromHours(20));
        await using var apps = Make(cache: NewCache(), clock: clock);
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));

        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AServerThatSaysItsToolsChangedIsReadAgainAndWhatIsKeptIsReplaced()
    {
        var cache = NewCache();
        await using var apps = Make(cache: cache);
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));
        var client = apps.Clients.Created[0];
        client.Tools.Add(Sample.Tool("addNote", "Adds a note to the to-do list."));

        client.RaiseToolsChanged();
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));

        var record = (await apps.Integrations.GetAsync("todoist"))!;
        Assert.Contains(cache.TryGet("todoist", JsonMcpToolCache.KeyOf(record))!.Tools, tool => tool.Name == "addNote");
        Assert.Equal(2, client.ListCalls);
    }

    [Fact]
    public async Task ARemoteServersToolsAreNeverKeptOnDiskBecauseTheyCanChangeWithoutAnyFileChanging()
    {
        var cache = NewCache();
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], cache: cache);

        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));

        Assert.Null(cache.TryGet("todoist", JsonMcpToolCache.KeyOf((await apps.Integrations.GetAsync("todoist"))!)));
        Assert.False(File.Exists(_folder.File("integration-tools.json")));
    }

    [Fact]
    public async Task ADisabledProgramIsNotOfferedAnythingEvenIfItsToolsAreKept()
    {
        var cache = NewCache();
        await using (var first = Make(cache: cache))
        {
            await first.OfferedAsync(ConnectedAppsFixture.Context(Request));
        }

        await using var apps = Make(LocalApp() with { Enabled = false }, NewCache());

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));

        Assert.DoesNotContain(offered, tool => tool.Name.StartsWith("mcp_todoist", StringComparison.Ordinal));
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    // ---- the cache file ----

    [Fact]
    public void TheCacheFileRoundTripsAndNeverHoldsAnythingButToolLists()
    {
        var cache = NewCache();
        var record = LocalApp();
        var key = JsonMcpToolCache.KeyOf(record);
        cache.Put("todoist", key, [Sample.Tool("createTask", "Creates a task.", readOnly: false, destructive: false), Sample.Tool("listTasks", readOnly: true)], new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));

        var again = NewCache().TryGet("todoist", key);

        Assert.NotNull(again);
        Assert.Equal(["createTask", "listTasks"], again!.Tools.Select(tool => tool.Name).ToArray());
        Assert.Equal("Creates a task.", again.Tools[0].Description);
        Assert.Equal(JsonValueKind.Object, again.Tools[0].InputSchema.ValueKind);
        Assert.True(again.Tools[1].Annotations.ReadOnlyHint);
        var text = File.ReadAllText(_folder.File("integration-tools.json"));
        Assert.DoesNotContain("tools\\\\todoist.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\\\Tools", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AKeyChangesWithTheProgramItsArgumentsItsEnvironmentAndItsVersionButNotWithWhetherItIsOnOrWhatItMayDo()
    {
        var record = LocalApp();
        var key = JsonMcpToolCache.KeyOf(record);

        Assert.NotEqual(key, JsonMcpToolCache.KeyOf(record with { Transport = record.Transport with { Command = @"C:\Tools\other.exe" } }));
        Assert.NotEqual(key, JsonMcpToolCache.KeyOf(record with { Transport = record.Transport with { Arguments = ["--x"] } }));
        Assert.NotEqual(key, JsonMcpToolCache.KeyOf(record with { Transport = record.Transport with { Environment = new Dictionary<string, string> { ["A"] = "b" } } }));
        Assert.NotEqual(key, JsonMcpToolCache.KeyOf(record with { InstalledVersion = "9.9.9" }));
        Assert.Equal(key, JsonMcpToolCache.KeyOf(record with { Enabled = false }));
        Assert.Equal(key, JsonMcpToolCache.KeyOf(record with { Permissions = IntegrationPermissions.Default }));
        Assert.Equal(64, key.Length);
    }

    [Fact]
    public void ACorruptOrOversizedCacheFileIsJustEmpty()
    {
        File.WriteAllText(_folder.File("integration-tools.json"), "not json {{{");
        Assert.Null(NewCache().TryGet("todoist", "k"));

        File.WriteAllText(_folder.File("integration-tools.json"), "{\"schemaVersion\":7,\"entries\":{}}");
        Assert.Null(NewCache().TryGet("todoist", "k"));

        File.WriteAllBytes(_folder.File("integration-tools.json"), new byte[5 * 1024 * 1024]);
        Assert.Null(NewCache().TryGet("todoist", "k"));
    }

    [Fact]
    public void AnEntryWithAToolNameThatIsNotValidMakesTheWholeEntryBeReadAgain()
    {
        var cache = NewCache();
        cache.Put("todoist", "k", [Sample.Tool("good")], DateTimeOffset.UtcNow);
        var path = _folder.File("integration-tools.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"good\"", "\"bad name!\"", StringComparison.Ordinal));

        Assert.Null(NewCache().TryGet("todoist", "k"));
    }

    [Fact]
    public void ARemovedEntryIsGoneAndAnUnwritableCacheIsOnlySlower()
    {
        var cache = NewCache();
        cache.Put("todoist", "k", [Sample.Tool("a")], DateTimeOffset.UtcNow);
        cache.Remove("todoist");
        Assert.Null(NewCache().TryGet("todoist", "k"));

        // A path that cannot be written (a folder is in the way) is ignored.
        Directory.CreateDirectory(_folder.File("blocked.json"));
        var broken = new JsonMcpToolCache(_folder.File("blocked.json"));
        broken.Put("todoist", "k", [Sample.Tool("a")], DateTimeOffset.UtcNow);
        Assert.NotNull(broken.TryGet("todoist", "k"));
    }

    [Fact]
    public void OnlyAFewEntriesAreKeptAndTheOldestGoFirst()
    {
        var cache = NewCache();
        var start = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 45; index++)
        {
            cache.Put("app" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), "k", [Sample.Tool("a")], start.AddMinutes(index));
        }

        var again = NewCache();
        Assert.Null(again.TryGet("app0", "k"));
        Assert.Null(again.TryGet("app4", "k"));
        Assert.NotNull(again.TryGet("app5", "k"));
        Assert.NotNull(again.TryGet("app44", "k"));
    }

    // ---- health and recovery ----

    [Fact]
    public async Task ACrashedProgramIsNoticedWhileIdleAndStartedAgainByTheNextUse()
    {
        await using var apps = Make();
        var context = ConnectedAppsFixture.Context(Request);
        await apps.OfferedAsync(context);
        var first = apps.Clients.Created[0];

        first.Drop();
        await WaitAsync(async () => (await apps.Integrations.GetAsync("todoist"))!.Health.Status == IntegrationHealthStatus.Unreachable);
        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(2, apps.Clients.CreateCalls);
        Assert.Equal(1, first.Disposed);
        Assert.Equal(IntegrationHealthStatus.Healthy, (await apps.Integrations.GetAsync("todoist"))!.Health.Status);
    }

    [Fact]
    public async Task AConnectionTheAssistantEndedItselfIsNotReportedAsACrash()
    {
        var clock = new ManualTimeProvider();
        await using var apps = Make(clock: clock);
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));
        var first = apps.Clients.Created[0];

        // Idle for longer than the time a connection is kept: it is closed by the Assistant, which makes the client say it disconnected.
        clock.Advance(TimeSpan.FromMinutes(7));
        first.Drop();
        await Task.Delay(150);

        Assert.Equal(IntegrationHealthStatus.Healthy, (await apps.Integrations.GetAsync("todoist"))!.Health.Status);
    }

    [Fact]
    public async Task ACheckOnAConnectedProgramPingsItAndRecordsItHealthy()
    {
        await using var apps = Make();
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));
        var client = apps.Clients.Created[0];

        var check = await ((IIntegrationConnections)apps.Manager).CheckHealthAsync("todoist", CancellationToken.None);

        Assert.True(check.Connected);
        Assert.Equal(1, client.PingCalls);
        Assert.Equal(1, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task ACheckOnAProgramThatDoesNotAnswerLetsGoOfItAndSaysItIsUnreachable()
    {
        await using var apps = Make();
        var context = ConnectedAppsFixture.Context(Request);
        await apps.OfferedAsync(context);
        var client = apps.Clients.Created[0];
        client.PingFailure = new McpException(McpFailure.TimedOut);

        var check = await ((IIntegrationConnections)apps.Manager).CheckHealthAsync("todoist", CancellationToken.None);

        Assert.False(check.Connected);
        Assert.Equal(McpFailure.Closed, check.Failure);
        Assert.Equal(1, client.Disposed);
        var health = (await apps.Integrations.GetAsync("todoist"))!.Health;
        Assert.Equal(IntegrationHealthStatus.Unreachable, health.Status);
        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");
        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(2, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task ACheckNeverStartsAProgramThatIsNotRunning()
    {
        await using var apps = Make();

        var check = await ((IIntegrationConnections)apps.Manager).CheckHealthAsync("todoist", CancellationToken.None);

        Assert.False(check.Connected);
        Assert.Null(check.Failure);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AQuietConnectionIsAskedWhetherItIsStillThereAndAHungOneIsLetGoOf()
    {
        var clock = new ManualTimeProvider();
        await using var apps = new ConnectedAppsFixture([LocalApp()], clock: clock, options: new McpLoadingOptions { IdleTimeout = TimeSpan.FromHours(1) });
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));
        var client = apps.Clients.Created[0];

        clock.Advance(TimeSpan.FromMinutes(3));
        await WaitAsync(() => Task.FromResult(client.PingCalls == 1));
        Assert.Equal(0, client.Disposed);

        client.PingFailure = new McpException(McpFailure.TimedOut);
        clock.Advance(TimeSpan.FromMinutes(3));
        await WaitAsync(async () => client.Disposed == 1 && (await apps.Integrations.GetAsync("todoist"))!.Health.Status == IntegrationHealthStatus.Unreachable);

        Assert.Equal(1, client.Disposed);
    }

    [Fact]
    public async Task ReconnectingDropsEverythingKeptStartsTheProgramAndReadsItsToolsAgain()
    {
        var cache = NewCache();
        await using var apps = Make(cache: cache);
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));
        var first = apps.Clients.Created[0];

        var check = await ((IIntegrationConnections)apps.Manager).ReconnectAsync("todoist", CancellationToken.None);

        Assert.True(check.Connected);
        Assert.True(check.Restarted);
        Assert.Equal(2, check.ToolCount);
        Assert.Equal(1, first.Disposed);
        Assert.Equal(2, apps.Clients.CreateCalls);
        Assert.Equal(1, apps.Clients.Created[1].ListCalls);
        Assert.Equal(IntegrationHealthStatus.Healthy, (await apps.Integrations.GetAsync("todoist"))!.Health.Status);
    }

    [Fact]
    public async Task ReconnectingToAProgramThatCannotStartSaysWhyAndRecordsIt()
    {
        await using var apps = Make(clients: _ => new StubMcpClient { ConnectFailure = new McpException(McpFailure.LaunchFailed), TransportKind = McpTransportKind.Stdio });

        var check = await ((IIntegrationConnections)apps.Manager).ReconnectAsync("todoist", CancellationToken.None);

        Assert.False(check.Connected);
        Assert.False(check.Blocked);
        Assert.Equal(McpFailure.LaunchFailed, check.Failure);
        var health = (await apps.Integrations.GetAsync("todoist"))!.Health;
        Assert.Equal(IntegrationHealthStatus.Unreachable, health.Status);
        Assert.Equal(McpFailure.LaunchFailed, health.Failure);
    }

    [Fact]
    public async Task ReconnectingIsBlockedForADisabledIntegrationAndForOneThatWouldLeaveThisPcWhileLocalOnlyIsOn()
    {
        await using var disabled = Make(LocalApp() with { Enabled = false });
        await using var remote = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], localOnly: true);

        var off = await ((IIntegrationConnections)disabled.Manager).ReconnectAsync("todoist", CancellationToken.None);
        var internet = await ((IIntegrationConnections)remote.Manager).ReconnectAsync("todoist", CancellationToken.None);

        Assert.True(off.Blocked);
        Assert.True(internet.Blocked);
        Assert.Equal(0, disabled.Clients.CreateCalls);
        Assert.Equal(0, remote.Clients.CreateCalls);
    }

    [Fact]
    public async Task ForgettingAnIntegrationEndsItsProgramAndALaterUseStartsItAgain()
    {
        await using var apps = Make();
        var context = ConnectedAppsFixture.Context(Request);
        await apps.OfferedAsync(context);

        await ((IIntegrationConnections)apps.Manager).ForgetAsync("todoist", CancellationToken.None);

        Assert.Equal(1, apps.Clients.Created[0].Disposed);
        await apps.OfferedAsync(ConnectedAppsFixture.Context(Request));
        Assert.Equal(2, apps.Clients.CreateCalls);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(30);
        }

        Assert.True(await condition(), "The condition did not become true.");
    }
}
