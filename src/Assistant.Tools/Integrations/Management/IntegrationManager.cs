using System.Globalization;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>How an installed integration is doing, for the Settings page.</summary>
public enum IntegrationHealthLevel
{
    /// <summary>Nothing is known, or it is turned off.</summary>
    Unknown = 0,

    /// <summary>It works.</summary>
    Good = 1,

    /// <summary>It needs something from the user.</summary>
    Warning = 2,

    /// <summary>It cannot be used.</summary>
    Problem = 3,
}

/// <summary>One installed integration as Settings shows it (PROJECT_SPEC §4.8, step 109). Words only: no path, no secret, no tool result.</summary>
public sealed record IntegrationInfo
{
    /// <summary>The integration's id.</summary>
    public required string Id { get; init; }

    /// <summary>The app's name.</summary>
    public required string Name { get; init; }

    /// <summary>Where it came from, in words: <c>Community registry: @x/y</c>.</summary>
    public required string Source { get; init; }

    /// <summary>The version that is installed, or a dash when it has none (a hosted server).</summary>
    public required string Version { get; init; }

    /// <summary>Whether it may be used.</summary>
    public bool Enabled { get; init; }

    /// <summary>How it is doing, in words.</summary>
    public required string Health { get; init; }

    /// <summary>How it is doing, for the colour of the words.</summary>
    public IntegrationHealthLevel HealthLevel { get; init; }

    /// <summary>What it may do and what it needs, in one line.</summary>
    public required string Permissions { get; init; }

    /// <summary>A newer version, when update checking is on and found one.</summary>
    public string? UpdateVersion { get; init; }

    /// <summary>Whether the Assistant installed it and can replace it with a newer version (an npm or PyPI package).</summary>
    public bool CanUpdate { get; init; }

    /// <summary>Whether the Assistant installed it (and so removing it deletes its files).</summary>
    public bool IsManaged { get; init; }

    /// <summary>How many tools it offered when last connected to.</summary>
    public int ToolCount { get; init; }

    /// <summary>Whether it is started by the Assistant on this PC (a program) and not reached over the web.</summary>
    public bool IsLocalProgram { get; init; }

    /// <summary>What the user lets it do, and which of those choices apply to it (step 119).</summary>
    public IntegrationAccess Access { get; init; } = new();

    /// <summary>Whether it is reached over HTTP, so that the user can sign in to it (in the browser) or give it an access token.</summary>
    public bool CanSignIn { get; init; }

    /// <summary>Whether a sign-in is in place and was accepted.</summary>
    public bool SignedIn { get; init; }

    /// <summary>Whether it needs the user to sign in (never done, run out, or refused).</summary>
    public bool NeedsSignIn { get; init; }

    /// <summary>The names of the keys it is given by the user (a program's environment variables, an HTTP header's name), each of which Settings has a box for; empty for one that needs none.</summary>
    public IReadOnlyList<string> KeyNames { get; init; } = [];
}

/// <summary>
/// What the user lets one integration do, as Settings shows it (PROJECT_SPEC §4.8, §4.9, step 119): whether the Assistant may use its tools that read, its tools that change
/// something (each use is still confirmed), connect to it over a network, use the account it signs in to, and look for a newer version of it. The <c>Applies</c> members say
/// which choices mean something for this integration, so the page offers only those: network access for one that reaches out, account access for one that signs in, updates for
/// one the Assistant installed from a registry.
/// </summary>
public sealed record IntegrationAccess
{
    /// <summary>The tools known to only read may be used.</summary>
    public bool Reads { get; init; } = true;

    /// <summary>The tools that change something may be used, after the user confirms each call.</summary>
    public bool Changes { get; init; } = true;

    /// <summary>The Assistant may connect to it over a network.</summary>
    public bool Network { get; init; } = true;

    /// <summary>It may use the account it signs in to.</summary>
    public bool Account { get; init; } = true;

    /// <summary>The Assistant may look for a newer version of it and offer it.</summary>
    public bool Updates { get; init; } = true;

    /// <summary>Whether the network choice means something: it is reached over a network, or is recorded as reaching out itself.</summary>
    public bool NetworkApplies { get; init; }

    /// <summary>Whether the account choice means something: it signs in.</summary>
    public bool AccountApplies { get; init; }

    /// <summary>Whether the update choice means something: the Assistant installed it from a package registry.</summary>
    public bool UpdatesApply { get; init; }
}

/// <summary>A change to what an integration may do: each member that is not <see langword="null"/> is the new value of that choice, and the others are left as they are.</summary>
public sealed record IntegrationAccessChange
{
    /// <summary>The tools known to only read may be used.</summary>
    public bool? Reads { get; init; }

    /// <summary>The tools that change something may be used.</summary>
    public bool? Changes { get; init; }

    /// <summary>The Assistant may connect to it over a network.</summary>
    public bool? Network { get; init; }

    /// <summary>It may use the account it signs in to.</summary>
    public bool? Account { get; init; }

    /// <summary>The Assistant may look for a newer version of it.</summary>
    public bool? Updates { get; init; }
}

/// <summary>Whether looking for newer versions is possible now.</summary>
public enum UpdateCheckState
{
    /// <summary>The user has not turned update checking on.</summary>
    Off = 0,

    /// <summary>It is on, but Local Only mode is on or the External Web and Image Search permission is off.</summary>
    Blocked = 1,

    /// <summary>It is on and nothing stops it.</summary>
    Ready = 2,
}

/// <summary>What looking for newer versions found.</summary>
/// <param name="State">Whether it was possible.</param>
/// <param name="Checked">How many integrations were looked up.</param>
/// <param name="Available">How many have a newer version.</param>
/// <param name="Failed">How many could not be looked up.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record UpdateCheckSummary(UpdateCheckState State, int Checked, int Available, int Failed, string Message);

/// <summary>How preparing an update ended.</summary>
public enum UpdatePreparationStatus
{
    /// <summary>A newer version passed the review and is offered for the user to approve.</summary>
    Offered = 0,

    /// <summary>There is no newer version.</summary>
    UpToDate = 1,

    /// <summary>The integration is not one the Assistant installs from a registry, so it cannot be updated here.</summary>
    NotUpdatable = 2,

    /// <summary>Local Only mode is on or the External Web and Image Search permission is off.</summary>
    Blocked = 3,

    /// <summary>The registry could not be reached.</summary>
    Failed = 4,

    /// <summary>A newer version exists but did not pass the review.</summary>
    NotPassed = 5,
}

/// <summary>An update the user may approve, or why there is none.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Message">What to tell the user.</param>
/// <param name="Offer">The offer, for <see cref="UpdatePreparationStatus.Offered"/>.</param>
public sealed record UpdatePreparation(UpdatePreparationStatus Status, string Message, IntegrationOffer? Offer = null);

/// <summary>How a reconnect went.</summary>
/// <param name="Connected">Whether the integration answered.</param>
/// <param name="Message">What to tell the user.</param>
/// <param name="ToolCount">How many tools it offers.</param>
public sealed record ReconnectOutcome(bool Connected, string Message, int ToolCount);

/// <summary>How removing an integration went.</summary>
/// <param name="Removed">Whether it was installed and is gone.</param>
/// <param name="FilesDeleted">Whether the files the Assistant kept for it are gone too (always true for one that has none).</param>
/// <param name="Message">What to tell the user.</param>
public sealed record RemoveOutcome(bool Removed, bool FilesDeleted, string Message);

/// <summary>Looks after the integrations the Assistant installed: shows them, turns them on and off, reconnects, updates and removes them (PROJECT_SPEC §4.8, step 109).</summary>
public interface IIntegrationManager
{
    /// <summary>Raised when the list or what Settings shows of it changed (an integration was installed, changed or removed, or newer versions were looked for).</summary>
    event EventHandler? Changed;

    /// <summary>Every installed integration, as Settings shows it. Nothing is started and nothing is sent.</summary>
    Task<IReadOnlyList<IntegrationInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether looking for newer versions is possible now.</summary>
    Task<UpdateCheckState> GetUpdateCheckStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the integration on or off. Turning it off ends its program at once and none of its tools is offered until it is turned on; it stays installed.
    /// </summary>
    Task SetEnabledAsync(string integrationId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes what the integration may do (step 119): its tools that read, its tools that change something, network access, its account and updates. Each change is the
    /// user's own and is made at once: a connection that is no longer allowed ends now and the tools that are no longer allowed are not offered again. Returns the integration as
    /// Settings shows it afterwards, or <see langword="null"/> when it is not installed any more.
    /// </summary>
    Task<IntegrationInfo?> SetAccessAsync(string integrationId, IntegrationAccessChange change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the connection, forgets what was kept of its tools, starts its program again and reads its tools: what the user asks for with Reconnect. It is
    /// the only thing that starts a program to check on it.
    /// </summary>
    Task<ReconnectOutcome> ReconnectAsync(string integrationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the latest version of each integration the Assistant installed from a registry, sending only the names of the packages. It does nothing
    /// unless the user turned update checking on, and, like looking for an integration, only while Local Only mode is off and the External Web and Image
    /// Search permission is on. Unless <paramref name="force"/>, it does nothing when it looked within the last day. It never installs anything.
    /// </summary>
    Task<UpdateCheckSummary> CheckForUpdatesAsync(bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// Works out the update of an integration: looks up and reviews the newest version, and offers it. Nothing is downloaded or run; the update is made only
    /// when the user approves the offer (<see cref="IIntegrationOffers.AcceptAsync"/>).
    /// </summary>
    Task<UpdatePreparation> PrepareUpdateAsync(string integrationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the integration: its program is ended, it is taken out of the list with the secrets it named, the files the Assistant kept for it are deleted, and
    /// a runtime no other integration uses is deleted. The app it connects to is never touched.
    /// </summary>
    Task<RemoveOutcome> RemoveAsync(string integrationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The lifecycle of the integrations the Assistant installed (PROJECT_SPEC §4.8, step 109). Installed integrations are reused: a request finds them in the
/// registry (step 105) and nothing is searched for or downloaded again, a program is started lazily when a tool is called, its tools are kept so that
/// reconnecting is fast, and a crashed program is started again by the next use. Nothing is ever replaced by a newer version from the internet on its own:
/// newer versions are only looked for when the user turned that on, only offered, and made only when the user approves. Removing an integration deletes what
/// the Assistant kept for it and nothing of the app it connects to. Logs say ids and counts only.
/// </summary>
internal sealed partial class IntegrationManager : IIntegrationManager, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LookUpTimeout = TimeSpan.FromSeconds(12);

    private readonly IInstalledIntegrationRegistry _registry;
    private readonly IIntegrationConnections _connections;
    private readonly IIntegrationInstaller _installer;
    private readonly IManagedRuntimes _runtimes;
    private readonly IntegrationLayout _layout;
    private readonly IMcpToolCache? _toolCache;
    private readonly IPackageMetadata _metadata;
    private readonly ICandidateReviewer _reviewer;
    private readonly IIntegrationOffers _offers;
    private readonly ISettingsService _settings;
    private readonly IPermissionPolicy _permissions;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntegrationManager> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _latest = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private UpdateCheckSummary? _lastSummary;

    /// <summary>Creates the manager.</summary>
    public IntegrationManager(
        IInstalledIntegrationRegistry registry,
        IIntegrationConnections connections,
        IIntegrationInstaller installer,
        IManagedRuntimes runtimes,
        IntegrationLayout layout,
        IMcpToolCache? toolCache,
        IPackageMetadata metadata,
        ICandidateReviewer reviewer,
        IIntegrationOffers offers,
        ISettingsService settings,
        IPermissionPolicy permissions,
        TimeProvider clock,
        ILogger<IntegrationManager> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _connections = connections;
        _installer = installer;
        _runtimes = runtimes;
        _layout = layout;
        _toolCache = toolCache;
        _metadata = metadata;
        _reviewer = reviewer;
        _offers = offers;
        _settings = settings;
        _permissions = permissions;
        _clock = clock;
        _logger = logger;
        _registry.Changed += OnRegistryChanged;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public void Dispose() => _registry.Changed -= OnRegistryChanged;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IntegrationInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> latest;
        lock (_gate)
        {
            latest = new Dictionary<string, string>(_latest, StringComparer.Ordinal);
        }

        var permissions = (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Permissions;
        return [.. records.Select(record => Describe(record, latest.GetValueOrDefault(record.Id), permissions))];
    }

    /// <inheritdoc/>
    public async Task<UpdateCheckState> GetUpdateCheckStateAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Integrations.CheckForIntegrationUpdates)
        {
            return UpdateCheckState.Off;
        }

        return await WebAllowedAsync(settings, cancellationToken).ConfigureAwait(false) ? UpdateCheckState.Ready : UpdateCheckState.Blocked;
    }

    /// <inheritdoc/>
    public async Task SetEnabledAsync(string integrationId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _registry.SetEnabledAsync(integrationId, enabled, cancellationToken).ConfigureAwait(false);
        if (!enabled)
        {
            // Turned off: its program ends now, and the tools it listed are not offered.
            await _connections.ForgetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<IntegrationInfo?> SetAccessAsync(string integrationId, IntegrationAccessChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var updated = await _registry.UpdateAsync(
            integrationId,
            record => record with
            {
                Permissions = record.Permissions with
                {
                    AllowReads = change.Reads ?? record.Permissions.AllowReads,
                    AllowSideEffects = change.Changes ?? record.Permissions.AllowSideEffects,
                    AllowNetwork = change.Network ?? record.Permissions.AllowNetwork,
                    AllowAccountAccess = change.Account ?? record.Permissions.AllowAccountAccess,
                    AllowUpdates = change.Updates ?? record.Permissions.AllowUpdates,
                },
            },
            cancellationToken).ConfigureAwait(false);

        // What is no longer allowed ends now: its program is stopped and the tools it listed are forgotten, so that the next use is made under the new rules.
        if (change.Reads == false || change.Changes == false || change.Network == false || change.Account == false)
        {
            await _connections.ForgetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        }

        LogAccessChanged(_logger, integrationId);
        string? latest;
        lock (_gate)
        {
            _latest.TryGetValue(integrationId, out latest);
        }

        return Describe(updated, latest, (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Permissions);
    }

    /// <inheritdoc/>
    public async Task<ReconnectOutcome> ReconnectAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        var record = await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new ReconnectOutcome(false, "That integration is not installed any more.", 0);
        }

        if (!record.Enabled)
        {
            return new ReconnectOutcome(false, $"{record.Name} is turned off. Turn it on first.", 0);
        }

        var check = await _connections.ReconnectAsync(integrationId, cancellationToken).ConfigureAwait(false);
        var outcome = check switch
        {
            { Connected: true } => new ReconnectOutcome(
                true,
                check.ToolCount == 1 ? $"{record.Name} is connected. It offers 1 tool." : $"{record.Name} is connected. It offers {check.ToolCount.ToString(CultureInfo.InvariantCulture)} tools.",
                check.ToolCount),
            { Blocked: true } => new ReconnectOutcome(false, BlockedText(record), 0),
            _ => new ReconnectOutcome(false, FailureText(record, check.Failure), 0),
        };
        LogReconnected(_logger, integrationId, outcome.Connected);
        return outcome;
    }

    /// <inheritdoc/>
    public async Task<UpdateCheckSummary> CheckForUpdatesAsync(bool force, CancellationToken cancellationToken = default)
    {
        var state = await GetUpdateCheckStateAsync(cancellationToken).ConfigureAwait(false);
        if (state == UpdateCheckState.Off)
        {
            return new UpdateCheckSummary(state, 0, 0, 0, "Checking for updates is off.");
        }

        if (state == UpdateCheckState.Blocked)
        {
            return new UpdateCheckSummary(
                state, 0, 0, 0, "Checking for updates needs the web: turn Local Only mode off in Settings > Privacy and allow External Web and Image Search in Settings > Permissions.");
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!force && now - _lastCheck < CheckInterval && _lastSummary is { } kept)
            {
                return kept;
            }
        }

        var records = (await _registry.ListAsync(cancellationToken).ConfigureAwait(false)).Where(record => CanLookUp(record) && record.Permissions.AllowUpdates).ToList();
        var results = await Task.WhenAll(records.Select(record => LookUpAsync(record, cancellationToken))).ConfigureAwait(false);
        var available = 0;
        var failed = 0;
        lock (_gate)
        {
            for (var index = 0; index < records.Count; index++)
            {
                if (results[index] is not { } latest)
                {
                    failed++;
                    continue;
                }

                _latest[records[index].Id] = latest;
                if (VersionOrder.IsNewer(latest, records[index].InstalledVersion))
                {
                    available++;
                }
            }

            _lastCheck = now;
            _lastSummary = new UpdateCheckSummary(
                UpdateCheckState.Ready,
                records.Count,
                available,
                failed,
                records.Count == 0
                    ? "There is nothing installed that can be checked."
                    : failed == records.Count
                        ? "I could not reach the registries to look. Try again later."
                        : available == 0 ? "Everything is up to date." : available == 1 ? "1 update is available." : $"{available.ToString(CultureInfo.InvariantCulture)} updates are available.");
        }

        LogChecked(_logger, records.Count, available, failed);
        Changed?.Invoke(this, EventArgs.Empty);
        return _lastSummary!;
    }

    /// <inheritdoc/>
    public async Task<UpdatePreparation> PrepareUpdateAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        var record = await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        if (record is null || !CanLookUp(record))
        {
            return new UpdatePreparation(
                UpdatePreparationStatus.NotUpdatable, "I can only update the integrations I installed from a package registry. This one has to be updated where you set it up.");
        }

        if (!record.Permissions.AllowUpdates)
        {
            return new UpdatePreparation(
                UpdatePreparationStatus.Blocked, $"Updates are turned off for {record.Name} in its permissions here, so nothing was looked up. Turn them on to look for a newer version.");
        }

        if (!await WebAllowedAsync(await _settings.LoadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
        {
            return new UpdatePreparation(
                UpdatePreparationStatus.Blocked, "Updating needs the web: turn Local Only mode off in Settings > Privacy and allow External Web and Image Search in Settings > Permissions.");
        }

        var managed = record.Managed!;
        var review = await _reviewer.ReviewUpdateAsync(Synthesize(record, managed), record.Name, record.Id, cancellationToken).ConfigureAwait(false);
        if (!review.IsAccepted)
        {
            var reason = review.Blockers.FirstOrDefault();
            return reason is { Code: ReviewCode.CouldNotCheck or ReviewCode.WebLocked }
                ? new UpdatePreparation(UpdatePreparationStatus.Failed, "I could not look up the latest version. " + reason.Text)
                : new UpdatePreparation(UpdatePreparationStatus.NotPassed, "The newest version did not pass my checks: " + (reason?.Text ?? "it did not pass."));
        }

        var candidate = review.Candidate!;
        lock (_gate)
        {
            _latest[record.Id] = candidate.Source.Version!;
        }

        if (!VersionOrder.IsNewer(candidate.Source.Version, record.InstalledVersion))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return new UpdatePreparation(UpdatePreparationStatus.UpToDate, $"{record.Name} is up to date.");
        }

        var offer = await _offers.OfferUpdateAsync(candidate, record, cancellationToken).ConfigureAwait(false);
        return new UpdatePreparation(UpdatePreparationStatus.Offered, $"Version {candidate.Source.Version} is available.", offer);
    }

    /// <inheritdoc/>
    public async Task<RemoveOutcome> RemoveAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        var record = await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new RemoveOutcome(false, true, "That integration is not installed any more.");
        }

        // Its program ends first, so that nothing holds its files.
        await _connections.ForgetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        await _registry.RemoveAsync(integrationId, cancellationToken).ConfigureAwait(false);
        _toolCache?.Remove(integrationId);
        lock (_gate)
        {
            _latest.Remove(integrationId);
        }

        var filesDeleted = true;
        if (record.Managed is not null && IntegrationRules.IsValidId(integrationId))
        {
            // The folder is worked out from the id, never taken from the record: nothing the record says can make this delete anything else.
            var directory = _layout.IntegrationDirectory(integrationId);
            filesDeleted = !_layout.IsInsideIntegrations(directory) || InstallFiles.DeleteDirectory(directory);
        }

        await ReleaseUnusedRuntimesAsync(cancellationToken).ConfigureAwait(false);
        LogRemoved(_logger, integrationId, filesDeleted);
        return new RemoveOutcome(
            true,
            filesDeleted,
            filesDeleted
                ? $"{record.Name} was removed."
                : $"{record.Name} was removed, but some of its files are still in use. They will be deleted the next time the Assistant cleans up.");
    }

    // A runtime that no installed integration uses is deleted: nothing of it was the user's, and it is downloaded again if it is needed.
    private async Task ReleaseUnusedRuntimesAsync(CancellationToken cancellationToken)
    {
        var inUse = new HashSet<(RuntimeKind, string)>();
        foreach (var other in await _registry.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (other.Managed is { Runtime: { } kind, RuntimeVersion: { } version })
            {
                inUse.Add((kind, version));
            }
        }

        _runtimes.RemoveUnused(inUse);
    }

    private static bool CanLookUp(InstalledIntegration record) =>
        record.Managed is { Kind: InstallSourceKind.Npm or InstallSourceKind.PyPi } && record.InstalledVersion is not null;

    // What the registry says is the latest version, or null when it cannot be read.
    private async Task<string?> LookUpAsync(InstalledIntegration record, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(LookUpTimeout);
        try
        {
            var managed = record.Managed!;
            return managed.Kind == InstallSourceKind.Npm
                ? (await _metadata.GetNpmAsync(managed.Package, null, limit.Token).ConfigureAwait(false))?.Version
                : (await _metadata.GetPyPiAsync(managed.Package, null, limit.Token).ConfigureAwait(false))?.Version;
        }
        catch (DiscoveryException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // The candidate that stands for the newest version of an installed package: what the Assistant knows of it from when it was installed.
    private static IntegrationCandidate Synthesize(InstalledIntegration record, ManagedInstall managed)
    {
        var npm = managed.Kind == InstallSourceKind.Npm;
        return new IntegrationCandidate
        {
            Name = managed.Package,
            SourceUrl = managed.Repository ?? (npm ? "https://www.npmjs.com/package/" + managed.Package : "https://pypi.org/project/" + managed.Package + "/"),
            RepositoryUrl = managed.Repository,
            Publisher = managed.Publisher,
            Trust = managed.Trust,
            Packages = [new CandidatePackage(npm ? CandidateInstallMethod.Npm : CandidateInstallMethod.PyPi, managed.Package, null)],
            Runtime = npm ? CandidateRuntime.NodeJs : CandidateRuntime.Python,
            RequiredSecrets = [.. record.Authentication.Secrets.Select(binding => binding.Target)],

            // What it offers now is the evidence that it is an MCP server.
            ToolNames = record.Capabilities.ToolNames,
            FoundIn = [record.Source.Kind == IntegrationSourceKind.OfficialRegistry ? "mcp-registry" : npm ? "npm" : "pypi"],
            Evidence = CapabilityEvidence.AppOnly,
            CommitSha = managed.Commit,
        };
    }

    private async Task<bool> WebAllowedAsync(Core.Settings.AppSettings settings, CancellationToken cancellationToken) =>
        !settings.Privacy.LocalOnly && (await _permissions.CheckAsync(PermissionCapability.ExternalSearch, cancellationToken).ConfigureAwait(false)).IsAllowed;

    private void OnRegistryChanged(object? sender, IntegrationsChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    // The keys the user gives an app: the names a program is given them under, or the headers a server is sent them in.
    private static IReadOnlyList<string> KeyNamesOf(InstalledIntegration record) =>
        record.Authentication.Kind == IntegrationAuthKind.EnvironmentSecret || record.Authentication.Kind == IntegrationAuthKind.HeaderKey
            ? record.Authentication.Secrets.Select(binding => binding.Target).ToList()
            : new List<string>();

    private static IntegrationInfo Describe(InstalledIntegration record, string? latest, Core.Settings.PermissionSettings? permissions = null)
    {
        var (health, level) = HealthOf(record, permissions);
        var managed = record.Managed;
        var local = record.Transport.Kind == McpTransportKind.Stdio;
        return new IntegrationInfo
        {
            Id = record.Id,
            Name = record.Name,
            Source = SourceOf(record),
            Version = record.InstalledVersion ?? "—",
            Enabled = record.Enabled,
            Health = health,
            HealthLevel = level,
            Permissions = PermissionsOf(record),
            UpdateVersion = latest is not null && VersionOrder.IsNewer(latest, record.InstalledVersion) ? latest : null,
            CanUpdate = CanLookUp(record),
            IsManaged = managed is not null,
            ToolCount = record.Capabilities.ToolNames.Count,
            IsLocalProgram = local,
            CanSignIn = !local || record.Authentication.Kind == IntegrationAuthKind.OAuth,
            KeyNames = KeyNamesOf(record),
            SignedIn = record.Authentication is { Kind: not IntegrationAuthKind.None, State: IntegrationAuthState.Ready },
            NeedsSignIn = record.Authentication.State is IntegrationAuthState.NeedsSignIn or IntegrationAuthState.Expired or IntegrationAuthState.Rejected,
            Access = new IntegrationAccess
            {
                Reads = record.Permissions.AllowReads,
                Changes = record.Permissions.AllowSideEffects,
                Network = record.Permissions.AllowNetwork,
                Account = record.Permissions.AllowAccountAccess,
                Updates = record.Permissions.AllowUpdates,
                NetworkApplies = McpConnectionManager.LeavesThisPc(record),
                AccountApplies = record.Authentication.Kind != IntegrationAuthKind.None,
                UpdatesApply = CanLookUp(record),
            },
        };
    }

    // Why a connection is refused now, in words: the choice of the user for this app, then the account, then Local Only.
    private static string BlockedText(InstalledIntegration record)
    {
        if (McpConnectionManager.LeavesThisPc(record) && !record.Permissions.AllowNetwork)
        {
            return $"{record.Name} cannot be connected to now: you turned off its network access.";
        }

        if (!record.Permissions.AllowAccountAccess && record.Authentication.Kind != IntegrationAuthKind.None)
        {
            return $"{record.Name} cannot be connected to now: you turned off its account access, so it cannot sign in.";
        }

        return $"{record.Name} cannot be connected to now: Local Only mode is on and it would send what you ask over the internet.";
    }

    private static string SourceOf(InstalledIntegration record)
    {
        var kind = record.Source.Kind switch
        {
            IntegrationSourceKind.OfficialRegistry => "Official MCP registry",
            IntegrationSourceKind.CommunityRegistry => record.Managed?.Kind switch
            {
                InstallSourceKind.Npm => "Community, npm",
                InstallSourceKind.PyPi => "Community, PyPI",
                _ => "Community",
            },
            IntegrationSourceKind.Bundled => "Comes with the Assistant",
            _ => "Added by you",
        };
        var origin = CandidateText.Line(record.Source.Origin, 80);
        return origin is null ? kind : $"{kind}: {origin}";
    }

    private static (string Text, IntegrationHealthLevel Level) HealthOf(InstalledIntegration record, Core.Settings.PermissionSettings? permissions)
    {
        if (!record.Enabled)
        {
            return ("Turned off", IntegrationHealthLevel.Unknown);
        }

        if (record.Authentication.State is IntegrationAuthState.NeedsSignIn or IntegrationAuthState.Rejected or IntegrationAuthState.Expired)
        {
            return ("Needs sign-in", IntegrationHealthLevel.Warning);
        }

        // Connected, and still of no use: the permission its tools need is off (a messaging app while Messaging is), which is otherwise said only in the conversation.
        if (permissions is not null && record.Permissions.RequiredCapability is { } capability && !permissions.IsOn(capability))
        {
            return ($"Not used: the {PermissionCatalog.Get(capability).Title} permission is off. Turn it on in Settings, under Permissions.", IntegrationHealthLevel.Warning);
        }

        return record.Health.Status switch
        {
            IntegrationHealthStatus.Healthy => ("Connected", IntegrationHealthLevel.Good),
            IntegrationHealthStatus.AuthRequired => ("Needs sign-in", IntegrationHealthLevel.Warning),
            IntegrationHealthStatus.Unreachable => ("Cannot be reached", IntegrationHealthLevel.Problem),
            IntegrationHealthStatus.Incompatible => ("Speaks a version of the protocol I do not understand", IntegrationHealthLevel.Problem),
            IntegrationHealthStatus.Failed => ("Something went wrong", IntegrationHealthLevel.Problem),
            _ => ("Not started yet. It starts when you need it.", IntegrationHealthLevel.Unknown),
        };
    }

    private static string PermissionsOf(InstalledIntegration record)
    {
        var parts = new List<string>
        {
            record.Transport.Kind == McpTransportKind.Stdio ? "Runs on this PC" : "Reached over the internet",
        };
        if (record.Permissions.LeavesThisPc && record.Transport.Kind == McpTransportKind.Stdio)
        {
            parts.Add("may use the internet");
        }

        if (record.Permissions.RequiredCapability is { } capability)
        {
            parts.Add($"needs the {PermissionCatalog.Get(capability).Title} permission");
        }

        parts.Add(
            !record.Permissions.AllowSideEffects && !record.Permissions.AllowReads ? "all its tools are turned off"
            : !record.Permissions.AllowSideEffects ? "read-only tools only"
            : !record.Permissions.AllowReads ? "only tools that change things, and it asks before each"
            : "asks before it changes anything");
        if (McpConnectionManager.LeavesThisPc(record) && !record.Permissions.AllowNetwork)
        {
            parts.Add("network access is off");
        }

        if (record.Authentication.Kind != IntegrationAuthKind.None && !record.Permissions.AllowAccountAccess)
        {
            parts.Add("account access is off");
        }

        if (CanLookUp(record) && !record.Permissions.AllowUpdates)
        {
            parts.Add("updates are off");
        }
        if (record.Permissions.ReadOnlyTools.Count > 0)
        {
            var count = record.Permissions.ReadOnlyTools.Count;
            parts.Add(count == 1 ? "1 tool you marked as read-only" : $"{count.ToString(CultureInfo.InvariantCulture)} tools you marked as read-only");
        }

        if (record.Permissions.BlockedTools.Count > 0)
        {
            var count = record.Permissions.BlockedTools.Count;
            parts.Add(count == 1 ? "1 tool blocked" : $"{count.ToString(CultureInfo.InvariantCulture)} tools blocked");
        }

        if (record.Authentication.Secrets.Count > 0)
        {
            var count = record.Authentication.Secrets.Count;
            parts.Add(count == 1 ? "uses 1 key" : $"uses {count.ToString(CultureInfo.InvariantCulture)} keys");
        }

        return string.Join(", ", parts) + ".";
    }

    private static string FailureText(InstalledIntegration record, McpFailure? failure) => failure switch
    {
        McpFailure.AuthRequired or McpFailure.Forbidden => $"{record.Name} needs you to sign in. Use Sign in under it.",
        McpFailure.LaunchFailed => $"{record.Name}'s program could not be started. It may have been moved or deleted; removing it and installing it again will fix that.",
        McpFailure.Unsupported => $"{record.Name} speaks a version of the protocol that I do not understand yet.",
        McpFailure.TimedOut => $"{record.Name} did not answer in time.",
        _ => $"{record.Name} could not be reached.",
    };

    [LoggerMessage(EventId = 3221, Level = LogLevel.Information, Message = "What integration {IntegrationId} may do was changed by the user")]
    private static partial void LogAccessChanged(ILogger logger, string integrationId);

    [LoggerMessage(EventId = 3200, Level = LogLevel.Information, Message = "Integration {IntegrationId} reconnected: {Connected}")]
    private static partial void LogReconnected(ILogger logger, string integrationId, bool connected);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "Integration updates looked for: {CheckedCount} looked up, {Available} newer, {Failed} failed")]
    private static partial void LogChecked(ILogger logger, int checkedCount, int available, int failed);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Information, Message = "Integration {IntegrationId} removed, files deleted: {FilesDeleted}")]
    private static partial void LogRemoved(ILogger logger, string integrationId, bool filesDeleted);
}

/// <summary>Which of two versions is newer.</summary>
internal static class VersionOrder
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is a newer version than <paramref name="current"/>: compared number by number. A version that cannot be read as numbers is
    /// never newer, so an odd version string cannot prompt an update.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryNumbers(candidate, out var latest) || !TryNumbers(current, out var installed))
        {
            return false;
        }

        for (var index = 0; index < Math.Max(latest.Count, installed.Count); index++)
        {
            var a = index < latest.Count ? latest[index] : 0;
            var b = index < installed.Count ? installed[index] : 0;
            if (a != b)
            {
                return a > b;
            }
        }

        return false;
    }

    // The leading numbers of a version: 1.2.3, 2026.8.31, 1.2.3-beta, 1.2.3+build.
    private static bool TryNumbers(string? version, out List<long> numbers)
    {
        numbers = [];
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var core = version.Trim().TrimStart('v');
        var end = core.IndexOfAny(['-', '+']);
        if (end >= 0)
        {
            core = core[..end];
        }

        foreach (var part in core.Split('.'))
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            numbers.Add(number);
        }

        return numbers.Count > 0;
    }
}
