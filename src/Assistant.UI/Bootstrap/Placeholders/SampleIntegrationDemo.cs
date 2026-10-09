using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>The developer's test of installing an integration (<c>demo integration</c>) and of the whole request that needs one (<c>demo integration request</c>).</summary>
internal interface ISampleIntegrationDemo
{
    /// <summary>The answer to <c>demo integration</c>: an offer to install the made-up sample integration, or why there is none.</summary>
    Task<MessageViewModel> OfferAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The answer to <c>demo integration request</c> (step 110): it turns the sample on for the request that is asked next ("List my notes in the Sample Notes app"),
    /// which the Assistant then answers with an offer of the sample and, once it is installed, carries out by itself.
    /// </summary>
    Task<MessageViewModel> StartRequestDemoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The Assistant's answer to <paramref name="request"/> when it is a request for the sample while the request demo is on and the sample is not installed: an
    /// offer to install it, which is set aside for the request like any other offer. <see langword="null"/> for every other request, which the Assistant answers as
    /// it always does (and so, once the sample is installed, goes straight to it).
    /// </summary>
    Task<ConnectedAppReply?> TryReplyAsync(string? request, CancellationToken cancellationToken);

    /// <summary>Whether the demo made the offer <paramref name="offerId"/>.</summary>
    bool Owns(string offerId);

    /// <summary>Installs what the offer <paramref name="offerId"/> offered, which the user approved by clicking Install.</summary>
    Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken);

    /// <summary>The user turned the offer <paramref name="offerId"/> down.</summary>
    void Decline(string offerId);
}

/// <summary>
/// The developer's test of installing an integration without any web and without risk (PROJECT_SPEC §4.8, step 108): <c>demo integration</c> packs "Sample Notes", a
/// made-up MCP server that touches no file and opens no connection and ships beside the app, into a bundle, serves that bundle from this PC's own loopback
/// address and offers it through the real approval panel. Clicking Install then runs the real installer (the download checked against its hash, the files unpacked
/// into the Assistant's integrations folder, the program started once to see that it speaks MCP, the integration recorded), and the sample is listed in
/// Settings > Integrations, where it can be reconnected, turned off, updated and removed. Nothing leaves the PC, and nothing is downloaded or run before the click.
/// The sample's note-listing tool is marked as one that only reads once it is installed, so it can be tried without the confirmation card, which is not built yet.
/// <c>demo integration request</c> (step 110) tries the rest: it only turns the sample on for the next request that names it, which then goes through the
/// Assistant as any request for an app that has no integration: it answers with the offer, the approval panel installs the sample, and the request is carried out by
/// itself, without being asked again; asked once more, it goes straight to the installed sample.
/// </summary>
internal sealed class SampleIntegrationDemo : ISampleIntegrationDemo, IDisposable
{
    /// <summary>The id the sample is installed under.</summary>
    internal const string SampleId = "samplenotes";

    private const string AppName = "Sample Notes";

    // The sample: the notes it lists only read, so they can be tried without the question that a change is asked.
    private static readonly SampleApp Notes = new(
        SampleId, AppName, "sample-notes", [], new IntegrationCapability(CapabilityAction.Read, "note"), ["list_notes"], ["list_notes"], IsSample: false);

    private readonly IInstalledIntegrationRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ISettingsLauncher? _settingsLauncher;
    private readonly SampleBundleHost _host;
    private readonly object _gate = new();
    private bool _requestDemoOn;

    public SampleIntegrationDemo(
        IntegrationLayout layout, IInstalledIntegrationRegistry registry, IManagedRuntimes runtimes, IMcpClientFactory clients, ISettingsService settings,
        IPermissionPolicy permissions, TimeProvider clock, ILoggerFactory loggers, ISettingsLauncher? settingsLauncher = null, string? serverDirectory = null,
        Assistant.Core.Audit.IAuditTrail? audit = null)
    {
        _registry = registry;
        _clock = clock;
        _settingsLauncher = settingsLauncher;
        _host = new SampleBundleHost(layout, registry, runtimes, clients, settings, permissions, clock, loggers, serverDirectory, audit);
    }

    /// <inheritdoc/>
    public async Task<MessageViewModel> OfferAsync(CancellationToken cancellationToken)
    {
        if (await _registry.GetAsync(SampleId, cancellationToken).ConfigureAwait(true) is not null)
        {
            return Prose(
                "The sample integration, Sample Notes, is installed already. Open Settings > Integrations to turn it off, reconnect it or remove it, or ask “List my notes in Sample Notes” to use it. "
                + "Remove it there and ask for “demo integration” again to install it again.");
        }

        if (await _host.OfferAsync(Notes, cancellationToken).ConfigureAwait(true) is not { } offer)
        {
            return Prose("The sample integration is not available here: its program is not beside the app.");
        }

        var panel = new IntegrationOfferContent(
            offer,
            (progress, token) => AcceptAsync(offer.OfferId, progress, token),
            () => Decline(offer.OfferId),
            _settingsLauncher is null ? null : () => _settingsLauncher.Show(Assistant.UI.Settings.SettingsSection.Integrations));

        var message = Prose(
            "I made up an integration to try this with: Sample Notes. It is not a real app. It touches no file and opens no connection, it is served from your own PC, and nothing has been "
            + "downloaded or run yet. Look it over and choose Install or Cancel. When it is installed it is listed in Settings > Integrations, and you can ask “List my notes in Sample Notes” to use it.");
        message.Content.Add(panel);
        return message;
    }

    /// <inheritdoc/>
    public async Task<MessageViewModel> StartRequestDemoAsync(CancellationToken cancellationToken)
    {
        if (await _registry.GetAsync(SampleId, cancellationToken).ConfigureAwait(true) is not null)
        {
            return Prose(
                "The sample integration, Sample Notes, is installed already, so there is nothing to offer: ask “List my notes in the Sample Notes app” and I go straight to it. "
                + "To see the offer and the way I carry on with your request, remove it in Settings > Integrations and ask for “demo integration request” again.");
        }

        if (!_host.HasProgram())
        {
            return Prose("The sample integration is not available here: its program is not beside the app.");
        }

        lock (_gate)
        {
            _requestDemoOn = true;
        }

        return Prose(
            "The sample integration, Sample Notes, is ready to try with a request. Ask me “List my notes in the Sample Notes app”. I have no integration for it yet, so I will offer the made-up "
            + "sample for you to look over. Nothing is downloaded or run until you click Install; when it is installed I carry on with your request without you asking again, and if you cancel I say "
            + "that nothing was done. Then ask the same thing once more: I go straight to the installed sample.");
    }

    /// <inheritdoc/>
    public async Task<ConnectedAppReply?> TryReplyAsync(string? request, CancellationToken cancellationToken)
    {
        bool on;
        lock (_gate)
        {
            on = _requestDemoOn;
        }

        if (!on || IntegrationRequestReader.Instance.Read(request) is not { AppKey: "samplenotes" } need
            || await _registry.GetAsync(SampleId, cancellationToken).ConfigureAwait(false) is not null
            || await _host.OfferAsync(Notes, cancellationToken).ConfigureAwait(false) is not { } offer)
        {
            return null;
        }

        var wanted = need.Capability.Action == CapabilityAction.Create ? "add a note" : "read your notes";
        var text =
            $"I can't {wanted} in {AppName} yet: no {AppName} integration is installed. I have a made-up one for trying this, so I put it below for you to look over. It touches no file and "
            + "opens no connection, and nothing has been downloaded, installed or run yet. I only install it if you click Install. Then I carry on with what you asked.";
        var notInstalled = $"I didn't install the {AppName} integration, so I didn't {wanted} in {AppName}. Ask me again whenever you want to set it up.";
        return new ConnectedAppReply(text, ConnectedAppReplyKind.InstallOffered, offer, notInstalled);
    }

    /// <inheritdoc/>
    public bool Owns(string offerId) => _host.Owns(offerId);

    /// <inheritdoc/>
    public Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken) =>
        _host.AcceptAsync(offerId, progress, cancellationToken);

    /// <inheritdoc/>
    public void Decline(string offerId) => _host.Decline(offerId);

    /// <inheritdoc/>
    public void Dispose() => _host.Dispose();

    private MessageViewModel Prose(string text) => new(MessageRole.Assistant, text) { CreatedAt = _clock.GetUtcNow() };
}

/// <summary>
/// The Assistant's answer to a request for an app (<see cref="IConnectedAppRequestHandler"/>) with one addition for <c>demo integration request</c> (step 110): while that demo is on,
/// a request for the made-up sample, which has no integration installed, is answered with the offer of the sample instead of the Assistant's search of the web. Every other request,
/// and every request once the sample is installed, is the Assistant's own handler's, unchanged.
/// </summary>
internal sealed class SampleAwareRequestHandler(IConnectedAppRequestHandler inner, ISampleIntegrationDemo demo) : IConnectedAppRequestHandler
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

/// <summary>
/// The Assistant's broker of offers (<see cref="IIntegrationOffers"/>) with one addition: the offers the sample demo made are accepted and declined by the demo, whose
/// installer may download the sample from this PC's own loopback address, which the app's own never may. Every other offer is the real broker's.
/// </summary>
internal sealed class SampleAwareOffers(IIntegrationOffers inner, ISampleIntegrationDemo demo) : IIntegrationOffers
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
