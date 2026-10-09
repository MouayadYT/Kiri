using System.Globalization;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Domain;
using Assistant.Tools.Mcp.Auth;

namespace Assistant.Tools.Integrations;

/// <summary>
/// The words and codes the activity log keeps about integrations (PROJECT_SPEC §4.8, step 117). An action is described by what it was done to (an app's name and a
/// version, which the review already tidied), a fixed sentence about what it was, and a code that says why it did not work. A key, a token, an address, a path and
/// the text a registry or a program wrote never reach the log: of an install failure only its kind is kept, never the message it came with.
/// </summary>
internal static class IntegrationAudit
{
    /// <summary>The code to keep for an install that failed: the name of the failure in lower snake case, which <see cref="AuditText.Reason"/> has words for.</summary>
    public static string? CodeOf(InstallFailure failure) => failure switch
    {
        InstallFailure.None => null,
        InstallFailure.OfferExpired => "offer_expired",
        InstallFailure.WebLocked => "web_locked",
        InstallFailure.NotAllowed => "not_allowed",
        InstallFailure.AlreadyInstalled => "already_installed",
        InstallFailure.Busy => "busy",
        InstallFailure.DownloadFailed => "download_failed",
        InstallFailure.HashMismatch => "hash_mismatch",
        InstallFailure.RuntimeUnavailable => "runtime_unavailable",
        InstallFailure.SetupFailed => "setup_failed",
        InstallFailure.EntryPointMissing => "entry_point_missing",
        InstallFailure.DidNotStart => "did_not_start",
        InstallFailure.LacksCapability => "lacks_capability",
        InstallFailure.DiskFailed => "disk_failed",
        InstallFailure.Unsupported => "unsupported",
        InstallFailure.SignInFailed => "sign_in_failed",
        InstallFailure.AppNotRunning => "app_not_running",
        _ => "tool_failed",
    };

    /// <summary>What is looked for: the app and the two words of what is wanted done in it, such as <c>Todoist (create task)</c>.</summary>
    public static string Need(IntegrationNeed need) =>
        need.IsForAnyApp
            ? $"an app that can {AuditText.Label(need.Capability.Phrase)}"
            : $"{AuditText.Label(need.AppName)} ({AuditText.Label(need.Capability.Phrase)})";

    /// <summary>An app with its version, if it has one: <c>Todoist 2.1.0</c>.</summary>
    public static string App(string? name, string? version) =>
        string.IsNullOrWhiteSpace(version) ? AuditText.Label(name) : $"{AuditText.Label(name)} {AuditText.Label(version)}";

    /// <summary>How many, in words: <c>1 integration</c>, <c>3 integrations</c>.</summary>
    public static string Count(int count) =>
        count == 1 ? "1 integration" : count.ToString(CultureInfo.InvariantCulture) + " integrations";
}

/// <summary>
/// The Integration Finder with a record of each look (PROJECT_SPEC §4.8, step 117): that the Assistant looked for an integration for an app and what it came to (how
/// many it found, that it was not allowed to look, that the places could not be reached). The app and the two words of what was wanted are all the look is made
/// from, and all the record says; what the user wrote around them is not in either.
/// </summary>
public sealed class AuditedIntegrationFinder(IIntegrationFinder inner, IAuditTrail audit) : IIntegrationFinder
{
    /// <inheritdoc/>
    public async Task<IntegrationDiscoveryResult> FindAsync(
        IntegrationNeed need, IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(need);
        using var action = audit.Begin(
            AuditKind.FindIntegration, need.AppName, $"Look for an integration for {IntegrationAudit.Need(need)}", RiskLevel.ReadOnly);
        try
        {
            var result = await inner.FindAsync(need, exclude, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case DiscoveryStatus.Found:
                    action.End(AuditStatus.Succeeded, summary: $"Look for an integration for {IntegrationAudit.Need(need)}: found {IntegrationAudit.Count(result.Candidates.Count)}");
                    break;
                case DiscoveryStatus.NothingPlausible:
                    action.End(AuditStatus.Succeeded, summary: $"Look for an integration for {IntegrationAudit.Need(need)}: none fits");
                    break;
                case DiscoveryStatus.Blocked:
                    // Nothing was sent: it is not a failure, it was not done.
                    action.End(AuditStatus.Skipped, result.Block switch { DiscoveryBlock.LocalOnly => "local_only", DiscoveryBlock.NotAllowedNow => "not_allowed", _ => "permission_off" });
                    break;
                default:
                    action.End(AuditStatus.Failed, "lookup_failed");
                    break;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }
}

/// <summary>
/// The installer with a record of each install and update (PROJECT_SPEC §4.8, step 117). The installer is only ever reached for an offer the user approved by
/// clicking Install (<see cref="IIntegrationOffers"/>), so the record says that they allowed it. How it ended is the outcome's status and the kind of failure,
/// never the message the outcome carries.
/// </summary>
public sealed class AuditedIntegrationInstaller(IIntegrationInstaller inner, IAuditTrail audit) : IIntegrationInstaller
{
    /// <inheritdoc/>
    public Task<InstallPlan> PlanAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) => inner.PlanAsync(candidate, cancellationToken);

    /// <inheritdoc/>
    public Task<int> CleanUpAsync(CancellationToken cancellationToken = default) => inner.CleanUpAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<InstallOutcome> InstallAsync(InstallCandidate candidate, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return RunAsync(
            AuditKind.InstallIntegration, candidate, $"Install {IntegrationAudit.App(candidate.AppName, candidate.Version)}", () => inner.InstallAsync(candidate, progress, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<InstallOutcome> UpdateAsync(
        InstallCandidate candidate, string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return RunAsync(
            AuditKind.UpdateIntegration, candidate, $"Update {AuditText.Label(candidate.AppName)} to {AuditText.Label(candidate.Version ?? "the newest version")}",
            () => inner.UpdateAsync(candidate, integrationId, progress, cancellationToken));
    }

    private async Task<InstallOutcome> RunAsync(AuditKind kind, InstallCandidate candidate, string summary, Func<Task<InstallOutcome>> run)
    {
        using var action = audit.Begin(kind, candidate.AppName, summary, RiskLevel.SideEffect, confirmation: ConfirmationDecision.Approved);
        try
        {
            var outcome = await run().ConfigureAwait(false);
            action.End(
                outcome.Status switch
                {
                    InstallStatus.Installed => AuditStatus.Succeeded,
                    InstallStatus.Cancelled => AuditStatus.Cancelled,
                    _ => AuditStatus.Failed,
                },
                outcome.Status == InstallStatus.Failed ? IntegrationAudit.CodeOf(outcome.Failure) : null);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }
}

/// <summary>
/// The broker of offers with a record of each offer and each time the user turned one down (PROJECT_SPEC §4.8, step 117). An offer is the Assistant putting a
/// reviewed integration in front of the user: nothing was downloaded or run. Accepting is not recorded here: it is the installer's, which records the install.
/// </summary>
public sealed class AuditedIntegrationOffers(IIntegrationOffers inner, IAuditTrail audit) : IIntegrationOffers
{
    // What the offers made are about, so that turning one down can say which: the most recent few, forgotten as new ones come.
    private const int Remembered = 16;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <inheritdoc/>
    public async Task<IntegrationOffer> OfferAsync(InstallCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var offer = await inner.OfferAsync(candidate, cancellationToken).ConfigureAwait(false);
        Record(offer, $"Offer to install {IntegrationAudit.App(offer.AppName, offer.NewVersion ?? candidate.Version)}");
        return offer;
    }

    /// <inheritdoc/>
    public async Task<IntegrationOffer> OfferUpdateAsync(InstallCandidate candidate, InstalledIntegration current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var offer = await inner.OfferUpdateAsync(candidate, current, cancellationToken).ConfigureAwait(false);
        Record(offer, $"Offer to update {AuditText.Label(offer.AppName)} to {AuditText.Label(offer.NewVersion ?? candidate.Version ?? "the newest version")}");
        return offer;
    }

    /// <inheritdoc/>
    public async Task<IntegrationOffer> OfferConnectAsync(
        KnownEndpoint endpoint, InstalledIntegration? existing, IntegrationCapability? capability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var offer = await inner.OfferConnectAsync(endpoint, existing, capability, cancellationToken).ConfigureAwait(false);
        Record(offer, existing is null ? $"Offer to connect {AuditText.Label(offer.AppName)}" : $"Offer to sign in to {AuditText.Label(offer.AppName)}");
        return offer;
    }

    /// <inheritdoc/>
    public Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
        inner.AcceptAsync(offerId, progress, cancellationToken);

    /// <inheritdoc/>
    public void Decline(string offerId)
    {
        string? name;
        lock (_gate)
        {
            _names.TryGetValue(offerId ?? string.Empty, out name);
        }

        inner.Decline(offerId!);
        if (name is not null)
        {
            // The user said no: nothing was installed, and the record says that they chose that.
            using var action = audit.Begin(
                AuditKind.DeclineIntegration, name, $"Turn down the offer to install {name}", RiskLevel.ReadOnly, confirmation: ConfirmationDecision.Declined);
            action.End(AuditStatus.Declined);
        }
    }

    private void Record(IntegrationOffer offer, string summary)
    {
        using var action = audit.Begin(AuditKind.OfferIntegration, offer.AppName, summary, RiskLevel.ReadOnly);
        action.End(AuditStatus.Succeeded);
        lock (_gate)
        {
            if (_names.TryAdd(offer.OfferId, AuditText.Label(offer.AppName)))
            {
                _order.Enqueue(offer.OfferId);
                while (_order.Count > Remembered)
                {
                    _names.Remove(_order.Dequeue());
                }
            }
        }
    }
}

/// <summary>
/// The manager of installed integrations with a record of what the user does with them (PROJECT_SPEC §4.8, step 117): looking for newer versions, turning one on or
/// off, reconnecting and removing. Listing is not recorded. A look for newer versions that is answered from what was found within the day is not a look, and is
/// not recorded again.
/// </summary>
public sealed class AuditedIntegrationManager(IIntegrationManager inner, IAuditTrail audit) : IIntegrationManager, IDisposable
{
    private readonly object _gate = new();
    private UpdateCheckSummary? _lastCheck;

    /// <inheritdoc/>
    public event EventHandler? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IntegrationInfo>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<UpdateCheckState> GetUpdateCheckStateAsync(CancellationToken cancellationToken = default) => inner.GetUpdateCheckStateAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task SetEnabledAsync(string integrationId, bool enabled, CancellationToken cancellationToken = default)
    {
        var name = await NameOfAsync(integrationId, cancellationToken).ConfigureAwait(false);
        using var action = audit.Begin(
            AuditKind.SwitchIntegration, name, $"Turn {(enabled ? "on" : "off")} {name}", RiskLevel.SideEffect);
        try
        {
            await inner.SetEnabledAsync(integrationId, enabled, cancellationToken).ConfigureAwait(false);
            action.End(AuditStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IntegrationInfo?> SetAccessAsync(string integrationId, IntegrationAccessChange change, CancellationToken cancellationToken = default)
    {
        var name = await NameOfAsync(integrationId, cancellationToken).ConfigureAwait(false);
        using var action = audit.Begin(AuditKind.ChangeIntegrationAccess, name, $"Change what {name} may do", RiskLevel.SideEffect);
        try
        {
            var info = await inner.SetAccessAsync(integrationId, change, cancellationToken).ConfigureAwait(false);
            action.End(info is null ? AuditStatus.Failed : AuditStatus.Succeeded, info is null ? "not_installed" : null);
            return info;
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ReconnectOutcome> ReconnectAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        var name = await NameOfAsync(integrationId, cancellationToken).ConfigureAwait(false);
        using var action = audit.Begin(AuditKind.ReconnectIntegration, name, $"Reconnect {name}", RiskLevel.SideEffect);
        try
        {
            var outcome = await inner.ReconnectAsync(integrationId, cancellationToken).ConfigureAwait(false);
            action.End(
                outcome.Connected ? AuditStatus.Succeeded : AuditStatus.Failed,
                outcome.Connected ? null : "not_connected",
                outcome.Connected ? $"Reconnect {name}: {(outcome.ToolCount == 1 ? "1 tool" : outcome.ToolCount.ToString(CultureInfo.InvariantCulture) + " tools")}" : null);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<UpdateCheckSummary> CheckForUpdatesAsync(bool force, CancellationToken cancellationToken = default)
    {
        var summary = await inner.CheckForUpdatesAsync(force, cancellationToken).ConfigureAwait(false);

        // Only a look that really looked: not one that was off or blocked, and not the answer kept from the last look.
        bool looked;
        lock (_gate)
        {
            looked = summary.State == UpdateCheckState.Ready && !ReferenceEquals(summary, _lastCheck);
            if (summary.State == UpdateCheckState.Ready)
            {
                _lastCheck = summary;
            }
        }

        if (looked)
        {
            using var action = audit.Begin(
                AuditKind.CheckIntegrationUpdates, "integrations", "Look for newer versions of the installed integrations", RiskLevel.ReadOnly);
            action.End(
                summary.Checked > 0 && summary.Failed == summary.Checked ? AuditStatus.Failed : AuditStatus.Succeeded,
                summary.Checked > 0 && summary.Failed == summary.Checked ? "lookup_failed" : null,
                $"Look for newer versions of {IntegrationAudit.Count(summary.Checked)}: {summary.Available.ToString(CultureInfo.InvariantCulture)} available");
        }

        return summary;
    }

    /// <inheritdoc/>
    public async Task<UpdatePreparation> PrepareUpdateAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        var name = await NameOfAsync(integrationId, cancellationToken).ConfigureAwait(false);
        using var action = audit.Begin(
            AuditKind.FindIntegrationUpdate, name, $"Look for a newer version of {name}", RiskLevel.ReadOnly);
        try
        {
            var preparation = await inner.PrepareUpdateAsync(integrationId, cancellationToken).ConfigureAwait(false);
            switch (preparation.Status)
            {
                case UpdatePreparationStatus.Offered:
                    action.End(AuditStatus.Succeeded, summary: $"Look for a newer version of {name}: one is available");
                    break;
                case UpdatePreparationStatus.UpToDate:
                    action.End(AuditStatus.Succeeded, summary: $"Look for a newer version of {name}: it is up to date");
                    break;
                case UpdatePreparationStatus.NotUpdatable:
                    action.End(AuditStatus.Skipped, "not_updatable");
                    break;
                case UpdatePreparationStatus.Blocked:
                    action.End(AuditStatus.Skipped, "web_locked");
                    break;
                case UpdatePreparationStatus.NotPassed:
                    action.End(AuditStatus.Failed, "review_failed");
                    break;
                default:
                    action.End(AuditStatus.Failed, "lookup_failed");
                    break;
            }

            return preparation;
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<RemoveOutcome> RemoveAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        var name = await NameOfAsync(integrationId, cancellationToken).ConfigureAwait(false);
        using var action = audit.Begin(AuditKind.RemoveIntegration, name, $"Remove {name}", RiskLevel.SideEffect);
        try
        {
            var outcome = await inner.RemoveAsync(integrationId, cancellationToken).ConfigureAwait(false);
            action.End(outcome.Removed ? AuditStatus.Succeeded : AuditStatus.Failed, outcome.Removed ? null : "not_connected");
            return outcome;
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => (inner as IDisposable)?.Dispose();

    // The name the user knows the integration by; its id when it cannot be found (an id is lower-case letters and digits, so it is safe to keep).
    private async Task<string> NameOfAsync(string integrationId, CancellationToken cancellationToken)
    {
        try
        {
            var list = await inner.ListAsync(cancellationToken).ConfigureAwait(false);
            if (list.FirstOrDefault(info => string.Equals(info.Id, integrationId, StringComparison.Ordinal)) is { } found)
            {
                return AuditText.Label(found.Name);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The name is for the record only: the action is not held up for it.
        }

        return IntegrationRules.IsValidId(integrationId) ? integrationId : "an integration";
    }
}

/// <summary>
/// The connector with a record of each connection and sign-in (PROJECT_SPEC §4.8): that the user approved connecting an app or signing in to it, and how it came to
/// end. The app's name is all the record says; no address, token or word the server said is in it.
/// </summary>
public sealed class AuditedIntegrationConnector(IIntegrationConnector inner, IAuditTrail audit) : IIntegrationConnector
{
    /// <inheritdoc/>
    public Task<InstallOutcome> ConnectAsync(
        KnownEndpoint endpoint, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, OAuthSignInOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return RunAsync(AuditKind.ConnectIntegration, endpoint.Name, $"Connect {AuditText.Label(endpoint.Name)}", () => inner.ConnectAsync(endpoint, progress, cancellationToken, options));
    }

    /// <inheritdoc/>
    public async Task<InstallOutcome> SignInAsync(
        string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, OAuthSignInOptions? options = null) =>
        await RunAsync(AuditKind.SignInIntegration, integrationId, $"Sign in to {AuditText.Label(integrationId)}", () => inner.SignInAsync(integrationId, progress, cancellationToken, options)).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task SignOutAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        using var action = audit.Begin(AuditKind.SignInIntegration, integrationId, $"Sign out of {AuditText.Label(integrationId)}", RiskLevel.SideEffect);
        try
        {
            await inner.SignOutAsync(integrationId, cancellationToken).ConfigureAwait(false);
            action.End(AuditStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<InstallOutcome> UseTokenAsync(string integrationId, string token, CancellationToken cancellationToken = default) =>
        RunAsync(AuditKind.SignInIntegration, integrationId, $"Give {AuditText.Label(integrationId)} an access token", () => inner.UseTokenAsync(integrationId, token, cancellationToken));

    /// <inheritdoc/>
    public Task<InstallOutcome> SetKeyAsync(string integrationId, string name, string value, CancellationToken cancellationToken = default) =>
        RunAsync(AuditKind.SignInIntegration, integrationId, $"Give {AuditText.Label(integrationId)} a key", () => inner.SetKeyAsync(integrationId, name, value, cancellationToken));

    private async Task<InstallOutcome> RunAsync(AuditKind kind, string name, string summary, Func<Task<InstallOutcome>> run)
    {
        using var action = audit.Begin(kind, name, summary, RiskLevel.SideEffect, confirmation: ConfirmationDecision.Approved);
        try
        {
            var outcome = await run().ConfigureAwait(false);
            action.End(
                outcome.Status switch
                {
                    InstallStatus.Installed => AuditStatus.Succeeded,
                    InstallStatus.Cancelled => AuditStatus.Cancelled,
                    _ => AuditStatus.Failed,
                },
                outcome.Status == InstallStatus.Failed ? CodeOf(outcome.Failure) : null);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            action.End(AuditStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            action.End(AuditStatus.Failed, "tool_failed");
            throw;
        }
    }

    private static string? CodeOf(InstallFailure failure) => IntegrationAudit.CodeOf(failure);
}
