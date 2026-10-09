using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>How long and how many offers are kept.</summary>
public sealed record IntegrationOffersOptions
{
    /// <summary>How long an offer can be accepted after it was made.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The most offers kept at once; the oldest is forgotten first.</summary>
    public int MaxPending { get; init; } = 8;
}

/// <summary>
/// The only way an integration gets installed (PROJECT_SPEC §4.8, step 108): an offer is made for a reviewed candidate, shown to the user, and accepted
/// or declined by the user's click. The model, a tool and the web have no way to accept one: nothing but the panel's Install button calls
/// <see cref="AcceptAsync"/>, and it needs an id that only <see cref="OfferAsync"/> hands out.
/// </summary>
public interface IIntegrationOffers
{
    /// <summary>Makes an offer to install <paramref name="candidate"/> and returns what the user is shown. Nothing is downloaded or run.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IntegrationOffer> OfferAsync(InstallCandidate candidate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes an offer to connect the app whose server is known (<see cref="KnownEndpoint"/>), or, when <paramref name="existing"/> is the integration already installed for
    /// it, to sign in to it again. Nothing is recorded or opened until the user accepts.
    /// </summary>
    Task<IntegrationOffer> OfferConnectAsync(KnownEndpoint endpoint, InstalledIntegration? existing, IntegrationCapability? capability, CancellationToken cancellationToken = default);

    /// <summary>Makes an offer to replace the installed integration <paramref name="current"/> with the version <paramref name="candidate"/> pins.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IntegrationOffer> OfferUpdateAsync(InstallCandidate candidate, InstalledIntegration current, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user approved the offer <paramref name="offerId"/>: installs (or updates) what it was made for. An offer can be accepted once, and only while it
    /// has not expired; an id that was never handed out, was used, declined or has expired installs nothing (<see cref="InstallFailure.OfferExpired"/>).
    /// </summary>
    Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>The user turned the offer down: it is forgotten and cannot be accepted any more.</summary>
    void Decline(string offerId);
}

/// <summary>
/// Keeps the offers made to the user until they approve or decline (PROJECT_SPEC §4.8, step 108). An offer holds the reviewed candidate in memory under an
/// unguessable id, for half an hour, one use, and at most a few at once. Logs say counts only.
/// </summary>
public sealed partial class IntegrationOffers : IIntegrationOffers
{
    private readonly IIntegrationInstaller _installer;
    private readonly IIntegrationConnector? _connector;
    private readonly TimeProvider _clock;
    private readonly IntegrationOffersOptions _options;
    private readonly ILogger<IntegrationOffers> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>Creates the broker over the installer that acts on an accepted offer.</summary>
    public IntegrationOffers(
        IIntegrationInstaller installer, TimeProvider clock, ILogger<IntegrationOffers> logger, IntegrationOffersOptions? options = null, IIntegrationConnector? connector = null)
    {
        _connector = connector;
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _installer = installer;
        _clock = clock;
        _logger = logger;
        _options = options ?? new IntegrationOffersOptions();
    }

    /// <inheritdoc/>
    public Task<IntegrationOffer> OfferAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) =>
        MakeAsync(candidate, current: null, cancellationToken);

    /// <inheritdoc/>
    public Task<IntegrationOffer> OfferUpdateAsync(InstallCandidate candidate, InstalledIntegration current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return MakeAsync(candidate, current, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Pending? offer;
        lock (_gate)
        {
            Forget(_clock.GetUtcNow());
            _pending.Remove(offerId ?? string.Empty, out offer);
        }

        if (offer is null)
        {
            LogAccepted(_logger, false);
            return InstallOutcome.Fail(InstallFailure.OfferExpired, "That offer has expired. Ask again and I will look it up again.");
        }

        LogAccepted(_logger, true);
        if (offer.Known is { } known)
        {
            return _connector is null
                ? InstallOutcome.Fail(InstallFailure.Unsupported, "I cannot connect apps in this version.")
                : offer.SignIn
                    ? await _connector.SignInAsync(known.IntegrationId, progress, cancellationToken).ConfigureAwait(false)
                    : await _connector.ConnectAsync(known, progress, cancellationToken).ConfigureAwait(false);
        }

        return offer.ReplacingId is { } id
            ? await _installer.UpdateAsync(offer.Candidate!, id, progress, cancellationToken).ConfigureAwait(false)
            : await _installer.InstallAsync(offer.Candidate!, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Decline(string offerId)
    {
        lock (_gate)
        {
            _pending.Remove(offerId ?? string.Empty);
        }
    }

    /// <inheritdoc/>
    public Task<IntegrationOffer> OfferConnectAsync(
        KnownEndpoint endpoint, InstalledIntegration? existing, IntegrationCapability? capability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Forget(now);
            while (_pending.Count >= Math.Max(1, _options.MaxPending))
            {
                _pending.Remove(_pending.MinBy(pair => pair.Value.ExpiresAt).Key);
            }

            _pending[id] = new Pending(null, null, now + _options.Lifetime, endpoint, existing is not null);
        }

        LogConnectOffered(_logger, endpoint.AppKey, existing is not null);
        return Task.FromResult(IntegrationOfferWriter.WriteConnect(endpoint, id, existing is not null, capability));
    }

    private async Task<IntegrationOffer> MakeAsync(InstallCandidate candidate, InstalledIntegration? current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var plan = await _installer.PlanAsync(candidate, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Forget(now);
            while (_pending.Count >= Math.Max(1, _options.MaxPending))
            {
                _pending.Remove(_pending.MinBy(pair => pair.Value.ExpiresAt).Key);
            }

            _pending[id] = new Pending(candidate, current?.Id, now + _options.Lifetime);
        }

        LogOffered(_logger, candidate.Id, candidate.Source.Kind, current is not null);
        return IntegrationOfferWriter.Write(candidate, plan, id, current);
    }

    // Forgets what has expired. Called with the gate held.
    private void Forget(DateTimeOffset now)
    {
        foreach (var expired in _pending.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
        {
            _pending.Remove(expired);
        }
    }

    [LoggerMessage(EventId = 3191, Level = LogLevel.Information, Message = "Integration {IntegrationId} offered for approval: {SourceKind}, update {IsUpdate}")]
    private static partial void LogOffered(ILogger logger, string integrationId, InstallSourceKind sourceKind, bool isUpdate);

    [LoggerMessage(EventId = 3192, Level = LogLevel.Information, Message = "Offer accepted by the user: known {Known}")]
    private static partial void LogAccepted(ILogger logger, bool known);

    [LoggerMessage(EventId = 3193, Level = LogLevel.Information, Message = "Connection to {AppKey} offered for approval, sign-in again {SignIn}")]
    private static partial void LogConnectOffered(ILogger logger, string appKey, bool signIn);

    // Candidate is null for an offer to connect (or sign in to) a known app, which has no package: Known says which.
    private sealed record Pending(InstallCandidate? Candidate, string? ReplacingId, DateTimeOffset ExpiresAt, KnownEndpoint? Known = null, bool SignIn = false);
}
