using System.Globalization;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>How the installer is bounded.</summary>
public sealed record IntegrationInstallerOptions
{
    /// <summary>The most time starting a newly installed integration and reading its tools is given.</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

/// <summary>How a check of the connection to an integration went.</summary>
/// <param name="Connected">Whether the integration answered.</param>
/// <param name="Blocked">Whether it may not be connected to now (it is off, or Local Only mode is on and it would send something off this PC).</param>
/// <param name="Failure">Why it did not answer, when it did not.</param>
/// <param name="ToolCount">How many tools it offered, after a reconnect.</param>
/// <param name="Restarted">Whether its program had stopped and was started again.</param>
internal sealed record ConnectionCheck(bool Connected, bool Blocked, Mcp.McpFailure? Failure, int ToolCount, bool Restarted);

/// <summary>Looks after what is connected to the installed integrations (step 109).</summary>
internal interface IIntegrationConnections
{
    /// <summary>Ends the connection to the integration, and the program started for it, and forgets what was loaded from it.</summary>
    Task ForgetAsync(string integrationId, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the connection, forgets what was loaded and kept of the integration's tools, connects again (starting its program) and reads its tools. It is
    /// what the user asks for with Reconnect, and the only thing that starts a program to check on it.
    /// </summary>
    Task<ConnectionCheck> ReconnectAsync(string integrationId, CancellationToken cancellationToken);

    /// <summary>
    /// Asks an integration that is connected whether it is still there. It never starts a program: one that is not connected is not asked about, and one
    /// that does not answer is let go of, so that the next use starts it again.
    /// </summary>
    Task<ConnectionCheck> CheckHealthAsync(string integrationId, CancellationToken cancellationToken);
}

/// <summary>
/// Installs the integrations the user approved (PROJECT_SPEC §4.8, step 108). It is the only code that puts an integration on this PC, and it acts only on an
/// <see cref="InstallCandidate"/> that the review produced and the user approved (<see cref="IIntegrationOffers"/>), which it checks again (<see cref="InstallRules"/>):
/// an exact version, a checksum, an address on the fixed list, the fingerprint of what was reviewed. It works in the Assistant's own folders
/// (<c>Integrations\&lt;id&gt;\&lt;version&gt;</c>, <c>Runtimes</c>) and needs no administrator rights. The integration is downloaded and checked against the review's checksum, set
/// up with the runtime it needs (set up by the Assistant when missing, reused when there), and only then started, once, to see that it speaks MCP and
/// offers what was asked; it is recorded as installed only if that works, and when anything fails or the user cancels, everything it made is deleted.
/// What was installed is remembered (<c>install.json</c> beside it and <see cref="ManagedInstall"/> in the record) so that it is reused and not
/// downloaded again. One installation runs at a time. Logs say ids, kinds and codes only.
/// </summary>
public sealed partial class IntegrationInstaller : IIntegrationInstaller
{
    private const string RecordName = "install.json";

    private readonly IntegrationLayout _layout;
    private readonly IInstalledIntegrationRegistry _registry;
    private readonly IManagedRuntimes _runtimes;
    private readonly IMcpClientFactory _clients;
    private readonly ISettingsService _settings;
    private readonly IPermissionPolicy _permissions;
    private readonly TimeProvider _clock;
    private readonly IntegrationInstallerOptions _options;
    private readonly ILogger<IntegrationInstaller> _logger;
    private readonly IReadOnlyDictionary<InstallSourceKind, IPackageInstaller> _installers;
    private readonly IIntegrationConnections? _connections;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the installer that downloads through <paramref name="downloader"/> and sets runtimes up through <paramref name="runtimes"/>.</summary>
    public IntegrationInstaller(
        IntegrationLayout layout,
        IInstalledIntegrationRegistry registry,
        IManagedRuntimes runtimes,
        IPackageDownloader downloader,
        IMcpClientFactory clients,
        ISettingsService settings,
        IPermissionPolicy permissions,
        TimeProvider clock,
        ILogger<IntegrationInstaller> logger,
        IntegrationInstallerOptions? options = null)
        : this(layout, registry, runtimes, downloader, clients, settings, permissions, clock, logger, options, new ProcessRunner(), null)
    {
    }

    internal IntegrationInstaller(
        IntegrationLayout layout,
        IInstalledIntegrationRegistry registry,
        IManagedRuntimes runtimes,
        IPackageDownloader downloader,
        IMcpClientFactory clients,
        ISettingsService settings,
        IPermissionPolicy permissions,
        TimeProvider clock,
        ILogger<IntegrationInstaller> logger,
        IntegrationInstallerOptions? options,
        IProcessRunner runner,
        IIntegrationConnections? connections)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runner);
        _layout = layout;
        _registry = registry;
        _runtimes = runtimes;
        _clients = clients;
        _settings = settings;
        _permissions = permissions;
        _clock = clock;
        _logger = logger;
        _options = options ?? new IntegrationInstallerOptions();
        _connections = connections;
        _installers = new Dictionary<InstallSourceKind, IPackageInstaller>
        {
            [InstallSourceKind.Npm] = new NpmPackageInstaller(downloader, runner),
            [InstallSourceKind.PyPi] = new PyPiPackageInstaller(downloader, runner),
            [InstallSourceKind.Bundle] = new BundlePackageInstaller(downloader, runtimes),
        };
    }

    /// <inheritdoc/>
    public Task<InstallPlan> PlanAsync(InstallCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Plan(candidate));
    }

    private InstallPlan Plan(InstallCandidate candidate)
    {
        if (InstallRules.Problems(candidate) is { Count: > 0 } problems)
        {
            return new InstallPlan { Blocker = problems[0] };
        }

        if (candidate.Source.Kind == InstallSourceKind.Remote)
        {
            return new InstallPlan();
        }

        var downloads = new List<PlannedDownload>();
        var reused = new List<string>();
        if (_installers[candidate.Source.Kind].RuntimeNeeded(candidate) is { } kind)
        {
            if (_runtimes.ReleaseFor(kind) is not { } release)
            {
                return new InstallPlan { Blocker = "I cannot set up what it needs on this kind of PC." };
            }

            if (_runtimes.Find(kind) is null)
            {
                downloads.Add(new PlannedDownload(true, release.DisplayName, release.ApproximateMegabytes));
            }
            else
            {
                reused.Add(release.DisplayName);
            }
        }

        var versionDirectory = _layout.VersionDirectory(candidate.Id, candidate.Source.Version!);
        var again = ReadComplete(versionDirectory, candidate) is not null;
        if (!again)
        {
            downloads.Add(new PlannedDownload(
                false,
                $"the {candidate.AppName} integration",
                candidate.Source.SizeBytes is > 0 and var size && candidate.Source.Kind != InstallSourceKind.Npm ? (int)Math.Max(1, Math.Round(size / 1_048_576.0)) : null));
        }

        return new InstallPlan { Downloads = downloads, ReusedRuntimes = reused, IntegrationAlreadyDownloaded = again };
    }

    /// <inheritdoc/>
    public Task<InstallOutcome> InstallAsync(InstallCandidate candidate, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(candidate, existingId: null, progress, cancellationToken);

    /// <inheritdoc/>
    public Task<InstallOutcome> UpdateAsync(
        InstallCandidate candidate, string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(integrationId);
        return RunAsync(candidate, integrationId, progress, cancellationToken);
    }

    /// <summary>
    /// Deletes what an interrupted installation left (<c>.staging</c>, a folder of an integration that is not installed, an old version that was replaced).
    /// It does nothing while an installation runs. Returns how many folders it deleted.
    /// </summary>
    public async Task<int> CleanUpAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        try
        {
            var installed = (await _registry.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(integration => integration.Id, StringComparer.Ordinal);
            var removed = 0;
            if (Directory.Exists(_layout.StagingRoot) && InstallFiles.DeleteDirectory(_layout.StagingRoot))
            {
                removed++;
            }

            if (Directory.Exists(_layout.Root))
            {
                foreach (var directory in Directory.EnumerateDirectories(_layout.Root))
                {
                    var id = Path.GetFileName(directory);
                    if (id.StartsWith('.') || !IntegrationRules.IsValidId(id))
                    {
                        continue;
                    }

                    if (!installed.TryGetValue(id, out var integration))
                    {
                        if (InstallFiles.DeleteDirectory(directory))
                        {
                            removed++;
                        }

                        continue;
                    }

                    // An old version that an update replaced, or an install that never finished.
                    foreach (var version in Directory.EnumerateDirectories(directory))
                    {
                        if (Path.GetFileName(version) != integration.InstalledVersion && InstallFiles.DeleteDirectory(version))
                        {
                            removed++;
                        }
                    }
                }
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<InstallOutcome> RunAsync(InstallCandidate candidate, string? existingId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return InstallOutcome.Fail(InstallFailure.Busy, "Another integration is being installed. Try again when it has finished.");
        }

        string? versionDirectory = null;
        var created = false;
        var downloads = _layout.NewStagingDirectory();
        InstallOutcome outcome;
        try
        {
            progress?.Report(new InstallProgress(InstallStep.Preparing, "Checking what I am about to install"));
            if (InstallRules.Problems(candidate) is { Count: > 0 })
            {
                return Finish(candidate, InstallOutcome.Fail(InstallFailure.NotAllowed, "I will not install that: it is not what was reviewed, or it breaks one of my rules."));
            }

            var existing = await _registry.GetAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
            if (existingId is null && existing is not null)
            {
                return Finish(candidate, InstallOutcome.Fail(InstallFailure.AlreadyInstalled, $"{candidate.AppName} is already installed."));
            }

            if (existingId is not null
                && (existing is null || existing.Managed is not { } before || before.Kind != candidate.Source.Kind
                    || candidate.Source.Kind != InstallSourceKind.Bundle && !string.Equals(before.Package, candidate.Source.Identifier, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(existing.InstalledVersion, candidate.Source.Version, StringComparison.Ordinal)))
            {
                return Finish(candidate, InstallOutcome.Fail(InstallFailure.NotAllowed, "That is not a newer version of what is installed."));
            }

            var source = candidate.Source;
            var kind = source.Kind;
            var runtimeKind = kind == InstallSourceKind.Remote ? null : _installers[kind].RuntimeNeeded(candidate);
            if (NeedsWeb(candidate, runtimeKind) && !await WebAllowedAsync(cancellationToken).ConfigureAwait(false))
            {
                return Finish(candidate, InstallOutcome.Fail(
                    InstallFailure.WebLocked,
                    "Downloading needs the web, and Local Only mode is on or External Web and Image Search is off. You can change that in Settings, then try again."));
            }

            PreparedInstall prepared;
            if (kind == InstallSourceKind.Remote)
            {
                // Nothing is downloaded or run: only where the server is gets recorded.
                prepared = new PreparedInstall(new IntegrationTransport { Kind = McpTransportKind.StreamableHttp, Endpoint = source.Identifier }, []);
            }
            else
            {
                versionDirectory = _layout.VersionDirectory(candidate.Id, source.Version!);
                if (ReadComplete(versionDirectory, candidate) is { } reuse)
                {
                    // Downloaded before and still whole: nothing is downloaded again.
                    prepared = reuse;
                }
                else
                {
                    ManagedRuntime? runtime = null;
                    if (runtimeKind is { } needed)
                    {
                        runtime = await _runtimes.EnsureAsync(needed, progress, cancellationToken).ConfigureAwait(false);
                    }

                    if (Directory.Exists(versionDirectory))
                    {
                        // What an interrupted installation left is not reused.
                        InstallFiles.DeleteDirectory(versionDirectory);
                    }

                    Directory.CreateDirectory(versionDirectory);
                    Directory.CreateDirectory(downloads);
                    created = true;
                    prepared = await _installers[kind].PrepareAsync(new PackageInstallContext(candidate, versionDirectory, downloads, runtime, progress), cancellationToken).ConfigureAwait(false);
                }
            }

            var record = BuildRecord(candidate, prepared, existing);
            if (IntegrationRules.Problems(record) is { Count: > 0 } || !LaunchIsInsideOurFolders(record))
            {
                throw new InstallException(InstallFailure.NotAllowed, "What I installed cannot be started by me, so I removed it.");
            }

            var checkedWith = Array.Empty<string>();
            McpServerInfo? server = null;
            if (kind != InstallSourceKind.Remote && candidate.RequiredSecrets.Count == 0)
            {
                // Started once, now that the user approved it, to see that it is an MCP server and offers what was asked.
                progress?.Report(new InstallProgress(InstallStep.Checking, "Checking that it starts and offers what you asked for"));
                (checkedWith, server) = await CheckAsync(record, candidate, existing, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new InstallProgress(InstallStep.Finishing, "Saving it as installed"));
            record = record with
            {
                Capabilities = checkedWith.Length == 0 && server is null
                    ? record.Capabilities
                    : new IntegrationCapabilities
                    {
                        Tools = true,
                        ProtocolVersion = server?.ProtocolVersion,
                        ToolNames = [.. checkedWith.Take(IntegrationRules.MaxToolNames)],
                        RefreshedAt = _clock.GetUtcNow(),
                    },
            };
            if (versionDirectory is not null && prepared.Transport.Kind == McpTransportKind.Stdio)
            {
                WriteRecord(versionDirectory, candidate, prepared, _clock.GetUtcNow());
            }

            string? removedTools = null;
            if (existing is null)
            {
                await _registry.AddAsync(record, cancellationToken).ConfigureAwait(false);
                created = false;
            }
            else
            {
                removedTools = RemovedTools(existing, checkedWith);
                await _registry.UpdateAsync(existing.Id, current => Replace(current, record), cancellationToken).ConfigureAwait(false);

                // From here on the new version is the installed one: nothing below may delete it.
                created = false;
                await RemoveOldVersionAsync(existing, candidate, cancellationToken).ConfigureAwait(false);
            }

            var saved = await _registry.GetAsync(candidate.Id, cancellationToken).ConfigureAwait(false) ?? record;
            outcome = new InstallOutcome
            {
                Status = InstallStatus.Installed,
                Message = DoneMessage(candidate, existing is not null, removedTools),
                Integration = saved,
                ToolNames = checkedWith,
            };
        }
        catch (OperationCanceledException)
        {
            outcome = InstallOutcome.Cancel();
        }
        catch (InstallException exception)
        {
            outcome = InstallOutcome.Fail(exception.Failure, exception.Message);
        }
        catch (IntegrationException exception)
        {
            outcome = exception.Failure switch
            {
                IntegrationFailure.Duplicate => InstallOutcome.Fail(InstallFailure.AlreadyInstalled, $"{candidate.AppName} is already installed."),
                IntegrationFailure.Invalid => InstallOutcome.Fail(InstallFailure.NotAllowed, "What I installed breaks one of my rules, so I removed it."),
                _ => InstallOutcome.Fail(InstallFailure.DiskFailed, "I could not save the list of installed integrations. Check that the disk has room."),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            outcome = InstallOutcome.Fail(InstallFailure.DiskFailed, "I could not write to my folder. Check that the disk has room.");
        }
        finally
        {
            // Whatever did not end up installed is deleted, and so are the downloads, which are never kept.
            if (created && versionDirectory is not null)
            {
                InstallFiles.DeleteDirectory(versionDirectory);
                RemoveIfEmpty(_layout.IntegrationDirectory(candidate.Id));
            }

            InstallFiles.DeleteDirectory(downloads);
            RemoveIfEmpty(_layout.StagingRoot);
            _gate.Release();
        }

        return Finish(candidate, outcome);
    }

    // Both locks must be open for anything that comes from the web: Local Only mode off, and the permission to use the web on. A permission set to ask every time counts as
    // open here (step 119): an installation is made only for an offer the user accepted by clicking Install on a panel that says what would be downloaded, which is the
    // question of that one use, and the user is not asked a second time about the same download.
    private async Task<bool> WebAllowedAsync(CancellationToken cancellationToken) =>
        !(await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Privacy.LocalOnly
        && (await _permissions.CheckAsync(PermissionCapability.ExternalSearch, cancellationToken).ConfigureAwait(false)).CouldBeAllowed;

    // A package from this PC (a loopback address) and needing nothing else is not the web. Everything else is: the package's file, the runtime, the dependencies.
    private static bool NeedsWeb(InstallCandidate candidate, RuntimeKind? runtimeKind)
    {
        if (candidate.Source.Kind == InstallSourceKind.Remote)
        {
            return false;
        }

        var local = candidate.Source.DownloadUrl is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) && PackageDownloadPolicy.IsLoopback(uri);
        return !(local && runtimeKind is null && candidate.Source.Kind == InstallSourceKind.Bundle);
    }

    private InstalledIntegration BuildRecord(InstallCandidate candidate, PreparedInstall prepared, InstalledIntegration? existing)
    {
        var secrets = candidate.RequiredSecrets
            .Select(name => new IntegrationSecretBinding(candidate.Authentication == IntegrationAuthKind.BearerToken ? "Authorization" : name, SecretName(candidate.Id, name)))
            .ToList();
        var source = candidate.Source;
        var sourceKind = candidate.FoundIn.Contains("mcp-registry", StringComparer.Ordinal)
            ? IntegrationSourceKind.OfficialRegistry
            : candidate.FoundIn.Count == 0 ? IntegrationSourceKind.UserAdded : IntegrationSourceKind.CommunityRegistry;
        var record = new InstalledIntegration
        {
            Id = candidate.Id,
            Name = candidate.AppName,
            Source = new IntegrationSource(sourceKind, source.Identifier),
            Transport = prepared.Transport,
            InstalledVersion = source.Version,
            Enabled = true,
            Authentication = secrets.Count == 0
                ? IntegrationAuthentication.None
                : new IntegrationAuthentication { Kind = candidate.Authentication, State = IntegrationAuthState.NeedsSignIn, Secrets = secrets },
            Permissions = new IntegrationPermissions { LeavesThisPc = candidate.LeavesThisPc },
            Managed = new ManagedInstall
            {
                Kind = source.Kind,
                Package = source.Identifier,
                Hash = source.Hash?.ToString(),
                Commit = candidate.CommitSha,
                Repository = candidate.RepositoryUrl,
                Publisher = candidate.Publisher,
                Trust = candidate.Trust,
                Runtime = prepared.Runtime?.Kind,
                RuntimeVersion = prepared.Runtime?.Version,
                InstalledAt = _clock.GetUtcNow(),
                Fingerprint = candidate.Fingerprint,
            },
        };

        // An update keeps what the user decided about the integration: whether it is on, what it may do, and which secrets it uses.
        return existing is null
            ? record
            : record with { Enabled = existing.Enabled, Permissions = existing.Permissions, Authentication = existing.Authentication, Source = existing.Source };
    }

    // The record an update leaves: the new way to start it and the new version, with everything else as it was.
    private static InstalledIntegration Replace(InstalledIntegration current, InstalledIntegration updated) =>
        current with
        {
            Transport = updated.Transport,
            InstalledVersion = updated.InstalledVersion,
            Managed = updated.Managed,
            Capabilities = updated.Capabilities.ToolNames.Count > 0 ? updated.Capabilities : current.Capabilities,
            Health = IntegrationHealth.Unknown,
        };

    // The name a secret is kept under: the integration and the variable, in the characters the store accepts.
    internal static string SecretName(string id, string name)
    {
        var cleaned = new string([.. name.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '-')]);
        var text = id + "." + cleaned;
        return text.Length <= 64 ? text : text[..64];
    }

    // The program an installed integration starts, and the folder it starts in, must be in the Assistant's own folders: the integrations and the runtimes.
    private bool LaunchIsInsideOurFolders(InstalledIntegration record)
    {
        if (record.Transport.Kind != McpTransportKind.Stdio)
        {
            return true;
        }

        var command = record.Transport.Command!;
        var directory = record.Transport.WorkingDirectory;
        return (_layout.IsInsideIntegrations(command) || _layout.IsInsideRuntimes(command))
            && (directory is null || _layout.IsInsideIntegrations(directory));
    }

    // Starts the integration once and reads its tools: it must speak MCP, offer tools and, when something was asked for, offer a tool for it.
    private async Task<(string[] ToolNames, McpServerInfo? Server)> CheckAsync(
        InstalledIntegration record, InstallCandidate candidate, InstalledIntegration? existing, CancellationToken cancellationToken)
    {
        IMcpClient client;
        try
        {
            client = _clients.Create(record);
        }
        catch (McpException)
        {
            throw new InstallException(InstallFailure.DidNotStart, $"{candidate.AppName} was installed, but I could not start it, so I removed it again.");
        }

        await using (client.ConfigureAwait(false))
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_options.CheckTimeout);
            IReadOnlyList<McpToolDescriptor> tools;
            try
            {
                await client.ConnectAsync(limit.Token).ConfigureAwait(false);
                tools = await client.ListToolsAsync(limit.Token).ConfigureAwait(false);
            }
            catch (McpException)
            {
                throw new InstallException(InstallFailure.DidNotStart, $"{candidate.AppName} was installed, but it did not start or did not answer as an MCP server does, so I removed it again.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InstallException(InstallFailure.DidNotStart, $"{candidate.AppName} was installed, but it did not start in time, so I removed it again.");
            }

            var names = tools.Select(tool => tool.Name).Where(IntegrationRules.IsValidToolName).ToArray();
            if (names.Length == 0)
            {
                throw new InstallException(InstallFailure.DidNotStart, $"{candidate.AppName} started, but it offers no tools, so I removed it again.");
            }

            if (candidate.Capability is { } wanted && CapabilityMatcher.Match(wanted, [.. names.Select(name => new ToolFacts(name))]).Count == 0)
            {
                throw new InstallException(
                    InstallFailure.LacksCapability, $"{candidate.AppName} started, but it has no tool that can {IntegrationReplyWriter.Wanted(wanted)}, so I removed it again.");
            }

            return (names, client.Server);
        }
    }

    // After an update: the tools the old version offered that the new one does not, as a sentence, or null.
    private static string? RemovedTools(InstalledIntegration before, string[] now)
    {
        if (before.Capabilities.ToolNames.Count == 0 || now.Length == 0)
        {
            return null;
        }

        var gone = before.Capabilities.ToolNames.Except(now, StringComparer.Ordinal).Take(5).ToList();
        return gone.Count == 0 ? null : "The new version no longer offers " + string.Join(", ", gone.Select(name => $"`{name}`")) + ".";
    }

    private static string DoneMessage(InstallCandidate candidate, bool updated, string? removedTools)
    {
        var text = updated
            ? $"Updated {candidate.AppName} to version {candidate.Source.Version}."
            : $"Installed {candidate.AppName}. It is listed in Settings > Integrations.";
        if (!updated && candidate.RequiredSecrets.Count > 0)
        {
            text += candidate.RequiredSecrets.Count == 1
                ? " It asks for a key: give it in Settings, under Integrations, and it is ready to use."
                : $" It asks for {candidate.RequiredSecrets.Count.ToString(CultureInfo.InvariantCulture)} keys: give them in Settings, under Integrations, and it is ready to use.";
        }

        return removedTools is null ? text : text + " " + removedTools;
    }

    // The old version's folder goes once nothing is running from it: what is connected to the integration is let go of first.
    private async Task RemoveOldVersionAsync(InstalledIntegration before, InstallCandidate candidate, CancellationToken cancellationToken)
    {
        if (before.InstalledVersion is not { } version || !IntegrationRules.IsValidVersion(version) || version == candidate.Source.Version)
        {
            return;
        }

        if (_connections is not null)
        {
            await _connections.ForgetAsync(before.Id, cancellationToken).ConfigureAwait(false);
        }

        var old = _layout.VersionDirectory(before.Id, version);
        if (_layout.IsInsideIntegrations(old))
        {
            InstallFiles.DeleteDirectory(old);
        }
    }

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the clean-up.
        }
    }

    // What an earlier installation left, if it is whole and is this very candidate: it is reused and nothing is downloaded.
    private PreparedInstall? ReadComplete(string versionDirectory, InstallCandidate candidate)
    {
        var path = Path.Combine(versionDirectory, RecordName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(path), IntegrationJson.Options);
            if (record is not { SchemaVersion: 1 } || record.IntegrationId != candidate.Id || record.Fingerprint != candidate.Fingerprint
                || record.Transport is not { Kind: McpTransportKind.Stdio, Command: not null }
                || McpLaunchRules.Problem(record.Transport.Command, record.Transport.Arguments, record.Transport.WorkingDirectory, record.Transport.Environment) is not null
                || !_layout.IsInsideIntegrations(record.Transport.Command) && !_layout.IsInsideRuntimes(record.Transport.Command)
                || record.RequiredFiles.Any(file => !File.Exists(file) || !_layout.IsInsideIntegrations(file) && !_layout.IsInsideRuntimes(file)))
            {
                return null;
            }

            ManagedRuntime? runtime = null;
            if (record.Runtime is { } kind)
            {
                runtime = _runtimes.Find(kind);
                if (runtime is null || runtime.Version != record.RuntimeVersion)
                {
                    return null;
                }
            }

            return new PreparedInstall(record.Transport, record.RequiredFiles, runtime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteRecord(string versionDirectory, InstallCandidate candidate, PreparedInstall prepared, DateTimeOffset now)
    {
        var record = new InstallRecord(
            1,
            candidate.Id,
            candidate.Fingerprint,
            candidate.Source.Kind,
            candidate.Source.Identifier,
            candidate.Source.Version,
            now,
            prepared.Runtime?.Kind,
            prepared.Runtime?.Version,
            prepared.Transport,
            prepared.RequiredFiles);

        // Written last, so that a folder with this file is a whole installation.
        File.WriteAllText(Path.Combine(versionDirectory, RecordName), JsonSerializer.Serialize(record, IntegrationJson.Options));
    }

    private InstallOutcome Finish(InstallCandidate candidate, InstallOutcome outcome)
    {
        LogFinished(_logger, candidate.Id, outcome.Status, outcome.Failure, candidate.Source.Kind);
        return outcome;
    }

    [LoggerMessage(EventId = 3190, Level = LogLevel.Information, Message = "Integration {IntegrationId} installation ended: {Status}, {Failure}, {SourceKind}")]
    private static partial void LogFinished(ILogger logger, string integrationId, InstallStatus status, InstallFailure failure, InstallSourceKind sourceKind);

    // What is written beside an installed version: enough to tell that it is whole, and what it is, and to start it again without downloading.
    private sealed record InstallRecord(
        int SchemaVersion,
        string IntegrationId,
        string Fingerprint,
        InstallSourceKind Kind,
        string Package,
        string? Version,
        DateTimeOffset InstalledAt,
        RuntimeKind? Runtime,
        string? RuntimeVersion,
        IntegrationTransport Transport,
        IReadOnlyList<string> RequiredFiles);
}
