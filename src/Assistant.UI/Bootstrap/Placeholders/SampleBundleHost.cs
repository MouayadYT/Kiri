using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Assistant.Core.Audit;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>
/// One of the made-up integrations that ship beside the app and that a demo offers (<c>demo integration</c>, <c>demo reminder</c>): what it is called, how its one program is started
/// to be it, what it can do, and what is set on it once the user has installed it.
/// </summary>
/// <param name="Id">The id it is installed under.</param>
/// <param name="AppName">The name the user sees.</param>
/// <param name="PackageName">The name of its bundle, which is also the start of the file's name.</param>
/// <param name="ServerArguments">What the program is started with to be this sample (the program is the same for every sample).</param>
/// <param name="Capability">What it can do, as the offer says it.</param>
/// <param name="MatchedTools">The tools that do it, as the offer says them.</param>
/// <param name="ReadOnlyTools">The tools that only read, which are marked so once it is installed, so that they run without the question a change is asked.</param>
/// <param name="IsSample">Whether it is marked as made up, so that what is sent through it is said to reach no one.</param>
internal sealed record SampleApp(
    string Id,
    string AppName,
    string PackageName,
    IReadOnlyList<string> ServerArguments,
    IntegrationCapability Capability,
    IReadOnlyList<string> MatchedTools,
    IReadOnlyList<string> ReadOnlyTools,
    bool IsSample)
{
    /// <summary>The bundle's file name.</summary>
    public string FileName => PackageName + "-1.0.0.mcpb";
}

/// <summary>
/// What the demos that install a made-up integration have in common (PROJECT_SPEC §4.8, steps 108 and 116): the program that ships beside the app is packed into an MCP bundle
/// with the manifest for the sample asked for, the bundle is served from this PC's own loopback address, an offer for it is planned by the real broker of offers, and the real
/// installer (the one difference from the app's: it may download from this PC's loopback address, which the app's own never may) installs it when, and only when, the user clicks
/// Install. Nothing leaves the PC and nothing is downloaded or run before the click.
/// </summary>
internal sealed class SampleBundleHost : IDisposable
{
    private const string ServerName = "Assistant.SampleMcpServer";

    private readonly IntegrationLayout _layout;
    private readonly IInstalledIntegrationRegistry _registry;
    private readonly IManagedRuntimes _runtimes;
    private readonly IMcpClientFactory _clients;
    private readonly ISettingsService _settings;
    private readonly IPermissionPolicy _permissions;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;
    private readonly string _serverDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _served = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstallCandidate> _candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SampleApp> _offerApps = new(StringComparer.Ordinal);
    private HttpListener? _listener;
    private readonly IAuditTrail? _audit;
    private IIntegrationOffers? _offers;

    public SampleBundleHost(
        IntegrationLayout layout, IInstalledIntegrationRegistry registry, IManagedRuntimes runtimes, IMcpClientFactory clients, ISettingsService settings,
        IPermissionPolicy permissions, TimeProvider clock, ILoggerFactory loggers, string? serverDirectory = null, IAuditTrail? audit = null)
    {
        _audit = audit;
        _layout = layout;
        _registry = registry;
        _runtimes = runtimes;
        _clients = clients;
        _settings = settings;
        _permissions = permissions;
        _clock = clock;
        _loggers = loggers;
        _serverDirectory = serverDirectory ?? AppContext.BaseDirectory;
    }

    /// <summary>Whether the samples' program is beside the app.</summary>
    public bool HasProgram() => ProgramFiles().All(File.Exists);

    /// <summary>The offer of <paramref name="app"/>: its bundle is made, served from this PC and planned (nothing is downloaded or run). Null when the program is not here.</summary>
    public async Task<IntegrationOffer?> OfferAsync(SampleApp app, CancellationToken cancellationToken)
    {
        InstallCandidate candidate;
        IIntegrationOffers offers;
        lock (_gate)
        {
            if (!_candidates.TryGetValue(app.Id, out var made))
            {
                var bundle = BuildBundle(app);
                if (bundle is null)
                {
                    return null;
                }

                var url = Serve(app, bundle);
                var hash = new ContentHash("sha256", Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant());
                var source = new InstallSource
                {
                    Kind = InstallSourceKind.Bundle,
                    Identifier = url,
                    Version = "1.0.0",
                    DownloadUrl = url,
                    Hash = hash,
                    SizeBytes = bundle.Length,
                };
                made = new InstallCandidate
                {
                    Id = app.Id,
                    AppName = app.AppName,
                    Name = $"{app.PackageName} (a made-up test package)",
                    Source = source,
                    Trust = CandidateTrust.Community,
                    Publisher = "this Assistant's own test sample",
                    License = "MIT",
                    LicenseStatus = LicenseStatus.Open,
                    Runtime = CandidateRuntime.None,
                    LeavesThisPc = false,
                    Capability = app.Capability,
                    Evidence = CapabilityEvidence.ToolListed,
                    MatchedTools = [.. app.MatchedTools],
                    ReviewedAt = _clock.GetUtcNow(),
                    Notes =
                    [
                        new ReviewFinding(ReviewSeverity.Caution, ReviewCode.Fact, "This is a made-up sample package that is served from this PC. It is only for trying the installation, and does nothing useful."),
                    ],
                    Fingerprint = InstallCandidate.FingerprintOf(app.Id, app.AppName, source),
                };
                _candidates[app.Id] = made;

                // A real installer, with the one difference that it may download from this PC's own loopback address, which the app's own never may.
                // What it does is in the activity log like what the app's own does (step 117).
                IIntegrationInstaller installer = new IntegrationInstaller(
                    _layout, _registry, _runtimes, new PackageDownloader(PackageDownloadPolicy.WithLoopback()), _clients, _settings, _permissions, _clock,
                    _loggers.CreateLogger<IntegrationInstaller>());
                IIntegrationOffers plain = new IntegrationOffers(
                    _audit is null ? installer : new AuditedIntegrationInstaller(installer, _audit),
                    _clock,
                    _loggers.CreateLogger<IntegrationOffers>());
                _offers ??= _audit is null ? plain : new AuditedIntegrationOffers(plain, _audit);
            }

            candidate = made;
            offers = _offers!;
        }

        var offer = await offers.OfferAsync(candidate, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _offerApps[offer.OfferId] = app;
        }

        return offer;
    }

    /// <summary>Whether this host made the offer <paramref name="offerId"/>.</summary>
    public bool Owns(string offerId)
    {
        lock (_gate)
        {
            return _offerApps.ContainsKey(offerId);
        }
    }

    /// <summary>Installs what the offer <paramref name="offerId"/> offered, which the user approved by clicking Install, and marks the sample as it is meant to be used.</summary>
    public async Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        IIntegrationOffers? offers;
        SampleApp? app;
        lock (_gate)
        {
            offers = _offers;
            _offerApps.TryGetValue(offerId, out app);
        }

        if (offers is null || app is null)
        {
            return InstallOutcome.Fail(InstallFailure.OfferExpired, "That offer has expired. Ask again and I will look it up again.");
        }

        var outcome = await offers.AcceptAsync(offerId, progress, cancellationToken).ConfigureAwait(true);
        if (outcome.IsInstalled)
        {
            // What only reads can be tried without the question that a change is asked, and a sample that sends is said to be made up.
            await _registry.UpdateAsync(
                app.Id,
                installed => installed with
                {
                    IsSample = app.IsSample || installed.IsSample,
                    Permissions = installed.Permissions with { ReadOnlyTools = [.. app.ReadOnlyTools] },
                },
                CancellationToken.None).ConfigureAwait(true);
        }

        return outcome;
    }

    /// <summary>The user turned the offer <paramref name="offerId"/> down.</summary>
    public void Decline(string offerId)
    {
        IIntegrationOffers? offers;
        lock (_gate)
        {
            offers = _offers;
        }

        offers?.Decline(offerId);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _listener?.Close();
            _listener = null;
        }
    }

    private IEnumerable<string> ProgramFiles() =>
        new[] { ".exe", ".dll", ".runtimeconfig.json", ".deps.json" }.Select(extension => Path.Combine(_serverDirectory, ServerName + extension));

    // The program, with the files it needs, and the manifest that says how to start it as this sample, as an MCP bundle. Null when the program is not here.
    private byte[]? BuildBundle(SampleApp app)
    {
        var files = ProgramFiles().ToList();
        if (files.Any(file => !File.Exists(file)))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
            {
                var arguments = string.Join(',', app.ServerArguments.Select(argument => "\"" + argument + "\""));
                writer.Write(
                    "{\"manifest_version\":\"0.2\",\"name\":\"" + app.PackageName + "\",\"version\":\"1.0.0\",\"description\":\"A made-up sample for trying the installation of integrations.\","
                    + "\"server\":{\"type\":\"binary\",\"entry_point\":\"" + ServerName + ".exe\",\"mcp_config\":{\"command\":\"${__dirname}/" + ServerName + ".exe\",\"args\":["
                    + arguments + "],\"env\":{}}}}");
            }

            foreach (var file in files)
            {
                archive.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

        return stream.ToArray();
    }

    // Serves the bundle at this PC's loopback address, for as long as the app runs, and returns its address. Called with the gate held.
    private string Serve(SampleApp app, byte[] bundle)
    {
        _served[app.FileName] = bundle;
        if (_listener is { IsListening: true })
        {
            return Url(_listener, app);
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }

            _listener = listener;
            _ = Task.Run(() => AnswerRequestsAsync(listener));
            return Url(listener, app);
        }

        throw new IOException("No port on this PC could be used to serve the sample.");
    }

    private static string Url(HttpListener listener, SampleApp app) => listener.Prefixes.First() + app.FileName;

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task AnswerRequestsAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                byte[]? bytes = null;
                if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath is { } path && path.StartsWith('/'))
                {
                    lock (_gate)
                    {
                        _served.TryGetValue(path[1..], out bytes);
                    }
                }

                if (bytes is not null)
                {
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
            }
            catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
            {
                // A client that went away is not a problem for the sample.
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
