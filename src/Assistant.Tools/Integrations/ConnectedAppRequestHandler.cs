using Assistant.Core.Calendar;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.Permissions;
using Assistant.Tools.Messaging.ConnectedApps;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Answers a request for something of an external app that the Assistant cannot serve now, by itself and not through the model (PROJECT_SPEC §4.8, steps 105-106).
/// The request is resolved in a fixed order (<see cref="IIntegrationResolver"/>): a working installed integration is simply used (the model is
/// then asked as always, with its tools); an installed one that needs fixing, a server already set up in another program, and a request to delete
/// are each answered with what is true; and only when nothing is installed or on this PC, or the one installed has nothing for this, the
/// Integration Finder is asked (<see cref="IIntegrationFinder"/>) and the answer says what it found, or why it did not look. So "Add 'buy milk' to
/// Microsoft To Do" with no integration is never answered by a model that pretends. When what the finder found passes the review, the answer offers
/// it, and the offer carries the Assistant's words for the case that the user does not install it (step 110: the request is set aside and goes on by
/// itself once they have). While it looks, the Searching chip shows and can be pressed; a stop, from the chip or the conversation, is answered with
/// what is true: that nothing was installed and the request was not carried out. Nothing here can fail a turn, and nothing is logged but outcomes.
/// </summary>
/// <remarks>
/// A request that names no app but asks for something an integration does ("check my calendar", "message my brother"; step 116) is read the same way
/// (<see cref="CapabilityRequestReader"/>) while <see cref="CapabilityNeedSwitch"/> is on: the first need that nothing serves goes through the same finder, review, offer and
/// resume as a request for a named app, and when every need is served (an installed integration with a tool for it, or a calendar or messaging provider the app has) the request is
/// the model's. The switch is off unless something turns it on, so a request that names no app is the model's, as it always was.
/// </remarks>
internal sealed partial class ConnectedAppRequestHandler : IConnectedAppRequestHandler
{
    private readonly IIntegrationResolver _resolver;
    private readonly IIntegrationFinder _finder;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConnectedAppRequestHandler> _logger;
    private readonly ICandidateReviewer? _reviewer;
    private readonly IIntegrationOffers? _offers;
    private readonly IActivityTracker? _activity;
    private readonly CapabilityNeedSwitch? _capabilityNeeds;
    private readonly ICalendarProvider? _calendar;
    private readonly IMessagingProvider? _messaging;
    private readonly IPermissionGate? _gate;
    private readonly ISettingsService? _settings;
    private readonly Func<string, KnownEndpoint?>? _knownEndpoints;

    /// <summary>Creates the handler.</summary>
    /// <param name="resolver">Decides what the request needs.</param>
    /// <param name="finder">Looks for integrations when none is usable.</param>
    /// <param name="clock">The time.</param>
    /// <param name="logger">Where outcomes are logged.</param>
    /// <param name="reviewer">Reviews what the finder found before any of it can be offered (step 107); without it the candidates are only listed.</param>
    /// <param name="offers">Makes the offer the user approves or declines (step 108); without it nothing is offered.</param>
    /// <param name="activity">Shows the Searching chip while the Assistant looks for an integration, and lets the user stop it from there (step 110).</param>
    /// <param name="capabilityNeeds">Whether a request that names no app is read for what it needs (step 116); without it, or while it is off, only a named app is.</param>
    /// <param name="calendar">The calendar provider the app has, if any: a need to read a calendar is served by it.</param>
    /// <param name="messaging">The messaging provider the app has, if any: a need to send a message is served by it, unless it is only the way to reach an installed integration.</param>
    /// <param name="gate">Asks the user before a look on the web when External Web and Image Search is set to ask every time (step 119); without it such a look is not made.</param>
    /// <param name="settings">Says whether Local Only mode is on, so that the user is not asked about a look that would be refused anyway.</param>
    /// <param name="knownEndpoints">Finds the server of an app the Assistant knows the address of (<see cref="KnownEndpoints.For"/>): such an app is connected and signed in to, not searched for; without it every app is searched for.</param>
    public ConnectedAppRequestHandler(
        IIntegrationResolver resolver,
        IIntegrationFinder finder,
        TimeProvider clock,
        ILogger<ConnectedAppRequestHandler> logger,
        ICandidateReviewer? reviewer = null,
        IIntegrationOffers? offers = null,
        IActivityTracker? activity = null,
        CapabilityNeedSwitch? capabilityNeeds = null,
        ICalendarProvider? calendar = null,
        IMessagingProvider? messaging = null,
        IPermissionGate? gate = null,
        ISettingsService? settings = null,
        Func<string, KnownEndpoint?>? knownEndpoints = null)
    {
        _knownEndpoints = knownEndpoints;
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(finder);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _finder = finder;
        _clock = clock;
        _logger = logger;
        _reviewer = reviewer;
        _offers = offers;
        _activity = activity;
        _capabilityNeeds = capabilityNeeds;
        _calendar = calendar;
        _messaging = messaging;
        _gate = gate;
        _settings = settings;
    }

    /// <inheritdoc/>
    public async Task<ConnectedAppReply?> TryAnswerAsync(ToolContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Request))
        {
            return null;
        }

        try
        {
            var resolution = await _resolver.ResolveRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (resolution.Kind == IntegrationResolutionKind.NotAnAppRequest && _capabilityNeeds is { IsOn: true })
            {
                resolution = await FirstUnservedNeedAsync(context.Request, cancellationToken).ConfigureAwait(false) ?? resolution;
            }

            // An app whose server is known (Todoist, Notion, Beeper on this PC and the others on the list) is connected, not searched for: nothing is found, reviewed
            // or downloaded, and the user signs in in their browser. The same for one that is connected and whose sign-in has run out.
            if (await ConnectReplyAsync(resolution, cancellationToken).ConfigureAwait(false) is { } connect)
            {
                LogAnswered(_logger, connect.Kind);
                return connect;
            }

            IntegrationDiscoveryResult? discovery = null;
            IReadOnlyList<CandidateReview>? reviews = null;
            IntegrationOffer? offer = null;
            if (resolution is { DiscoveryAllowed: true, Need: { } need })
            {
                // When External Web and Image Search is set to ask every time, the user is asked before anything is sent (step 119), and the yes is for this one look: it covers
                // the finder, the review of what it found and the offer, which all read the permission, and nothing after. A look that is refused by Local Only mode is not asked about.
                PermissionGrant? grant = null;
                if (_gate is not null && !await LocalOnlyAsync(cancellationToken).ConfigureAwait(false))
                {
                    grant = await _gate.RequestAsync(PermissionCapability.ExternalSearch, $"Look for an integration for {need.AppName} on the web.", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (grant?.Decision.Reason is PermissionDecisionReason.Declined or PermissionDecisionReason.CouldNotAsk)
                {
                    discovery = IntegrationDiscoveryResult.BlockedBy(DiscoveryBlock.NotAllowedNow, _clock.GetUtcNow());
                }
                else
                {
                    // The approval is opened here, in this method, so that everything below runs under it.
                    using var approval = grant?.Enter();

                    // The Searching chip shows while the Assistant looks, and pressing it stops the look: a stop, whoever asked, ends it here.
                    using var looking = _activity?.Begin(ActivityKind.WebSearch, cancellationToken: cancellationToken);
                    var lookingToken = looking?.CancellationToken ?? cancellationToken;
                    try
                    {
                        // An integration that is installed and has nothing for this is not offered again.
                        IReadOnlyCollection<string> installedOrigin = resolution.Integration?.Source.Origin is { } origin ? [origin] : [];
                        discovery = await _finder.FindAsync(need, installedOrigin, lookingToken).ConfigureAwait(false);

                        // What was found is not installed, and not even offered, until it passed the review. The first that does is offered to the user,
                        // who is the only one who can say yes.
                        if (_reviewer is not null && discovery is { Status: DiscoveryStatus.Found, Candidates.Count: > 0 })
                        {
                            reviews = await _reviewer.ReviewAllAsync(discovery.Candidates, need, lookingToken).ConfigureAwait(false);
                            if (_offers is not null && reviews.FirstOrDefault(review => review.IsAccepted)?.Candidate is { } passed)
                            {
                                offer = await _offers.OfferAsync(passed, lookingToken).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (lookingToken.IsCancellationRequested)
                    {
                        // Nothing was installed, and the request is not carried out: the Assistant says so instead of going quiet.
                        LogAnswered(_logger, ConnectedAppReplyKind.LookupStopped);
                        return new ConnectedAppReply(IntegrationReplyWriter.LookupStopped(need), ConnectedAppReplyKind.LookupStopped);
                    }
                }
            }

            var reply = IntegrationReplyWriter.Write(resolution, discovery, _clock.GetUtcNow(), reviews, offer);
            if (reply is not null)
            {
                LogAnswered(_logger, reply.Kind);
            }

            return reply;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // A request that could not be checked is a request for the model, as it always was.
            LogFailed(_logger, exception.GetType().Name);
            return null;
        }
    }

    // The offer to connect a known app, or to sign in to an installed one again; null when the resolution is neither.
    private async Task<ConnectedAppReply?> ConnectReplyAsync(IntegrationResolution resolution, CancellationToken cancellationToken)
    {
        if (_offers is null || _knownEndpoints is null || resolution.Need is not { IsForAnyApp: false } need)
        {
            return null;
        }

        InstalledIntegration? existing = null;
        KnownEndpoint? endpoint;
        var signIn = false;
        if (resolution is { Kind: IntegrationResolutionKind.InstalledNotUsable, Problem: InstalledProblem.NeedsSignIn, Integration: { Authentication.Kind: IntegrationAuthKind.OAuth } installed })
        {
            existing = installed;
            signIn = true;
            endpoint = _knownEndpoints?.Invoke(installed.Id) ?? Describe(installed);
            if (endpoint is null)
            {
                return null;
            }
        }
        else if (resolution.Kind == IntegrationResolutionKind.NotInstalled && _knownEndpoints?.Invoke(need.AppKey) is { } known)
        {
            endpoint = known;
        }
        else
        {
            return null;
        }

        if (!endpoint.RunsOnThisPc && await LocalOnlyAsync(cancellationToken).ConfigureAwait(false))
        {
            return IntegrationReplyWriter.ConnectBlockedByLocalOnly(need, endpoint);
        }

        var offer = await _offers.OfferConnectAsync(endpoint, existing, need.Capability, cancellationToken).ConfigureAwait(false);
        return IntegrationReplyWriter.ConnectOffer(need, endpoint, offer, signIn);
    }

    // An integration that signs in with OAuth and is not on the list of known apps, described as one, to be signed in to again.
    private static KnownEndpoint? Describe(InstalledIntegration integration) =>
        integration.Transport.Kind != Mcp.McpTransportKind.Stdio && Uri.TryCreate(integration.Transport.Endpoint, UriKind.Absolute, out var address)
            ? new KnownEndpoint(integration.Id, integration.Name, address.AbsoluteUri, address.Host, address.IsLoopback)
            : null;

    private async Task<bool> LocalOnlyAsync(CancellationToken cancellationToken) =>
        _settings is not null && (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Privacy.LocalOnly;

    // The first need of a request that names no app (step 116) that nothing serves yet; null when the request has none or every one is served. A need is served by an installed
    // integration that works and has a tool for it, or by a provider the app has of its own; one an installed integration has but cannot be used for now (turned off, signed out) is
    // answered with that, and never looked for again.
    private async Task<IntegrationResolution?> FirstUnservedNeedAsync(string request, CancellationToken cancellationToken)
    {
        foreach (var need in CapabilityRequestReader.Read(request))
        {
            var resolution = await _resolver.ResolveAsync(need, cancellationToken).ConfigureAwait(false);
            if (resolution.Kind == IntegrationResolutionKind.UseInstalled || await ServedByAProviderAsync(need, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            return resolution;
        }

        return null;
    }

    private async Task<bool> ServedByAProviderAsync(IntegrationNeed need, CancellationToken cancellationToken) => need.Capability.Object switch
    {
        "event" => _calendar is not null,
        "message" => _messaging is not null and not McpMessagingProvider && await _messaging.IsAvailableAsync(cancellationToken).ConfigureAwait(false),
        _ => false,
    };

    [LoggerMessage(EventId = 3160, Level = LogLevel.Information, Message = "External app request answered by the Assistant: {Kind}")]
    private static partial void LogAnswered(ILogger logger, ConnectedAppReplyKind kind);

    [LoggerMessage(EventId = 3161, Level = LogLevel.Warning, Message = "External app request could not be checked: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
