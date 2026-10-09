using System.Text.Json;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>
/// What the Assistant does with integrations is in the activity log (PROJECT_SPEC §4.8, step 117): looking for one, offering it, installing and updating it, removing it,
/// turning it on and off, reconnecting it and looking for newer versions; each with what it was done to, how it ended, and what the user answered, and never a key, an address, a
/// path, the text a registry or a program wrote, or what the user asked for.
/// </summary>
public sealed class IntegrationAuditTests : IDisposable
{
    private const string Leak = "https://evil.example/leak?token=SECRET-TOKEN-123";
    private readonly TickingClock _clock = new(Reviewable.Now);
    private readonly AuditLog _log;
    private readonly InstallFixture _fixture = new();

    public IntegrationAuditTests() => _log = new AuditLog(_clock, NullLogger<AuditLog>.Instance);

    public void Dispose() => _fixture.Dispose();

    // A clock that moves a second each time it is read, so that what happens one after another is in that order in the log.
    private sealed class TickingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private async Task<IReadOnlyList<AuditEntry>> EntriesAsync() =>
        [.. (await _log.ListAsync(100)).Select(item => item.Action!).OrderBy(entry => entry.StartedAt).ThenBy(entry => entry.Id)];

    private async Task<AuditEntry> TheOnlyEntryAsync() => Assert.Single(await EntriesAsync());

    private static IntegrationNeed Need(string app = "Todoist", CapabilityAction action = CapabilityAction.Create, string obj = "task") =>
        new(app, app.ToLowerInvariant(), new IntegrationCapability(action, obj), IntegrationNeedSource.Catalog);

    // ---- looking for an integration ----------------------------------------------------------------------------------------

    private sealed class FakeFinder(Func<IntegrationDiscoveryResult> result) : IIntegrationFinder
    {
        public Task<IntegrationDiscoveryResult> FindAsync(IntegrationNeed need, IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(result());
    }

    private AuditedIntegrationFinder Finder(Func<IntegrationDiscoveryResult> result) => new(new FakeFinder(result), _log);

    [Fact]
    public async Task ALookIsRecordedWithTheAppAndWhatWasWanted_AndHowManyWereFound()
    {
        var candidates = new[] { Candidates.Make("a/one"), Candidates.Make("b/two") };
        var finder = Finder(() => new IntegrationDiscoveryResult { Status = DiscoveryStatus.Found, Candidates = candidates });

        await finder.FindAsync(Need());

        var entry = await TheOnlyEntryAsync();
        Assert.Equal(AuditKind.FindIntegration, entry.Kind);
        Assert.Equal("Todoist", entry.Name);
        Assert.Equal("Look for an integration for Todoist (create task): found 2 integrations", entry.Summary);
        Assert.Equal((AuditStatus.Succeeded, RiskLevel.ReadOnly), (entry.Status, entry.Risk!.Value));
        Assert.Null(entry.Confirmation);
        Assert.Null(entry.ErrorCode);
        Assert.NotNull(entry.EndedAt);
    }

    [Fact]
    public async Task OneIntegrationIsSaidInTheSingular_AndNoneThatFitsIsSaid()
    {
        await Finder(() => new IntegrationDiscoveryResult { Status = DiscoveryStatus.Found, Candidates = [Candidates.Make("a/one")] }).FindAsync(Need());
        await Finder(() => new IntegrationDiscoveryResult { Status = DiscoveryStatus.NothingPlausible }).FindAsync(Need("Notion", CapabilityAction.Read, "page"));

        var entries = await EntriesAsync();
        Assert.Equal("Look for an integration for Todoist (create task): found 1 integration", entries[0].Summary);
        Assert.Equal("Look for an integration for Notion (read page): none fits", entries[1].Summary);
        Assert.All(entries, entry => Assert.Equal(AuditStatus.Succeeded, entry.Status));
    }

    [Fact]
    public async Task ALookThatWasNotAllowedIsSkipped_WithTheReason_BecauseNothingWasSent()
    {
        await Finder(() => new IntegrationDiscoveryResult { Status = DiscoveryStatus.Blocked, Block = DiscoveryBlock.LocalOnly }).FindAsync(Need());
        await Finder(() => new IntegrationDiscoveryResult { Status = DiscoveryStatus.Blocked, Block = DiscoveryBlock.PermissionOff }).FindAsync(Need());

        var entries = await EntriesAsync();
        Assert.Equal([(AuditStatus.Skipped, "local_only"), (AuditStatus.Skipped, "permission_off")], entries.Select(entry => (entry.Status, entry.ErrorCode!)));
        Assert.Equal("Not run: Local Only mode is on", AuditText.StatusText(entries[0].Status, entries[0].Confirmation, entries[0].ErrorCode));
    }

    [Fact]
    public async Task ALookThatCouldNotReachAnyPlaceFailed()
    {
        await Finder(() => new IntegrationDiscoveryResult { Status = DiscoveryStatus.Failed }).FindAsync(Need());

        var entry = await TheOnlyEntryAsync();
        Assert.Equal((AuditStatus.Failed, "lookup_failed"), (entry.Status, entry.ErrorCode));
    }

    [Fact]
    public async Task ALookThatThrowsOrIsStoppedIsRecordedAsThat_AndTheFailureStillReachesTheCaller()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Finder(() => throw new InvalidOperationException(Leak)).FindAsync(Need()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Finder(() => throw new OperationCanceledException()).FindAsync(Need()));

        var entries = await EntriesAsync();
        Assert.Equal([AuditStatus.Failed, AuditStatus.Cancelled], entries.Select(entry => entry.Status));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(entries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestForAnyAppThatDoesAThingIsSaidWithoutAnAppName()
    {
        var finder = Finder(() => new IntegrationDiscoveryResult { Status = DiscoveryStatus.NothingPlausible });

        await finder.FindAsync(IntegrationNeed.ForCalendar());

        Assert.Equal("Look for an integration for an app that can read event: none fits", (await TheOnlyEntryAsync()).Summary);
    }

    [Fact]
    public async Task AnAppNameThatLooksLikeAKeyIsNotKept()
    {
        var need = Need("Notion sk-live-abcdefghijklmnopqrstuvwxyz0123456789");

        await Finder(() => new IntegrationDiscoveryResult { Status = DiscoveryStatus.NothingPlausible }).FindAsync(need);

        var entry = await TheOnlyEntryAsync();
        Assert.DoesNotContain("abcdefghijklmnop", entry.Name + entry.Summary, StringComparison.Ordinal);
    }

    // ---- offering and turning down ---------------------------------------------------------------------------------------

    private sealed class FakeOffers : IIntegrationOffers
    {
        public List<string> Declined { get; } = [];

        public List<string> Accepted { get; } = [];

        private int _made;

        public Task<IntegrationOffer> OfferAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Offer("offer-" + ++_made, candidate.AppName, candidate.Version));

        public Task<IntegrationOffer> OfferConnectAsync(KnownEndpoint endpoint, InstalledIntegration? existing, IntegrationCapability? capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(Offer("offer-" + ++_made, endpoint.Name, null, existing is null ? IntegrationOfferKind.Connect : IntegrationOfferKind.SignIn));

        public Task<IntegrationOffer> OfferUpdateAsync(InstallCandidate candidate, InstalledIntegration current, CancellationToken cancellationToken = default) =>
            Task.FromResult(Offer("offer-" + ++_made, candidate.AppName, candidate.Version, IntegrationOfferKind.Update));

        public Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Accepted.Add(offerId);
            return Task.FromResult(InstallOutcome.Fail(InstallFailure.OfferExpired, "expired"));
        }

        public void Decline(string offerId) => Declined.Add(offerId);

        private static IntegrationOffer Offer(string id, string app, string? version, IntegrationOfferKind kind = IntegrationOfferKind.Install) => new()
        {
            OfferId = id,
            Kind = kind,
            AppName = app,
            IntegrationName = "example/notes-mcp",
            MakerText = "Community-made",
            Provides = "Create a note",
            Source = "bundle " + Leak,
            NewVersion = version,
        };
    }

    private static InstallCandidate Candidate(string version = "1.0.0", string app = "Notes") =>
        SampleBundle.Candidate(SampleBundle.Bytes(version), version, "notes", app);

    [Fact]
    public async Task AnOfferIsRecordedWithTheAppAndVersion_AndNothingWasDownloaded()
    {
        var offers = new AuditedIntegrationOffers(new FakeOffers(), _log);

        var offer = await offers.OfferAsync(Candidate());

        Assert.Equal("offer-1", offer.OfferId);
        var entry = await TheOnlyEntryAsync();
        Assert.Equal(AuditKind.OfferIntegration, entry.Kind);
        Assert.Equal("Offer to install Notes 1.0.0", entry.Summary);
        Assert.Equal((AuditStatus.Succeeded, RiskLevel.ReadOnly), (entry.Status, entry.Risk!.Value));
        Assert.DoesNotContain("evil", JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpdateOfferIsRecordedAsAnUpdate()
    {
        var offers = new AuditedIntegrationOffers(new FakeOffers(), _log);

        await offers.OfferUpdateAsync(Candidate("2.0.0"), null!);

        Assert.Equal("Offer to update Notes to 2.0.0", (await TheOnlyEntryAsync()).Summary);
    }

    [Fact]
    public async Task TurningAnOfferDown_IsRecordedAsTheUsersAnswer_ForTheAppItWasAbout()
    {
        var inner = new FakeOffers();
        var offers = new AuditedIntegrationOffers(inner, _log);
        var offer = await offers.OfferAsync(Candidate());

        offers.Decline(offer.OfferId);

        Assert.Equal([offer.OfferId], inner.Declined);
        var entries = await EntriesAsync();
        Assert.Equal(2, entries.Count);
        var declined = entries[1];
        Assert.Equal(AuditKind.DeclineIntegration, declined.Kind);
        Assert.Equal((AuditStatus.Declined, ConfirmationDecision.Declined), (declined.Status, declined.Confirmation!.Value));
        Assert.Equal("Turn down the offer to install Notes", declined.Summary);
    }

    [Fact]
    public async Task AnOfferThatWasNeverMadeHereIsDeclinedWithoutARecord_AndAcceptingIsNotRecordedByTheBroker()
    {
        var inner = new FakeOffers();
        var offers = new AuditedIntegrationOffers(inner, _log);

        offers.Decline("something-else");
        await offers.AcceptAsync("offer-1");

        Assert.Equal(["something-else"], inner.Declined);
        Assert.Equal(["offer-1"], inner.Accepted);
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task OnlyTheLastFewOffersAreRememberedByName_SoNothingGrowsWithoutLimit()
    {
        var inner = new FakeOffers();
        var offers = new AuditedIntegrationOffers(inner, _log);
        var first = await offers.OfferAsync(Candidate());
        IntegrationOffer last = first;
        for (var i = 0; i < 20; i++)
        {
            last = await offers.OfferAsync(Candidate(app: "Other" + i));
        }

        var before = (await EntriesAsync()).Count;
        offers.Decline(first.OfferId);
        Assert.Equal(before, (await EntriesAsync()).Count);

        offers.Decline(last.OfferId);
        var declined = (await EntriesAsync()).Last();
        Assert.Equal(AuditKind.DeclineIntegration, declined.Kind);
        Assert.Equal("Turn down the offer to install Other19", declined.Summary);
        Assert.Equal([first.OfferId, last.OfferId], inner.Declined);
    }

    // ---- installing and updating -----------------------------------------------------------------------------------------

    private sealed class FakeInstaller(Func<InstallOutcome> outcome) : IIntegrationInstaller
    {
        public Task<InstallPlan> PlanAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) => Task.FromResult(new InstallPlan());

        public Task<InstallOutcome> InstallAsync(InstallCandidate candidate, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(outcome());

        public Task<InstallOutcome> UpdateAsync(InstallCandidate candidate, string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(outcome());

        public Task<int> CleanUpAsync(CancellationToken cancellationToken = default) => Task.FromResult(2);
    }

    private static InstallOutcome Installed() => new() { Status = InstallStatus.Installed, Message = "Installed " + Leak };

    [Fact]
    public async Task AnInstallIsRecordedAsSomethingTheUserAllowed_WithTheAppAndVersion()
    {
        var installer = new AuditedIntegrationInstaller(new FakeInstaller(Installed), _log);

        var outcome = await installer.InstallAsync(Candidate());

        Assert.True(outcome.IsInstalled);
        var entry = await TheOnlyEntryAsync();
        Assert.Equal(AuditKind.InstallIntegration, entry.Kind);
        Assert.Equal("Install Notes 1.0.0", entry.Summary);
        Assert.Equal((AuditStatus.Succeeded, RiskLevel.SideEffect, ConfirmationDecision.Approved), (entry.Status, entry.Risk!.Value, entry.Confirmation!.Value));
        Assert.Null(entry.ErrorCode);
    }

    [Fact]
    public async Task AFailedInstallKeepsTheKindOfFailure_NeverTheMessageItCameWith()
    {
        var installer = new AuditedIntegrationInstaller(new FakeInstaller(() => InstallOutcome.Fail(InstallFailure.HashMismatch, "The file at " + Leak + " is not what was reviewed.")), _log);

        var outcome = await installer.InstallAsync(Candidate());

        Assert.Equal(InstallFailure.HashMismatch, outcome.Failure);
        var entry = await TheOnlyEntryAsync();
        Assert.Equal((AuditStatus.Failed, "hash_mismatch"), (entry.Status, entry.ErrorCode));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(entry), StringComparison.Ordinal);
        Assert.Equal("Didn't work: the download was not what was reviewed", AuditText.StatusText(entry.Status, entry.Confirmation, entry.ErrorCode));
    }

    [Fact]
    public async Task AnInstallTheUserCancelledIsRecordedAsStopped()
    {
        var installer = new AuditedIntegrationInstaller(new FakeInstaller(InstallOutcome.Cancel), _log);

        await installer.InstallAsync(Candidate());

        var entry = await TheOnlyEntryAsync();
        Assert.Equal((AuditStatus.Cancelled, null), (entry.Status, entry.ErrorCode));
    }

    [Fact]
    public async Task AnInstallThatThrowsIsRecordedFailed_AndStillThrows()
    {
        var installer = new AuditedIntegrationInstaller(new FakeInstaller(() => throw new IOException(Leak)), _log);

        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(Candidate()));

        var entry = await TheOnlyEntryAsync();
        Assert.Equal(AuditStatus.Failed, entry.Status);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpdateIsRecordedWithTheVersionItWentTo()
    {
        var installer = new AuditedIntegrationInstaller(new FakeInstaller(Installed), _log);

        await installer.UpdateAsync(Candidate("2.0.0"), "notes");

        var entry = await TheOnlyEntryAsync();
        Assert.Equal(AuditKind.UpdateIntegration, entry.Kind);
        Assert.Equal("Update Notes to 2.0.0", entry.Summary);
    }

    [Fact]
    public async Task PlanningAndCleaningUpAreNotRecorded()
    {
        var installer = new AuditedIntegrationInstaller(new FakeInstaller(Installed), _log);

        await installer.PlanAsync(Candidate());
        Assert.Equal(2, await installer.CleanUpAsync());

        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public void EveryWayAnInstallCanFailHasWordsInTheLog()
    {
        foreach (var failure in Enum.GetValues<InstallFailure>().Where(failure => failure != InstallFailure.None))
        {
            var code = IntegrationAudit.CodeOf(failure);
            Assert.NotNull(code);
            Assert.True(AuditText.HasReason(code), $"{failure} has no words");
            Assert.NotEqual("something went wrong", AuditText.Reason(code));
        }

        Assert.Null(IntegrationAudit.CodeOf(InstallFailure.None));
    }

    [Fact]
    public async Task TheRealInstallerIsRecordedEndToEnd_AnInstallThatWorked_AndOneWhoseDownloadWasNotWhatWasReviewed()
    {
        var installer = new AuditedIntegrationInstaller(_fixture.Installer, _log);
        var good = _fixture.Serve();

        var installed = await installer.InstallAsync(good);

        Assert.True(installed.IsInstalled);

        using var broken = new InstallFixture();
        var tampered = broken.Serve(id: "tampered");
        broken.Downloader.Files[tampered.Source.DownloadUrl!] = SampleBundle.Bytes("1.0.0", type: "binary", args: "[\"--different\"]");
        var failed = await new AuditedIntegrationInstaller(broken.Installer, _log).InstallAsync(tampered);

        Assert.Equal(InstallFailure.HashMismatch, failed.Failure);
        var entries = await EntriesAsync();
        Assert.Equal([AuditStatus.Succeeded, AuditStatus.Failed], entries.Select(entry => entry.Status));
        Assert.Equal("hash_mismatch", entries[1].ErrorCode);
        Assert.All(entries, entry => Assert.Equal(ConfirmationDecision.Approved, entry.Confirmation));
    }

    // ---- what the user does with installed integrations --------------------------------------------------------------------

    private sealed class FakeManager : IIntegrationManager, IDisposable
    {
        public event EventHandler? Changed;

        public bool Disposed { get; private set; }

        public List<IntegrationInfo> Integrations { get; } = [Info("todoist", "Todoist")];

        public Func<ReconnectOutcome> Reconnect { get; set; } = () => new ReconnectOutcome(true, "Todoist is connected. It offers 4 tools.", 4);

        public Func<UpdateCheckSummary> Check { get; set; } = () => new UpdateCheckSummary(UpdateCheckState.Ready, 2, 1, 0, "1 update is available.");

        public Func<UpdatePreparation> Prepare { get; set; } = () => new UpdatePreparation(UpdatePreparationStatus.UpToDate, "Todoist is up to date.");

        public Func<RemoveOutcome> Remove { get; set; } = () => new RemoveOutcome(true, true, "Todoist was removed.");

        public Exception? Throws { get; set; }

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<IReadOnlyList<IntegrationInfo>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IntegrationInfo>>(Integrations);

        public Task<UpdateCheckState> GetUpdateCheckStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(UpdateCheckState.Ready);

        public Task SetEnabledAsync(string integrationId, bool enabled, CancellationToken cancellationToken = default) =>
            Throws is null ? Task.CompletedTask : Task.FromException(Throws);

        public Task<IntegrationInfo?> SetAccessAsync(string integrationId, IntegrationAccessChange change, CancellationToken cancellationToken = default) =>
            Throws is null ? Task.FromResult(Integrations.FirstOrDefault(info => info.Id == integrationId)) : Task.FromException<IntegrationInfo?>(Throws);

        public Task<ReconnectOutcome> ReconnectAsync(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(Reconnect());

        public Task<UpdateCheckSummary> CheckForUpdatesAsync(bool force, CancellationToken cancellationToken = default) => Task.FromResult(Check());

        public Task<UpdatePreparation> PrepareUpdateAsync(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(Prepare());

        public Task<RemoveOutcome> RemoveAsync(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(Remove());

        public void Dispose() => Disposed = true;

        private static IntegrationInfo Info(string id, string name) => new()
        {
            Id = id,
            Name = name,
            Source = "Community registry",
            Version = "1.0.0",
            Health = "Connected",
            Permissions = "It can read tasks.",
        };
    }

    [Fact]
    public async Task TurningAnIntegrationOnOrOff_IsRecordedWithItsName()
    {
        var manager = new AuditedIntegrationManager(new FakeManager(), _log);

        await manager.SetEnabledAsync("todoist", false);
        await manager.SetEnabledAsync("todoist", true);

        var entries = await EntriesAsync();
        Assert.Equal(["Turn off Todoist", "Turn on Todoist"], entries.Select(entry => entry.Summary));
        Assert.All(entries, entry => Assert.Equal((AuditKind.SwitchIntegration, AuditStatus.Succeeded, RiskLevel.SideEffect), (entry.Kind, entry.Status, entry.Risk!.Value)));
    }

    [Fact]
    public async Task AnIntegrationThatCannotBeFoundInTheListIsNamedByItsId_WhichIsSafe()
    {
        var manager = new AuditedIntegrationManager(new FakeManager(), _log);

        await manager.SetEnabledAsync("gone", false);
        await manager.SetEnabledAsync("Not A Valid Id; DROP", false);

        Assert.Equal(["Turn off gone", "Turn off an integration"], (await EntriesAsync()).Select(entry => entry.Summary));
    }

    [Fact]
    public async Task ASwitchThatFailsIsRecordedFailed_AndStillThrows()
    {
        var inner = new FakeManager { Throws = new IOException(Leak) };
        var manager = new AuditedIntegrationManager(inner, _log);

        await Assert.ThrowsAsync<IOException>(() => manager.SetEnabledAsync("todoist", false));

        var entry = await TheOnlyEntryAsync();
        Assert.Equal(AuditStatus.Failed, entry.Status);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReconnectIsRecordedWithHowManyToolsTheIntegrationOffers_OrThatItCouldNotBeReached()
    {
        var inner = new FakeManager();
        var manager = new AuditedIntegrationManager(inner, _log);

        await manager.ReconnectAsync("todoist");
        inner.Reconnect = () => new ReconnectOutcome(false, "Todoist did not answer at " + Leak, 0);
        await manager.ReconnectAsync("todoist");

        var entries = await EntriesAsync();
        Assert.Equal("Reconnect Todoist: 4 tools", entries[0].Summary);
        Assert.Equal((AuditStatus.Failed, "not_connected"), (entries[1].Status, entries[1].ErrorCode));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(entries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALookForNewerVersionsIsRecordedOnceWithTheCounts_NotAgainForTheAnswerKept()
    {
        var kept = new UpdateCheckSummary(UpdateCheckState.Ready, 2, 1, 0, "1 update is available.");
        var manager = new AuditedIntegrationManager(new FakeManager { Check = () => kept }, _log);

        await manager.CheckForUpdatesAsync(force: true);
        await manager.CheckForUpdatesAsync(force: false);
        await manager.CheckForUpdatesAsync(force: false);

        var entry = await TheOnlyEntryAsync();
        Assert.Equal(AuditKind.CheckIntegrationUpdates, entry.Kind);
        Assert.Equal("Look for newer versions of 2 integrations: 1 available", entry.Summary);
    }

    [Fact]
    public async Task ANewLookIsRecordedAgain_AndOneThatWasOffOrBlockedIsNotARecord()
    {
        var inner = new FakeManager();
        var manager = new AuditedIntegrationManager(inner, _log);
        inner.Check = () => new UpdateCheckSummary(UpdateCheckState.Off, 0, 0, 0, "off");
        await manager.CheckForUpdatesAsync(force: true);
        inner.Check = () => new UpdateCheckSummary(UpdateCheckState.Blocked, 0, 0, 0, "blocked");
        await manager.CheckForUpdatesAsync(force: true);
        Assert.Empty(await EntriesAsync());

        inner.Check = () => new UpdateCheckSummary(UpdateCheckState.Ready, 1, 0, 0, "ok");
        await manager.CheckForUpdatesAsync(force: true);
        inner.Check = () => new UpdateCheckSummary(UpdateCheckState.Ready, 1, 1, 0, "ok");
        await manager.CheckForUpdatesAsync(force: true);

        Assert.Equal(2, (await EntriesAsync()).Count);
    }

    [Fact]
    public async Task ALookThatCouldReachNoRegistryFailed()
    {
        var inner = new FakeManager { Check = () => new UpdateCheckSummary(UpdateCheckState.Ready, 2, 0, 2, "no") };
        var manager = new AuditedIntegrationManager(inner, _log);

        await manager.CheckForUpdatesAsync(force: true);

        var entry = await TheOnlyEntryAsync();
        Assert.Equal((AuditStatus.Failed, "lookup_failed"), (entry.Status, entry.ErrorCode));
    }

    [Theory]
    [InlineData(UpdatePreparationStatus.Offered, AuditStatus.Succeeded, null, "Look for a newer version of Todoist: one is available")]
    [InlineData(UpdatePreparationStatus.UpToDate, AuditStatus.Succeeded, null, "Look for a newer version of Todoist: it is up to date")]
    [InlineData(UpdatePreparationStatus.NotUpdatable, AuditStatus.Skipped, "not_updatable", null)]
    [InlineData(UpdatePreparationStatus.Blocked, AuditStatus.Skipped, "web_locked", null)]
    [InlineData(UpdatePreparationStatus.NotPassed, AuditStatus.Failed, "review_failed", null)]
    [InlineData(UpdatePreparationStatus.Failed, AuditStatus.Failed, "lookup_failed", null)]
    public async Task LookingForOneIntegrationsNewerVersionIsRecordedByHowItEnded(UpdatePreparationStatus status, AuditStatus expected, string? code, string? summary)
    {
        var inner = new FakeManager { Prepare = () => new UpdatePreparation(status, "message " + Leak) };
        var manager = new AuditedIntegrationManager(inner, _log);

        var preparation = await manager.PrepareUpdateAsync("todoist");

        Assert.Equal(status, preparation.Status);
        var entry = await TheOnlyEntryAsync();
        Assert.Equal((AuditKind.FindIntegrationUpdate, expected, code), (entry.Kind, entry.Status, entry.ErrorCode));
        if (summary is not null)
        {
            Assert.Equal(summary, entry.Summary);
        }

        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingAnIntegrationIsRecordedWithItsName()
    {
        var manager = new AuditedIntegrationManager(new FakeManager(), _log);

        await manager.RemoveAsync("todoist");

        var entry = await TheOnlyEntryAsync();
        Assert.Equal((AuditKind.RemoveIntegration, "Remove Todoist", AuditStatus.Succeeded), (entry.Kind, entry.Summary, entry.Status));
    }

    [Fact]
    public async Task ARemovalOfSomethingThatWasNotInstalledIsNotASuccess()
    {
        var inner = new FakeManager { Remove = () => new RemoveOutcome(false, true, "That integration is not installed any more.") };
        var manager = new AuditedIntegrationManager(inner, _log);

        await manager.RemoveAsync("todoist");

        Assert.Equal(AuditStatus.Failed, (await TheOnlyEntryAsync()).Status);
    }

    [Fact]
    public async Task ListingIsNotRecorded_ChangesAreForwarded_AndTheManagerIsDisposedWithTheDecorator()
    {
        var inner = new FakeManager();
        var manager = new AuditedIntegrationManager(inner, _log);
        var changes = 0;
        EventHandler handler = (_, _) => changes++;
        manager.Changed += handler;

        var list = await manager.ListAsync();
        Assert.Equal(UpdateCheckState.Ready, await manager.GetUpdateCheckStateAsync());
        inner.RaiseChanged();
        manager.Changed -= handler;
        inner.RaiseChanged();
        manager.Dispose();

        Assert.Single(list);
        Assert.Equal(1, changes);
        Assert.True(inner.Disposed);
        Assert.Empty(await EntriesAsync());
    }
}
