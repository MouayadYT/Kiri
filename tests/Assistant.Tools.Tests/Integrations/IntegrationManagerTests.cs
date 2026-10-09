using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 109: reuse, update, disable and remove the integrations the Assistant installed.</summary>
public sealed class IntegrationManagerTests : IDisposable
{
    private sealed class FakeOffers : IIntegrationOffers
    {
        public List<(InstallCandidate Candidate, InstalledIntegration Current)> Updates { get; } = [];

        public Task<IntegrationOffer> OfferAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IntegrationOffer> OfferConnectAsync(KnownEndpoint endpoint, InstalledIntegration? existing, IntegrationCapability? capability, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IntegrationOffer> OfferUpdateAsync(InstallCandidate candidate, InstalledIntegration current, CancellationToken cancellationToken = default)
        {
            Updates.Add((candidate, current));
            return Task.FromResult(new IntegrationOffer
            {
                OfferId = "update-offer",
                Kind = IntegrationOfferKind.Update,
                AppName = current.Name,
                IntegrationName = candidate.Name,
                MakerText = "m",
                Provides = "p",
                Source = "s",
                CurrentVersion = current.InstalledVersion,
                NewVersion = candidate.Source.Version,
            });
        }

        public Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Decline(string offerId)
        {
        }
    }

    private sealed class FakeReviewer : ICandidateReviewer
    {
        public Func<IntegrationCandidate, CandidateReview>? Update { get; set; }

        public List<(IntegrationCandidate Candidate, string App, string Id)> Updates { get; } = [];

        public Task<CandidateReview> ReviewAsync(IntegrationCandidate candidate, IntegrationNeed need, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CandidateReview>> ReviewAllAsync(IReadOnlyList<IntegrationCandidate> candidates, IntegrationNeed need, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CandidateReview> ReviewUpdateAsync(IntegrationCandidate candidate, string appName, string integrationId, CancellationToken cancellationToken = default)
        {
            Updates.Add((candidate, appName, integrationId));
            return Task.FromResult(Update!(candidate));
        }
    }

    private sealed class Installer : IIntegrationInstaller
    {
        public Task<InstallPlan> PlanAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) => Task.FromResult(new InstallPlan());

        public Task<InstallOutcome> InstallAsync(InstallCandidate candidate, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A manager never installs.");

        public Task<InstallOutcome> UpdateAsync(InstallCandidate candidate, string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A manager never updates by itself.");

        public Task<int> CleanUpAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class MemoryToolCache : IMcpToolCache
    {
        public List<string> Removed { get; } = [];

        public CachedTools? TryGet(string integrationId, string key) => null;

        public void Put(string integrationId, string key, IReadOnlyList<McpToolDescriptor> tools, DateTimeOffset savedAt)
        {
        }

        public void Remove(string integrationId) => Removed.Add(integrationId);
    }

    private readonly InstallFixture _fixture = new();
    private readonly FakePackageMetadata _metadata = new();
    private readonly FakeReviewer _reviewer = new();
    private readonly FakeOffers _offers = new();
    private readonly MemoryToolCache _toolCache = new();
    private readonly FixedSettings _settings;
    private readonly ManualTimeProvider _clock = new(Reviewable.Now);
    private readonly IntegrationManager _manager;

    public IntegrationManagerTests()
    {
        _settings = new FixedSettings { Current = new AppSettings { Integrations = new IntegrationSettings { CheckForIntegrationUpdates = true }, Privacy = new PrivacySettings { LocalOnly = false } } };
        _manager = Manager(_settings);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _fixture.Dispose();
    }

    private IntegrationManager Manager(FixedSettings settings, bool web = true) =>
        new(
            _fixture.Registry,
            _fixture.Connections,
            new Installer(),
            _fixture.Runtimes,
            _fixture.Layout,
            _toolCache,
            _metadata,
            _reviewer,
            _offers,
            settings,
            new FakePermissions(web),
            _clock,
            NullLogger<IntegrationManager>.Instance);

    private static InstalledIntegration Npm(string id = "todoist", string version = "13.3.0", bool enabled = true) => new()
    {
        Id = id,
        Name = "Todoist",
        Source = new IntegrationSource(IntegrationSourceKind.OfficialRegistry, "@doist/todoist-mcp"),
        Transport = new IntegrationTransport { Kind = McpTransportKind.Stdio, Command = @"C:\Assistant\Runtimes\node\24.21.0\node.exe", Arguments = [@"C:\Assistant\Integrations\todoist\13.3.0\app\index.js"] },
        InstalledVersion = version,
        Enabled = enabled,
        Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["add-tasks", "find-tasks"] },
        Managed = new ManagedInstall
        {
            Kind = InstallSourceKind.Npm,
            Package = "@doist/todoist-mcp",
            Trust = CandidateTrust.VerifiedVendor,
            Publisher = "Doist",
            Repository = "https://github.com/Doist/todoist-mcp",
            Runtime = RuntimeKind.NodeJs,
            RuntimeVersion = "24.21.0",
            InstalledAt = Reviewable.Now,
        },
    };

    private static InstallCandidate NewerCandidate(string version = "13.4.0")
    {
        var source = new InstallSource
        {
            Kind = InstallSourceKind.Npm,
            Identifier = "@doist/todoist-mcp",
            Version = version,
            DownloadUrl = $"https://registry.npmjs.org/@doist/todoist-mcp/-/todoist-mcp-{version}.tgz",
            Hash = new ContentHash("sha512", new string('b', 128)),
        };
        return new InstallCandidate
        {
            Id = "todoist",
            AppName = "Todoist",
            Name = "@doist/todoist-mcp",
            Source = source,
            Fingerprint = InstallCandidate.FingerprintOf("todoist", "Todoist", source),
        };
    }

    // ---- what Settings shows ----

    [Fact]
    public async Task EachInstalledIntegrationIsDescribedWithoutAnythingBeingStartedOrSent()
    {
        await _fixture.Registry.AddAsync(Npm());
        await _fixture.Registry.AddAsync(Sample.Remote("notes", "Notes") with { InstalledVersion = null });

        var list = await _manager.ListAsync();

        Assert.Equal(["todoist", "notes"], list.Select(info => info.Id).ToArray());
        var todoist = list[0];
        Assert.Equal("Todoist", todoist.Name);
        Assert.Equal("Official MCP registry: @doist/todoist-mcp", todoist.Source);
        Assert.Equal("13.3.0", todoist.Version);
        Assert.True(todoist.Enabled);
        Assert.True(todoist.IsManaged);
        Assert.True(todoist.CanUpdate);
        Assert.True(todoist.IsLocalProgram);
        Assert.Equal(2, todoist.ToolCount);
        Assert.Equal("Not started yet. It starts when you need it.", todoist.Health);
        Assert.StartsWith("Runs on this PC", todoist.Permissions, StringComparison.Ordinal);
        Assert.Contains("asks before it changes anything", todoist.Permissions, StringComparison.Ordinal);
        Assert.Equal("\u2014", list[1].Version);
        Assert.False(list[1].IsManaged);
        Assert.False(list[1].CanUpdate);
        Assert.Equal("Added by you", list[1].Source);
        Assert.Empty(_metadata.Asked);
        Assert.Empty(_fixture.Connections.Reconnected);
        Assert.Equal(0, _fixture.Clients.CreateCalls);
    }

    [Theory]
    [InlineData(IntegrationHealthStatus.Healthy, "Connected", IntegrationHealthLevel.Good)]
    [InlineData(IntegrationHealthStatus.Unknown, "Not started yet. It starts when you need it.", IntegrationHealthLevel.Unknown)]
    [InlineData(IntegrationHealthStatus.AuthRequired, "Needs sign-in", IntegrationHealthLevel.Warning)]
    [InlineData(IntegrationHealthStatus.Unreachable, "Cannot be reached", IntegrationHealthLevel.Problem)]
    [InlineData(IntegrationHealthStatus.Incompatible, "Speaks a version of the protocol I do not understand", IntegrationHealthLevel.Problem)]
    [InlineData(IntegrationHealthStatus.Failed, "Something went wrong", IntegrationHealthLevel.Problem)]
    public async Task HowItIsDoingIsSaidInWords(IntegrationHealthStatus status, string text, IntegrationHealthLevel level)
    {
        await _fixture.Registry.AddAsync(Npm() with { Health = new IntegrationHealth { Status = status, CheckedAt = Reviewable.Now } });

        var info = Assert.Single(await _manager.ListAsync());

        Assert.Equal(text, info.Health);
        Assert.Equal(level, info.HealthLevel);
    }

    [Fact]
    public async Task ATurnedOffIntegrationAndOneThatNeedsSigningInSaySoFirst()
    {
        await _fixture.Registry.AddAsync(Npm("a", enabled: false));
        await _fixture.Registry.AddAsync(Npm("b") with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.EnvironmentSecret,
                State = IntegrationAuthState.NeedsSignIn,
                Secrets = [new IntegrationSecretBinding("TODOIST_API_KEY", "b.todoist-api-key")],
            },
            Health = new IntegrationHealth { Status = IntegrationHealthStatus.Healthy },
        });

        var list = await _manager.ListAsync();

        Assert.Equal(("Turned off", IntegrationHealthLevel.Unknown), (list[0].Health, list[0].HealthLevel));
        Assert.Equal(("Needs sign-in", IntegrationHealthLevel.Warning), (list[1].Health, list[1].HealthLevel));
        Assert.Contains("uses 1 key", list[1].Permissions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppThatIsConnectedButWhosePermissionIsOffSaysSoInsteadOfLookingFine()
    {
        // A messaging app, connected and healthy, while Messaging is off in Permissions: none of its tools would run.
        await _fixture.Registry.AddAsync(Npm() with
        {
            Health = new IntegrationHealth { Status = IntegrationHealthStatus.Healthy, CheckedAt = Reviewable.Now },
            Permissions = new IntegrationPermissions { RequiredCapability = PermissionCapability.Messaging },
        });

        var off = Assert.Single(await _manager.ListAsync());
        Assert.Equal("Not used: the Messaging permission is off. Turn it on in Settings, under Permissions.", off.Health);
        Assert.Equal(IntegrationHealthLevel.Warning, off.HealthLevel);

        _settings.Current = _settings.Current with { Permissions = _settings.Current.Permissions with { Messaging = true } };
        var on = Assert.Single(await _manager.ListAsync());
        Assert.Equal(("Connected", IntegrationHealthLevel.Good), (on.Health, on.HealthLevel));
    }

    [Fact]
    public async Task WhatItMayDoIsSummarisedWithoutAnySecretOrName()
    {
        await _fixture.Registry.AddAsync(Npm() with
        {
            Permissions = new IntegrationPermissions
            {
                LeavesThisPc = true,
                RequiredCapability = PermissionCapability.Files,
                AllowSideEffects = false,
                ReadOnlyTools = ["find-tasks"],
                BlockedTools = ["a", "b"],
            },
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.EnvironmentSecret,
                Secrets = [new IntegrationSecretBinding("TODOIST_API_KEY", "todoist.todoist-api-key")],
            },
        });

        var info = Assert.Single(await _manager.ListAsync());

        Assert.Equal(
            "Runs on this PC, may use the internet, needs the Files permission, read-only tools only, 1 tool you marked as read-only, 2 tools blocked, uses 1 key.",
            info.Permissions);
        Assert.DoesNotContain("TODOIST_API_KEY", info.Permissions, StringComparison.Ordinal);
        Assert.DoesNotContain("find-tasks", info.Permissions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostedServerSaysItIsReachedOverTheInternet()
    {
        await _fixture.Registry.AddAsync(Sample.Remote("notes", "Notes"));

        var info = Assert.Single(await _manager.ListAsync());

        Assert.StartsWith("Reached over the internet", info.Permissions, StringComparison.Ordinal);
        Assert.False(info.IsLocalProgram);
    }

    // ---- enable and disable ----

    [Fact]
    public async Task TurningOffEndsItsProgramAtOnceAndKeepsItInstalled()
    {
        await _fixture.Registry.AddAsync(Npm());

        await _manager.SetEnabledAsync("todoist", false);

        Assert.False((await _fixture.Registry.GetAsync("todoist"))!.Enabled);
        Assert.Equal(["todoist"], _fixture.Connections.Forgotten);
        Assert.Equal("Turned off", Assert.Single(await _manager.ListAsync()).Health);
    }

    [Fact]
    public async Task TurningOnChangesOnlyTheRecordAndStartsNothing()
    {
        await _fixture.Registry.AddAsync(Npm(enabled: false));

        await _manager.SetEnabledAsync("todoist", true);

        Assert.True((await _fixture.Registry.GetAsync("todoist"))!.Enabled);
        Assert.Empty(_fixture.Connections.Forgotten);
        Assert.Empty(_fixture.Connections.Reconnected);
        Assert.Equal(0, _fixture.Clients.CreateCalls);
    }

    [Fact]
    public async Task TurningAnUnknownIntegrationOnOrOffIsAnErrorTheCallerSees()
    {
        await Assert.ThrowsAsync<IntegrationException>(() => _manager.SetEnabledAsync("nobody", true));
    }

    // ---- what the user lets one integration do (step 119) ----

    [Fact]
    public async Task WhatAnIntegrationMayDoIsShownWithTheChoicesThatApplyToIt()
    {
        await _fixture.Registry.AddAsync(Npm());
        await _fixture.Registry.AddAsync(Sample.Remote("notes", "Notes") with
        {
            Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.BearerToken, Secrets = [new IntegrationSecretBinding("Authorization", "notes.token")] },
        });

        var list = await _manager.ListAsync();

        var program = list[0].Access;
        Assert.True(program.Reads && program.Changes && program.Network && program.Account && program.Updates);
        Assert.False(program.NetworkApplies);
        Assert.False(program.AccountApplies);
        Assert.True(program.UpdatesApply);
        var hosted = list[1].Access;
        Assert.True(hosted.NetworkApplies);
        Assert.True(hosted.AccountApplies);
        Assert.False(hosted.UpdatesApply);
    }

    [Fact]
    public async Task ChangingWhatItMayDoIsSavedAtOnceKeepsTheOtherChoicesAndReturnsTheNewState()
    {
        await _fixture.Registry.AddAsync(Npm() with { Permissions = new IntegrationPermissions { ReadOnlyTools = ["find-tasks"] } });

        var info = await _manager.SetAccessAsync("todoist", new IntegrationAccessChange { Reads = false, Updates = false });

        var saved = (await _fixture.Registry.GetAsync("todoist"))!.Permissions;
        Assert.False(saved.AllowReads);
        Assert.False(saved.AllowUpdates);
        Assert.True(saved.AllowSideEffects);
        Assert.True(saved.AllowNetwork);
        Assert.Equal(["find-tasks"], saved.ReadOnlyTools);
        Assert.False(info!.Access.Reads);
        Assert.False(info.Access.Updates);
        Assert.Contains("only tools that change things", info.Permissions, StringComparison.Ordinal);
        Assert.Contains("updates are off", info.Permissions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIsNoLongerAllowedEndsTheConnectionAtOnceAndWhatIsAllowedAgainDoesNot()
    {
        await _fixture.Registry.AddAsync(Npm());

        await _manager.SetAccessAsync("todoist", new IntegrationAccessChange { Network = false });
        Assert.Equal(["todoist"], _fixture.Connections.Forgotten);

        await _manager.SetAccessAsync("todoist", new IntegrationAccessChange { Network = true, Updates = true });
        Assert.Equal(["todoist"], _fixture.Connections.Forgotten);
    }

    [Fact]
    public async Task ChangingWhatAnUnknownIntegrationMayDoChangesNothing()
    {
        Assert.Null(await _manager.SetAccessAsync("nobody", new IntegrationAccessChange { Reads = false }));
        Assert.Empty(_fixture.Connections.Forgotten);
    }

    [Fact]
    public async Task AnIntegrationWhoseUpdatesAreOffIsNeitherLookedUpNorOffered()
    {
        await _fixture.Registry.AddAsync(Npm());
        await _fixture.Registry.AddAsync(Npm("other", "1.0.0") with { Permissions = new IntegrationPermissions { AllowUpdates = false } });
        _metadata.Npm["@doist/todoist-mcp"] = Reviewable.NpmFactsFor(version: "13.4.0");

        var summary = await _manager.CheckForUpdatesAsync(force: true);
        var prepared = await _manager.PrepareUpdateAsync("other");

        Assert.Equal(1, summary.Checked);
        Assert.Equal(UpdatePreparationStatus.Blocked, prepared.Status);
        Assert.Contains("Updates are turned off", prepared.Message, StringComparison.Ordinal);
        Assert.Empty(_reviewer.Updates);
        Assert.Empty(_offers.Updates);
    }

    // ---- reconnect ----

    [Fact]
    public async Task ReconnectingAsksTheConnectionsToStartItAgainAndSaysHowManyToolsItOffers()
    {
        await _fixture.Registry.AddAsync(Npm());
        _fixture.Connections.ReconnectResult = new ConnectionCheck(true, false, null, 5, true);

        var outcome = await _manager.ReconnectAsync("todoist");

        Assert.True(outcome.Connected);
        Assert.Equal("Todoist is connected. It offers 5 tools.", outcome.Message);
        Assert.Equal(5, outcome.ToolCount);
        Assert.Equal(["todoist"], _fixture.Connections.Reconnected);
    }

    [Fact]
    public async Task ReconnectingToAnIntegrationThatIsOffOrGoneDoesNotStartAnything()
    {
        await _fixture.Registry.AddAsync(Npm(enabled: false));

        var off = await _manager.ReconnectAsync("todoist");
        var gone = await _manager.ReconnectAsync("nobody");

        Assert.False(off.Connected);
        Assert.Contains("turned off", off.Message, StringComparison.Ordinal);
        Assert.False(gone.Connected);
        Assert.Contains("not installed", gone.Message, StringComparison.Ordinal);
        Assert.Empty(_fixture.Connections.Reconnected);
    }

    [Theory]
    [InlineData(McpFailure.LaunchFailed, "program could not be started")]
    [InlineData(McpFailure.AuthRequired, "sign in")]
    [InlineData(McpFailure.Unsupported, "do not understand")]
    [InlineData(McpFailure.TimedOut, "did not answer in time")]
    [InlineData(McpFailure.ConnectFailed, "could not be reached")]
    public async Task AReconnectThatFailsSaysWhyInWordsAndNeverWithTheServersWords(McpFailure failure, string words)
    {
        await _fixture.Registry.AddAsync(Npm());
        _fixture.Connections.ReconnectResult = new ConnectionCheck(false, false, failure, 0, false);

        var outcome = await _manager.ReconnectAsync("todoist");

        Assert.False(outcome.Connected);
        Assert.Contains(words, outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReconnectThatIsBlockedSaysLocalOnlyIsOn()
    {
        await _fixture.Registry.AddAsync(Npm());
        _fixture.Connections.ReconnectResult = new ConnectionCheck(false, true, McpFailure.Blocked, 0, false);

        var outcome = await _manager.ReconnectAsync("todoist");

        Assert.Contains("Local Only mode is on", outcome.Message, StringComparison.Ordinal);
    }

    // ---- looking for newer versions ----

    [Fact]
    public async Task WhileUpdateCheckingIsOffNothingIsLookedUpAnywhere()
    {
        await _fixture.Registry.AddAsync(Npm());
        var off = Manager(new FixedSettings());

        var summary = await off.CheckForUpdatesAsync(force: true);

        Assert.Equal(UpdateCheckState.Off, summary.State);
        Assert.Empty(_metadata.Asked);
        Assert.Equal(UpdateCheckState.Off, await off.GetUpdateCheckStateAsync());
    }

    [Fact]
    public async Task WhileLocalOnlyIsOnOrTheWebPermissionIsOffNothingIsLookedUpAndTheReasonIsSaid()
    {
        await _fixture.Registry.AddAsync(Npm());
        var localOnly = Manager(new FixedSettings { Current = new AppSettings { Integrations = new IntegrationSettings { CheckForIntegrationUpdates = true }, Privacy = new PrivacySettings { LocalOnly = true } } });
        var noPermission = Manager(_settings, web: false);

        var first = await localOnly.CheckForUpdatesAsync(force: true);
        var second = await noPermission.CheckForUpdatesAsync(force: true);

        Assert.Equal(UpdateCheckState.Blocked, first.State);
        Assert.Equal(UpdateCheckState.Blocked, second.State);
        Assert.Contains("Local Only", first.Message, StringComparison.Ordinal);
        Assert.Empty(_metadata.Asked);
    }

    [Fact]
    public async Task WhenOnItLooksUpOnlyThePackagesTheAssistantInstalledAndOnlyByName()
    {
        await _fixture.Registry.AddAsync(Npm());
        await _fixture.Registry.AddAsync(Npm("pythonic", "0.4.1") with { Managed = Npm().Managed! with { Kind = InstallSourceKind.PyPi, Package = "todoist-mcp" } });
        await _fixture.Registry.AddAsync(Npm("bundled") with { Managed = Npm().Managed! with { Kind = InstallSourceKind.Bundle, Package = "https://github.com/e/r/releases/download/v1/x.mcpb" } });
        await _fixture.Registry.AddAsync(Sample.Remote("notes", "Notes"));
        _metadata.Npm["@doist/todoist-mcp"] = Reviewable.NpmFactsFor(version: "13.4.0");
        _metadata.PyPi["todoist-mcp"] = Reviewable.PyPiFactsFor(version: "0.4.2");

        var summary = await _manager.CheckForUpdatesAsync(force: false);

        Assert.Equal(UpdateCheckState.Ready, summary.State);
        Assert.Equal(2, summary.Checked);
        Assert.Equal(2, summary.Available);
        Assert.Equal("2 updates are available.", summary.Message);
        Assert.Equal(["npm:@doist/todoist-mcp@latest", "pypi:todoist-mcp@latest"], _metadata.Asked.Order().ToArray());
        var list = await _manager.ListAsync();
        Assert.Equal("13.4.0", list.Single(info => info.Id == "todoist").UpdateVersion);
        Assert.Equal("0.4.2", list.Single(info => info.Id == "pythonic").UpdateVersion);
        Assert.Null(list.Single(info => info.Id == "bundled").UpdateVersion);
        Assert.Null(list.Single(info => info.Id == "notes").UpdateVersion);
    }

    [Fact]
    public async Task AVersionThatIsNotNewerIsNotAnUpdateAndAnUpToDateStateIsSaid()
    {
        await _fixture.Registry.AddAsync(Npm(version: "13.4.0"));
        _metadata.Npm["@doist/todoist-mcp"] = Reviewable.NpmFactsFor(version: "13.4.0");

        var summary = await _manager.CheckForUpdatesAsync(force: true);

        Assert.Equal(0, summary.Available);
        Assert.Equal("Everything is up to date.", summary.Message);
        Assert.Null(Assert.Single(await _manager.ListAsync()).UpdateVersion);
    }

    [Fact]
    public async Task TheLookUpIsMadeOnceADayUnlessTheUserAsksAgain()
    {
        await _fixture.Registry.AddAsync(Npm());
        _metadata.Npm["@doist/todoist-mcp"] = Reviewable.NpmFactsFor(version: "13.4.0");

        await _manager.CheckForUpdatesAsync(force: false);
        _clock.Advance(TimeSpan.FromHours(2));
        var again = await _manager.CheckForUpdatesAsync(force: false);
        Assert.Single(_metadata.Asked);
        Assert.Equal(1, again.Available);

        await _manager.CheckForUpdatesAsync(force: true);
        Assert.Equal(2, _metadata.Asked.Count);

        _clock.Advance(TimeSpan.FromHours(25));
        await _manager.CheckForUpdatesAsync(force: false);
        Assert.Equal(3, _metadata.Asked.Count);
    }

    [Fact]
    public async Task ARegistryThatCannotBeReachedIsCountedAndSaid()
    {
        await _fixture.Registry.AddAsync(Npm());
        _metadata.Fails = DiscoveryFailure.Network;

        var summary = await _manager.CheckForUpdatesAsync(force: true);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Available);
        Assert.Contains("could not reach", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookingUpRaisesTheChangedEventAndNeverInstallsAnything()
    {
        await _fixture.Registry.AddAsync(Npm());
        _metadata.Npm["@doist/todoist-mcp"] = Reviewable.NpmFactsFor(version: "13.4.0");
        var raised = 0;
        _manager.Changed += (_, _) => raised++;

        await _manager.CheckForUpdatesAsync(force: true);

        Assert.True(raised >= 1);
        Assert.Equal("13.3.0", (await _fixture.Registry.GetAsync("todoist"))!.InstalledVersion);
        Assert.Equal(0, _fixture.Downloader.Count);
    }

    [Theory]
    [InlineData("13.4.0", "13.3.0", true)]
    [InlineData("14.0.0", "13.9.9", true)]
    [InlineData("13.3.1", "13.3.0", true)]
    [InlineData("13.3.0", "13.3.0", false)]
    [InlineData("13.2.9", "13.3.0", false)]
    [InlineData("13.10.0", "13.9.0", true)]
    [InlineData("2026.8.31", "2026.8.18", true)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("1.0.1", "1.0", true)]
    [InlineData("v2.0.0", "1.9.9", true)]
    [InlineData("2.0.0-beta.1", "1.9.9", true)]
    [InlineData("banana", "1.0.0", false)]
    [InlineData("1.0.0", "banana", false)]
    [InlineData(null, "1.0.0", false)]
    [InlineData("1.0.0", null, false)]
    public void VersionsAreComparedNumberByNumberAndAnOddOneIsNeverNewer(string? candidate, string? current, bool newer)
    {
        Assert.Equal(newer, VersionOrder.IsNewer(candidate, current));
    }

    // ---- preparing an update ----

    [Fact]
    public async Task ANewerVersionThatPassesTheReviewIsOfferedAndNothingIsDownloadedOrChanged()
    {
        await _fixture.Registry.AddAsync(Npm());
        _reviewer.Update = _ => CandidateReview.Accept(NewerCandidate("13.4.0"), []);

        var preparation = await _manager.PrepareUpdateAsync("todoist");

        Assert.Equal(UpdatePreparationStatus.Offered, preparation.Status);
        Assert.Equal("Version 13.4.0 is available.", preparation.Message);
        var offer = preparation.Offer!;
        Assert.Equal(IntegrationOfferKind.Update, offer.Kind);
        Assert.Equal("13.3.0", offer.CurrentVersion);
        Assert.Equal("13.4.0", offer.NewVersion);
        var (candidate, current) = Assert.Single(_offers.Updates);
        Assert.Equal("13.4.0", candidate.Source.Version);
        Assert.Equal("todoist", current.Id);
        Assert.Equal("13.3.0", (await _fixture.Registry.GetAsync("todoist"))!.InstalledVersion);
        Assert.Equal(0, _fixture.Downloader.Count);
    }

    [Fact]
    public async Task TheCandidateReviewedIsWhatTheAssistantKnowsOfTheInstalledPackageNotWhatTheWebSays()
    {
        await _fixture.Registry.AddAsync(Npm() with { Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.EnvironmentSecret, Secrets = [new IntegrationSecretBinding("TODOIST_API_KEY", "x")] } });
        _reviewer.Update = _ => CandidateReview.Accept(NewerCandidate(), []);

        await _manager.PrepareUpdateAsync("todoist");

        var (candidate, app, id) = Assert.Single(_reviewer.Updates);
        Assert.Equal("Todoist", app);
        Assert.Equal("todoist", id);
        Assert.Equal("@doist/todoist-mcp", candidate.Name);
        Assert.Equal(CandidateTrust.VerifiedVendor, candidate.Trust);
        Assert.Equal("Doist", candidate.Publisher);
        var package = Assert.Single(candidate.Packages);
        Assert.Equal(CandidateInstallMethod.Npm, package.Method);
        Assert.Null(package.Version);
        Assert.Equal(["TODOIST_API_KEY"], candidate.RequiredSecrets);
        Assert.Equal(["add-tasks", "find-tasks"], candidate.ToolNames);
        Assert.Contains("mcp-registry", candidate.FoundIn);
    }

    [Fact]
    public async Task WhenTheNewestVersionIsTheOneInstalledItIsUpToDate()
    {
        await _fixture.Registry.AddAsync(Npm(version: "13.4.0"));
        _reviewer.Update = _ => CandidateReview.Accept(NewerCandidate("13.4.0"), []);

        var preparation = await _manager.PrepareUpdateAsync("todoist");

        Assert.Equal(UpdatePreparationStatus.UpToDate, preparation.Status);
        Assert.Null(preparation.Offer);
        Assert.Empty(_offers.Updates);
    }

    [Fact]
    public async Task ANewerVersionThatDoesNotPassTheReviewIsNotOfferedAndSaysWhy()
    {
        await _fixture.Registry.AddAsync(Npm());
        _reviewer.Update = _ => CandidateReview.Reject([ReviewRules.Block(ReviewCode.NoIntegrityHash, "npm publishes no checksum for it.")]);

        var preparation = await _manager.PrepareUpdateAsync("todoist");

        Assert.Equal(UpdatePreparationStatus.NotPassed, preparation.Status);
        Assert.Contains("did not pass my checks: npm publishes no checksum for it.", preparation.Message, StringComparison.Ordinal);
        Assert.Empty(_offers.Updates);
    }

    [Fact]
    public async Task ARegistryThatCannotBeReachedMeansTheUpdateFailedToBeLookedUp()
    {
        await _fixture.Registry.AddAsync(Npm());
        _reviewer.Update = _ => CandidateReview.Reject([ReviewRules.Block(ReviewCode.CouldNotCheck, "I could not reach npm to check the package.")]);

        var preparation = await _manager.PrepareUpdateAsync("todoist");

        Assert.Equal(UpdatePreparationStatus.Failed, preparation.Status);
    }

    [Fact]
    public async Task AnIntegrationNotInstalledFromARegistryCannotBeUpdatedHereAndNothingIsLookedUp()
    {
        await _fixture.Registry.AddAsync(Sample.Remote("notes", "Notes"));

        var preparation = await _manager.PrepareUpdateAsync("notes");
        var missing = await _manager.PrepareUpdateAsync("nobody");

        Assert.Equal(UpdatePreparationStatus.NotUpdatable, preparation.Status);
        Assert.Equal(UpdatePreparationStatus.NotUpdatable, missing.Status);
        Assert.Empty(_reviewer.Updates);
    }

    [Fact]
    public async Task PreparingAnUpdateWhileTheWebIsLockedSaysSoAndLooksNothingUp()
    {
        await _fixture.Registry.AddAsync(Npm());
        var locked = Manager(_settings, web: false);

        var preparation = await locked.PrepareUpdateAsync("todoist");

        Assert.Equal(UpdatePreparationStatus.Blocked, preparation.Status);
        Assert.Empty(_reviewer.Updates);
    }

    [Fact]
    public async Task ThereIsNoWayForTheManagerToInstallOrUpdateOnItsOwn()
    {
        // The installer it is given throws if an install or an update is asked of it; an update is only ever offered here.
        await _fixture.Registry.AddAsync(Npm());
        _reviewer.Update = _ => CandidateReview.Accept(NewerCandidate("13.4.0"), []);
        _metadata.Npm["@doist/todoist-mcp"] = Reviewable.NpmFactsFor(version: "13.4.0");

        await _manager.CheckForUpdatesAsync(force: true);
        await _manager.PrepareUpdateAsync("todoist");
        await _manager.ListAsync();

        Assert.Equal("13.3.0", (await _fixture.Registry.GetAsync("todoist"))!.InstalledVersion);
    }

    // ---- removing ----

    private async Task<string> InstalledOnDisk(string id = "todoist", string version = "13.3.0")
    {
        var directory = Path.Combine(_fixture.Paths.IntegrationsDirectory, id, version, "app", "node_modules");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "x.js"), "// code");
        await _fixture.Registry.AddAsync(Npm(id, version));
        return Path.Combine(_fixture.Paths.IntegrationsDirectory, id);
    }

    [Fact]
    public async Task RemovingEndsItsProgramFirstThenTakesItOutOfTheListAndDeletesItsFilesAndWhatWasKept()
    {
        var folder = await InstalledOnDisk();
        var order = new List<string>();
        _fixture.Connections.OnForget = _ =>
        {
            order.Add(Directory.Exists(folder) && _fixture.Store.Saved.Count == 1 ? "forgot-while-files-and-record-exist" : "forgot-late");
            return Task.CompletedTask;
        };

        var outcome = await _manager.RemoveAsync("todoist");

        Assert.True(outcome.Removed);
        Assert.True(outcome.FilesDeleted);
        Assert.Equal("Todoist was removed.", outcome.Message);
        Assert.Equal(["forgot-while-files-and-record-exist"], order);
        Assert.Empty(_fixture.Store.Saved);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(["todoist"], _toolCache.Removed);
    }

    [Fact]
    public async Task RemovingDeletesTheSecretsItNamed()
    {
        await InstalledOnDisk();
        await _fixture.Registry.UpdateAsync("todoist", integration => integration with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.EnvironmentSecret,
                Secrets = [new IntegrationSecretBinding("TODOIST_API_KEY", "todoist.todoist-api-key")],
            },
        });
        _fixture.Secrets.Secrets["todoist.todoist-api-key"] = "secret-value";

        await _manager.RemoveAsync("todoist");

        Assert.Equal(["todoist.todoist-api-key"], _fixture.Secrets.Deleted);
        Assert.Empty(_fixture.Secrets.Secrets);
    }

    [Fact]
    public async Task RemovingKeepsRuntimesOtherIntegrationsUseAndReleasesTheRest()
    {
        await InstalledOnDisk("todoist");
        await InstalledOnDisk("notion", "1.0.0");

        await _manager.RemoveAsync("todoist");
        var stillUsed = _fixture.Runtimes.LastInUse!;

        Assert.Contains((RuntimeKind.NodeJs, "24.21.0"), stillUsed);

        await _manager.RemoveAsync("notion");
        Assert.Empty(_fixture.Runtimes.LastInUse!);
    }

    [Fact]
    public async Task RemovingOnlyEverDeletesTheFolderWorkedOutFromTheIdNotAPathTheRecordNames()
    {
        var outside = Path.Combine(_fixture.Folder.Path, "the-users-own-files");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "precious.txt"), "keep me");
        await _fixture.Registry.AddAsync(Npm() with
        {
            Transport = new IntegrationTransport { Kind = McpTransportKind.Stdio, Command = @"C:\Assistant\node.exe", Arguments = [Path.Combine(outside, "index.js")], WorkingDirectory = outside },
        });

        var outcome = await _manager.RemoveAsync("todoist");

        Assert.True(outcome.Removed);
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public async Task AnIntegrationTheUserAddedHasNoFilesOfTheAssistantsToDelete()
    {
        var folder = Path.Combine(_fixture.Paths.IntegrationsDirectory, "notes");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "keep.txt"), "x");
        await _fixture.Registry.AddAsync(Sample.Remote("notes", "Notes"));

        var outcome = await _manager.RemoveAsync("notes");

        Assert.True(outcome.Removed);
        Assert.True(File.Exists(Path.Combine(folder, "keep.txt")));
    }

    [Fact]
    public async Task FilesThatAreStillInUseAreSaidAndLeftForTheNextCleanUpWhileTheRecordIsGone()
    {
        var folder = await InstalledOnDisk();
        await using var held = new FileStream(Path.Combine(folder, "13.3.0", "app", "node_modules", "x.js"), FileMode.Open, FileAccess.Read, FileShare.None);

        var outcome = await _manager.RemoveAsync("todoist");

        Assert.True(outcome.Removed);
        Assert.False(outcome.FilesDeleted);
        Assert.Contains("next time the Assistant cleans up", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_fixture.Store.Saved);
    }

    [Fact]
    public async Task RemovingSomethingThatIsNotInstalledIsSaidAndChangesNothing()
    {
        var outcome = await _manager.RemoveAsync("nobody");

        Assert.False(outcome.Removed);
        Assert.Empty(_fixture.Connections.Forgotten);
    }

    [Fact]
    public async Task TheManagerRaisesChangedWhenTheListChanges()
    {
        var raised = 0;
        _manager.Changed += (_, _) => raised++;

        await _fixture.Registry.AddAsync(Npm());
        await _manager.SetEnabledAsync("todoist", false);
        await _manager.RemoveAsync("todoist");

        Assert.True(raised >= 3);
    }

    [Fact]
    public async Task ADisposedManagerNoLongerListensToTheRegistry()
    {
        var raised = 0;
        _manager.Changed += (_, _) => raised++;
        _manager.Dispose();

        await _fixture.Registry.AddAsync(Npm());

        Assert.Equal(0, raised);
    }
}
