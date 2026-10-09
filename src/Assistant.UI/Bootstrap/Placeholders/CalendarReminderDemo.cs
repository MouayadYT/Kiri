using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.People;
using Assistant.Core.Permissions;
using Assistant.Tools.Integrations;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>The developer's test of the whole calendar-to-brother request (<c>demo reminder</c>).</summary>
internal interface ICalendarReminderDemo
{
    /// <summary>Whether the demo is on.</summary>
    bool IsOn { get; }

    /// <summary>The answer to <c>demo reminder</c>: turns the demo on, and says what to ask.</summary>
    Task<MessageViewModel> StartAsync(CancellationToken cancellationToken);

    /// <summary>The answer to <c>demo reminder off</c>: turns the demo off.</summary>
    MessageViewModel Stop();

    /// <summary>
    /// The Assistant's answer to <paramref name="request"/> while the demo is on and the request needs a calendar or a messaging app that is not installed: an offer to install the
    /// made-up one, which is set aside for the request like any other offer. <see langword="null"/> for every other request.
    /// </summary>
    Task<ConnectedAppReply?> TryReplyAsync(string? request, CancellationToken cancellationToken);

    /// <summary>The made-up people, who stand in for the user's own list while the demo is on: a brother, Omar.</summary>
    Task<IPersonResolver> PeopleAsync(CancellationToken cancellationToken);

    /// <summary>Whether the demo made the offer <paramref name="offerId"/>.</summary>
    bool Owns(string offerId);

    /// <summary>Installs what the offer <paramref name="offerId"/> offered, which the user approved by clicking Install.</summary>
    Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken);

    /// <summary>The user turned the offer <paramref name="offerId"/> down.</summary>
    void Decline(string offerId);
}

/// <summary>
/// <c>demo reminder</c> (PROJECT_SPEC §4.8, step 116): the whole request "Check my calendar for exams in the next two weeks and message my brother to remind him", carried out by the
/// real Assistant with made-up parts. The request is the user's to type once the demo is on; it goes through the Assistant as any request does. The Assistant has no calendar and no messaging
/// app yet, so it offers the made-up ones (Sample Calendar and Sample Messages, two integrations that ship beside the app, installed by the real installer from this PC's own address, only on
/// the user's click) and, each time one is installed, carries on with the request by itself. Then the real model, in the real loop, reads the calendar through the generic integration
/// route, picks out the exams, finds who "my brother" is in a made-up list of people that stands in for the user's own while the demo is on (Omar, with a made-up number; the user's
/// People are not read, and nothing is saved in them), writes the reminder, and the real question is asked inline, with the person, where the message would go and the whole text. Only a yes
/// sends it, and the sample messaging app sends it to no one. While the demo is on Calendar and Messaging are allowed (<see cref="TemporaryPermissionGrants"/>: the policy is not wrapped and
/// nothing is turned on in Settings), and "demo reminder off" takes that and the made-up brother away again.
/// </summary>
internal sealed class CalendarReminderDemo : ICalendarReminderDemo
{
    private static readonly SampleApp Calendar = new(
        "samplecalendar", "Sample Calendar", "sample-calendar", ["--app", "calendar"], new IntegrationCapability(CapabilityAction.Read, "event"),
        ["list_events", "search_events"], ["list_events", "search_events"], IsSample: true);

    private static readonly SampleApp Messages = new(
        "samplemessages", "Sample Messages", "sample-messages", ["--app", "messages"], new IntegrationCapability(CapabilityAction.Send, "message"),
        ["send_message"], ["search_chats"], IsSample: true);

    private readonly SampleBundleHost _host;
    private readonly IInstalledIntegrationRegistry _registry;
    private readonly IIntegrationResolver _resolver;
    private readonly CapabilityNeedSwitch _needs;
    private readonly TemporaryPermissionGrants _grants;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _peopleGate = new(1, 1);
    private IPersonResolver? _people;
    private int _on;

    public CalendarReminderDemo(
        SampleBundleHost host, IInstalledIntegrationRegistry registry, IIntegrationResolver resolver, CapabilityNeedSwitch needs, TemporaryPermissionGrants grants, TimeProvider clock)
    {
        _host = host;
        _registry = registry;
        _resolver = resolver;
        _needs = needs;
        _grants = grants;
        _clock = clock;
    }

    /// <inheritdoc/>
    public bool IsOn => Volatile.Read(ref _on) != 0;

    /// <inheritdoc/>
    public async Task<MessageViewModel> StartAsync(CancellationToken cancellationToken)
    {
        if (!_host.HasProgram())
        {
            return Prose("The reminder demo is not available here: the program of the made-up calendar and messaging app is not beside the app.");
        }

        Volatile.Write(ref _on, 1);
        _needs.IsOn = true;
        _grants.Grant(PermissionCapability.Calendar, PermissionCapability.Messaging);
        await PeopleAsync(cancellationToken).ConfigureAwait(true);
        return Prose(
            "The reminder demo is on. Everything in it is made up: a calendar, a messaging app and a brother called Omar, who is not in your own People. Nothing leaves this PC and no one is messaged.\n\n"
            + "Ask me: “Check my calendar for exams in the next two weeks and message my brother to remind him.”\n\n"
            + "I have no calendar or messaging integration yet, so I offer the made-up ones for you to look over. I install one only when you click Install, and each time I carry on with your request "
            + "by myself. Then I read the calendar, pick out the exams, find your brother (Omar), write the reminder and ask you before I send it: the question shows who it is for, where it goes and "
            + "the whole text, and only your yes sends it (to no one: it is a sample). Ask “demo reminder off” to take the demo away; the two samples stay installed until you remove them in "
            + "Settings > Integrations.");
    }

    /// <inheritdoc/>
    public MessageViewModel Stop()
    {
        Volatile.Write(ref _on, 0);
        _needs.IsOn = false;
        _grants.Revoke(PermissionCapability.Calendar, PermissionCapability.Messaging);
        return Prose("The reminder demo is off. Your own People and Permissions are as they were. The two made-up integrations stay installed until you remove them in Settings > Integrations.");
    }

    /// <inheritdoc/>
    public async Task<ConnectedAppReply?> TryReplyAsync(string? request, CancellationToken cancellationToken)
    {
        if (!IsOn)
        {
            return null;
        }

        foreach (var need in CapabilityRequestReader.Read(request))
        {
            var sample = need.Capability.Object == "event" ? Calendar : Messages;

            // Something that does it is installed already (the sample, or an integration of the user's own): nothing is offered for it.
            if (await _registry.GetAsync(sample.Id, cancellationToken).ConfigureAwait(false) is not null
                || (await _resolver.ResolveAsync(need, cancellationToken).ConfigureAwait(false)).Kind == IntegrationResolutionKind.UseInstalled)
            {
                continue;
            }

            if (await _host.OfferAsync(sample, cancellationToken).ConfigureAwait(false) is not { } offer)
            {
                return null;
            }

            var (wanted, detail) = need.Capability.Object == "event"
                ? ("read your calendar", "Its events are made up around today's date, so there are exams in the next two weeks, and it reads no real calendar.")
                : ("send a message", "It sends to no one: a message is only said to have been sent, and it keeps nothing.");
            var text =
                $"I can't {wanted} yet: no {(need.Capability.Object == "event" ? "calendar" : "messaging")} integration is installed. I have a made-up one for trying this, {sample.AppName}, so I put it below "
                + $"for you to look over. {detail} It opens no connection, and nothing has been downloaded, installed or run yet. I only install it if you click Install. Then I carry on with what you asked.";
            var notInstalled = $"I didn't install {sample.AppName}, so I didn't {wanted}. Ask me again whenever you want to set it up.";
            return new ConnectedAppReply(text, ConnectedAppReplyKind.InstallOffered, offer, notInstalled);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<IPersonResolver> PeopleAsync(CancellationToken cancellationToken)
    {
        await _peopleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_people is null)
            {
                // A list that exists only for this demo and is never written anywhere: the user's own people are neither read nor changed.
                var store = new InMemoryPersonStore(_clock);
                var now = _clock.GetUtcNow();
                await store.SaveAsync(
                    Person.Create("Omar", now) with
                    {
                        Relationships = ["Brother"],
                        Aliases = ["Bro"],
                        Identifiers = [new PersonIdentifier(PersonIdentifierKind.Phone, "+1 555 0100", "Messages")],
                    },
                    cancellationToken).ConfigureAwait(false);
                _people = new PersonResolver(store);
            }

            return _people;
        }
        finally
        {
            _peopleGate.Release();
        }
    }

    /// <inheritdoc/>
    public bool Owns(string offerId) => _host.Owns(offerId);

    /// <inheritdoc/>
    public Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken) =>
        _host.AcceptAsync(offerId, progress, cancellationToken);

    /// <inheritdoc/>
    public void Decline(string offerId) => _host.Decline(offerId);

    private MessageViewModel Prose(string text) => new(MessageRole.Assistant, text) { CreatedAt = _clock.GetUtcNow() };
}

/// <summary>
/// The Assistant's answer to a request for an app (<see cref="IConnectedAppRequestHandler"/>) with one addition for <c>demo reminder</c> (step 116): while the demo is on, a request that needs a
/// calendar or a messaging app that is not installed is answered with the offer of the made-up one instead of the Assistant's search of the web. Every other request, and every request once the
/// samples are installed, is the Assistant's own handler's, unchanged.
/// </summary>
internal sealed class ReminderAwareRequestHandler(IConnectedAppRequestHandler inner, ICalendarReminderDemo demo) : IConnectedAppRequestHandler
{
    /// <inheritdoc/>
    public async Task<ConnectedAppReply?> TryAnswerAsync(ToolContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            if (await demo.TryReplyAsync(context.Request, cancellationToken).ConfigureAwait(false) is { } reply)
            {
                return reply;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // A demo that cannot offer the sample is a request for the Assistant, as always.
        }

        return await inner.TryAnswerAsync(context, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The Assistant's broker of offers with one addition: the offers the reminder demo made are accepted and declined by the demo, whose installer may download from this PC's own address.</summary>
internal sealed class ReminderAwareOffers(IIntegrationOffers inner, ICalendarReminderDemo demo) : IIntegrationOffers
{
    /// <inheritdoc/>
    public Task<IntegrationOffer> OfferAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) => inner.OfferAsync(candidate, cancellationToken);

    /// <inheritdoc/>
    public Task<IntegrationOffer> OfferConnectAsync(KnownEndpoint endpoint, InstalledIntegration? existing, IntegrationCapability? capability, CancellationToken cancellationToken = default) =>
        inner.OfferConnectAsync(endpoint, existing, capability, cancellationToken);

    /// <inheritdoc/>
    public Task<IntegrationOffer> OfferUpdateAsync(InstallCandidate candidate, InstalledIntegration current, CancellationToken cancellationToken = default) =>
        inner.OfferUpdateAsync(candidate, current, cancellationToken);

    /// <inheritdoc/>
    public Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
        demo.Owns(offerId) ? demo.AcceptAsync(offerId, progress, cancellationToken) : inner.AcceptAsync(offerId, progress, cancellationToken);

    /// <inheritdoc/>
    public void Decline(string offerId)
    {
        if (demo.Owns(offerId))
        {
            demo.Decline(offerId);
        }
        else
        {
            inner.Decline(offerId);
        }
    }
}

/// <summary>
/// The people the Assistant resolves a name against (<see cref="IPersonResolver"/>) with one addition for <c>demo reminder</c>: while the demo is on, "my brother" is answered from the demo's
/// own made-up list (Omar) and not from the user's People, which are neither read nor changed. When the demo is off it is the user's own list, as always.
/// </summary>
internal sealed class ReminderAwarePeople(IPersonResolver inner, ICalendarReminderDemo demo) : IPersonResolver
{
    /// <inheritdoc/>
    public async Task<PersonResolution> ResolveAsync(string? reference, CancellationToken cancellationToken = default) =>
        demo.IsOn
            ? await (await demo.PeopleAsync(cancellationToken).ConfigureAwait(false)).ResolveAsync(reference, cancellationToken).ConfigureAwait(false)
            : await inner.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
}
