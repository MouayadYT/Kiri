using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

public sealed class IntegrationRegistryTests
{
    private static InstalledIntegrationRegistry Registry(IInstalledIntegrationStore store, Assistant.Core.Contracts.ISecretStore? secrets = null) =>
        new(store, NullLogger<InstalledIntegrationRegistry>.Instance, secrets);

    [Fact]
    public async Task ANewRegistryHasNothingInstalled()
    {
        Assert.Empty(await Registry(new MemoryIntegrationStore()).ListAsync());
    }

    [Fact]
    public async Task WhatWasKeptIsReadTheFirstTimeAndNotAgain()
    {
        var store = new MemoryIntegrationStore(Sample.Remote());
        var registry = Registry(store);

        Assert.Single(await registry.ListAsync());
        Assert.NotNull(await registry.GetAsync("todoist"));
        Assert.Null(await registry.GetAsync("missing"));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task AnIntegrationIsAddedAndWrittenBeforeItTakesEffect()
    {
        var store = new MemoryIntegrationStore();
        var registry = Registry(store);

        var added = await registry.AddAsync(Sample.Remote());

        Assert.Equal("todoist", added.Id);
        Assert.Equal(["todoist"], (await registry.ListAsync()).Select(integration => integration.Id));
        Assert.Equal(["todoist"], store.Saved.Select(integration => integration.Id));
    }

    [Fact]
    public async Task IntegrationsAreListedInTheOrderTheyWereInstalled()
    {
        var registry = Registry(new MemoryIntegrationStore());
        await registry.AddAsync(Sample.Remote("zeta", "Zeta"));
        await registry.AddAsync(Sample.Remote("alpha", "Alpha"));
        Assert.Equal(["zeta", "alpha"], (await registry.ListAsync()).Select(integration => integration.Id));
    }

    [Fact]
    public async Task AnIdCanBeInstalledOnlyOnce()
    {
        var registry = Registry(new MemoryIntegrationStore(Sample.Remote()));
        var exception = await Assert.ThrowsAsync<IntegrationException>(() => registry.AddAsync(Sample.Remote(name: "Other")));
        Assert.Equal(IntegrationFailure.Duplicate, exception.Failure);
    }

    [Fact]
    public async Task AnIntegrationThatBreaksARuleIsRefusedAndNothingIsWritten()
    {
        var store = new MemoryIntegrationStore();
        var registry = Registry(store);

        var exception = await Assert.ThrowsAsync<IntegrationException>(() => registry.AddAsync(Sample.Remote(endpoint: "http://mcp.example.com/mcp")));

        Assert.Equal(IntegrationFailure.Invalid, exception.Failure);
        Assert.NotEmpty(exception.Problems);
        Assert.Equal(0, store.Writes);
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task ACommandThatIsAShellIsRefusedAtTheDoor()
    {
        var registry = Registry(new MemoryIntegrationStore());
        var exception = await Assert.ThrowsAsync<IntegrationException>(
            () => registry.AddAsync(Sample.Program(command: @"C:\Windows\System32\cmd.exe", arguments: ["/c", "calc"])));
        Assert.Equal(IntegrationFailure.Invalid, exception.Failure);
    }

    [Fact]
    public async Task ANullIntegrationIsRefused()
    {
        var registry = Registry(new MemoryIntegrationStore());
        var exception = await Assert.ThrowsAsync<IntegrationException>(() => registry.AddAsync(null!));
        Assert.Equal(IntegrationFailure.Invalid, exception.Failure);
    }

    [Fact]
    public async Task AWriteThatFailsLeavesTheRegistryAsItWas()
    {
        var store = new MemoryIntegrationStore(Sample.Remote("one", "One")) { FailWrites = true };
        var registry = Registry(store);

        await Assert.ThrowsAsync<IntegrationException>(() => registry.AddAsync(Sample.Remote("two", "Two")));
        await Assert.ThrowsAsync<IntegrationException>(() => registry.UpdateAsync("one", integration => integration with { Enabled = false }));
        await Assert.ThrowsAsync<IntegrationException>(() => registry.RemoveAsync("one"));

        var list = await registry.ListAsync();
        Assert.Equal(["one"], list.Select(integration => integration.Id));
        Assert.True(list[0].Enabled);
    }

    [Fact]
    public async Task AnIntegrationIsChangedByAFunction()
    {
        var store = new MemoryIntegrationStore(Sample.Remote());
        var registry = Registry(store);

        var updated = await registry.UpdateAsync("todoist", integration => integration with { Enabled = false, InstalledVersion = "2.0.0" });

        Assert.False(updated.Enabled);
        var stored = Assert.Single(store.Saved);
        Assert.False(stored.Enabled);
        Assert.Equal("2.0.0", stored.InstalledVersion);
        Assert.Equal("2.0.0", (await registry.GetAsync("todoist"))!.InstalledVersion);
    }

    [Fact]
    public async Task AChangeThatLeavesTheRecordAsItWasWritesAndRaisesNothing()
    {
        var store = new MemoryIntegrationStore(Sample.Remote());
        var registry = Registry(store);
        var raised = 0;
        registry.Changed += (_, _) => raised++;

        await registry.UpdateAsync("todoist", integration => integration);
        await registry.SetEnabledAsync("todoist", true);

        Assert.Equal(0, store.Writes);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task TheIdOfAnInstalledIntegrationCannotBeChanged()
    {
        var registry = Registry(new MemoryIntegrationStore(Sample.Remote()));
        var exception = await Assert.ThrowsAsync<IntegrationException>(() => registry.UpdateAsync("todoist", integration => integration with { Id = "other" }));
        Assert.Equal(IntegrationFailure.Invalid, exception.Failure);
    }

    [Fact]
    public async Task AChangeThatBreaksARuleIsRefused()
    {
        var registry = Registry(new MemoryIntegrationStore(Sample.Remote()));
        var exception = await Assert.ThrowsAsync<IntegrationException>(
            () => registry.UpdateAsync("todoist", integration => integration with { Transport = integration.Transport with { Endpoint = "http://evil.example.com/" } }));
        Assert.Equal(IntegrationFailure.Invalid, exception.Failure);
        Assert.Equal("https://mcp.example.com/mcp", (await registry.GetAsync("todoist"))!.Transport.Endpoint);
    }

    [Fact]
    public async Task UpdatingAnIntegrationThatIsNotThereFails()
    {
        var registry = Registry(new MemoryIntegrationStore());
        var exception = await Assert.ThrowsAsync<IntegrationException>(() => registry.UpdateAsync("nothing", integration => integration));
        Assert.Equal(IntegrationFailure.NotFound, exception.Failure);
    }

    [Fact]
    public async Task RemovingDeletesTheSecretsTheIntegrationNamed()
    {
        var secrets = new FakeSecretStore();
        secrets.Secrets["mcp.todoist.token"] = "the-token";
        var integration = Sample.Remote() with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.BearerToken,
                Secrets = [new IntegrationSecretBinding("Authorization", "mcp.todoist.token")],
            },
        };
        var registry = Registry(new MemoryIntegrationStore(integration), secrets);

        Assert.True(await registry.RemoveAsync("todoist"));

        Assert.Empty(await registry.ListAsync());
        Assert.Equal(["mcp.todoist.token"], secrets.Deleted);
        Assert.Empty(secrets.Secrets);
        Assert.False(await registry.RemoveAsync("todoist"));
    }

    [Fact]
    public async Task ASecretThatCannotBeDeletedDoesNotKeepTheIntegration()
    {
        var integration = Sample.Remote() with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.HeaderKey,
                Secrets = [new IntegrationSecretBinding("X-Key", "mcp.todoist.key")],
            },
        };
        var registry = Registry(new MemoryIntegrationStore(integration), new ThrowingSecretStore());

        Assert.True(await registry.RemoveAsync("todoist"));
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task TheListSurvivesANewRegistryOverTheSameStore()
    {
        var store = new MemoryIntegrationStore();
        await Registry(store).AddAsync(Sample.Remote());
        Assert.Equal("todoist", Assert.Single(await Registry(store).ListAsync()).Id);
    }

    [Fact]
    public async Task ChangesAreReportedAfterTheyAreWritten()
    {
        var store = new MemoryIntegrationStore();
        var registry = Registry(store);
        var seen = new List<(string Id, IntegrationChangeKind Kind, int Writes)>();
        registry.Changed += (_, change) => seen.Add((change.IntegrationId, change.Kind, store.Writes));

        await registry.AddAsync(Sample.Remote());
        await registry.SetEnabledAsync("todoist", false);
        await registry.RemoveAsync("todoist");

        Assert.Equal(
            [("todoist", IntegrationChangeKind.Added, 1), ("todoist", IntegrationChangeKind.Updated, 2), ("todoist", IntegrationChangeKind.Removed, 3)],
            seen);
    }

    [Fact]
    public async Task AListenerThatThrowsCannotUndoAChange()
    {
        var registry = Registry(new MemoryIntegrationStore());
        registry.Changed += (_, _) => throw new InvalidOperationException("listener failed");

        await registry.AddAsync(Sample.Remote());

        Assert.Single(await registry.ListAsync());
    }

    [Fact]
    public async Task ChangesMadeAtTheSameTimeAreAllKept()
    {
        var registry = Registry(new MemoryIntegrationStore());
        await Task.WhenAll(Enumerable.Range(0, 20).Select(number => registry.AddAsync(Sample.Remote("app" + (char)('a' + number), "App " + number))));
        Assert.Equal(20, (await registry.ListAsync()).Count);
    }

    [Fact]
    public async Task TheListGivenIsASnapshot()
    {
        var registry = Registry(new MemoryIntegrationStore(Sample.Remote()));
        var before = await registry.ListAsync();
        await registry.AddAsync(Sample.Remote("other", "Other"));
        Assert.Single(before);
        Assert.Equal(2, (await registry.ListAsync()).Count);
    }

    [Fact]
    public async Task HealthIsRecordedAndARepeatedResultWithinTheHourIsNotWrittenAgain()
    {
        var store = new MemoryIntegrationStore(Sample.Remote());
        var registry = Registry(store);
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

        await registry.RecordHealthAsync("todoist", IntegrationHealthStatus.Healthy, null, now);
        Assert.Equal(1, store.Writes);

        await registry.RecordHealthAsync("todoist", IntegrationHealthStatus.Healthy, null, now.AddMinutes(30));
        Assert.Equal(1, store.Writes);

        await registry.RecordHealthAsync("todoist", IntegrationHealthStatus.Healthy, null, now.AddMinutes(61));
        Assert.Equal(2, store.Writes);

        var health = (await registry.GetAsync("todoist"))!.Health;
        Assert.Equal(IntegrationHealthStatus.Healthy, health.Status);
        Assert.Equal(0, health.ConsecutiveFailures);
    }

    [Fact]
    public async Task FailuresInARowAreCountedAndAHealthyResultResetsTheCount()
    {
        var registry = Registry(new MemoryIntegrationStore(Sample.Remote()));
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

        await registry.RecordHealthAsync("todoist", IntegrationHealthStatus.Unreachable, McpFailure.ConnectFailed, now);
        await registry.RecordHealthAsync("todoist", IntegrationHealthStatus.Failed, McpFailure.Protocol, now.AddMinutes(1));
        Assert.Equal(2, (await registry.GetAsync("todoist"))!.Health.ConsecutiveFailures);

        await registry.RecordHealthAsync("todoist", IntegrationHealthStatus.Healthy, null, now.AddMinutes(2));
        var health = (await registry.GetAsync("todoist"))!.Health;
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.Null(health.Failure);
    }

    [Fact]
    public async Task CapabilitiesAreRecordedAndWrittenOnlyWhenTheyChange()
    {
        var store = new MemoryIntegrationStore(Sample.Remote());
        var registry = Registry(store);
        var capabilities = new IntegrationCapabilities { Tools = true, ProtocolVersion = "2026-07-28", ToolNames = ["a", "b"], RefreshedAt = DateTimeOffset.UnixEpoch };

        await registry.RecordCapabilitiesAsync("todoist", capabilities);
        await registry.RecordCapabilitiesAsync("todoist", capabilities with { RefreshedAt = DateTimeOffset.UnixEpoch.AddDays(1) });
        Assert.Equal(1, store.Writes);

        await registry.RecordCapabilitiesAsync("todoist", capabilities with { ToolNames = ["a", "b", "c"] });
        Assert.Equal(2, store.Writes);
        Assert.Equal(["a", "b", "c"], (await registry.GetAsync("todoist"))!.Capabilities.ToolNames);
    }

    [Fact]
    public async Task TheAuthenticationStateAndTheTransportKindAreRecorded()
    {
        var registry = Registry(new MemoryIntegrationStore(Sample.Remote()));
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

        await registry.RecordAuthenticationStateAsync("todoist", IntegrationAuthState.NeedsSignIn, now);
        await registry.RecordTransportKindAsync("todoist", McpTransportKind.LegacySse);

        var integration = (await registry.GetAsync("todoist"))!;
        Assert.Equal(IntegrationAuthState.NeedsSignIn, integration.Authentication.State);
        Assert.Equal(now, integration.Authentication.CheckedAt);
        Assert.Equal(McpTransportKind.LegacySse, integration.Transport.Kind);
    }

    // A secret store that cannot do anything.
    private sealed class ThrowingSecretStore : Assistant.Core.Contracts.ISecretStore
    {
        public Task SetAsync(string name, string secret, CancellationToken cancellationToken = default) => throw new Assistant.Core.Contracts.SecretStoreException("no");

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) => throw new Assistant.Core.Contracts.SecretStoreException("no");

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new Assistant.Core.Contracts.SecretStoreException("no");
    }
}
