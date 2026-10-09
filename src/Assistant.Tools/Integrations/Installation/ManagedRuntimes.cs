using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>A runtime the Assistant set up for integrations, ready to use.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Version">Its version.</param>
/// <param name="DisplayName">What the user is told it is called.</param>
/// <param name="Directory">Its folder.</param>
/// <param name="ExecutablePath">The full path of its program.</param>
public sealed record ManagedRuntime(RuntimeKind Kind, string Version, string DisplayName, string Directory, string ExecutablePath);

/// <summary>The runtimes the Assistant sets up for integrations (PROJECT_SPEC §4.8, step 108).</summary>
public interface IManagedRuntimes
{
    /// <summary>The release of <paramref name="kind"/> that would be set up, or <see langword="null"/> when there is none for this PC.</summary>
    RuntimeRelease? ReleaseFor(RuntimeKind kind);

    /// <summary>The runtime of <paramref name="kind"/> if it is set up already and intact, otherwise <see langword="null"/>. Nothing is downloaded.</summary>
    ManagedRuntime? Find(RuntimeKind kind);

    /// <summary>
    /// The runtime of <paramref name="kind"/>, set up if it is not: the one that is there is reused; otherwise the release the Assistant pins is downloaded,
    /// checked against the hash it must have, and unpacked into the Assistant's own folder, with no administrator rights. The user's own Node.js or
    /// Python is never used or changed. Only called as part of an installation the user approved.
    /// </summary>
    /// <exception cref="InstallException">There is no runtime of that kind for this PC, or it could not be downloaded, checked or unpacked.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ManagedRuntime> EnsureAsync(RuntimeKind kind, IProgress<InstallProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Deletes every runtime that is not in <paramref name="inUse"/> (kind and version), and returns how many it deleted.</summary>
    int RemoveUnused(IReadOnlySet<(RuntimeKind Kind, string Version)> inUse);
}

/// <summary>
/// Sets up Node.js and Python for the integrations that need them (PROJECT_SPEC §4.8, step 108), so that a nontechnical user is never asked to install a
/// runtime or open a terminal. A runtime goes into <c>Runtimes\&lt;kind&gt;\&lt;version&gt;</c> under the Assistant's folder, unpacked beside it and moved into place
/// only when whole, with a marker file written last that says which release and hash it is; an interrupted setup leaves nothing that looks complete.
/// What is there is reused for every integration and every version that fits. What is downloaded is fixed in <see cref="RuntimeCatalog"/> with the hash
/// its maker published, so nothing else can be substituted. Logs say the kind and version only.
/// </summary>
public sealed partial class ManagedRuntimes : IManagedRuntimes
{
    private const string MarkerName = "runtime.json";

    private readonly IntegrationLayout _layout;
    private readonly IPackageDownloader _downloader;
    private readonly TimeProvider _clock;
    private readonly Func<RuntimeKind, RuntimeRelease?> _catalog;
    private readonly ILogger<ManagedRuntimes> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the manager.</summary>
    /// <param name="layout">Where runtimes go.</param>
    /// <param name="downloader">What downloads and checks the files.</param>
    /// <param name="clock">The time, written in the marker.</param>
    /// <param name="logger">Where outcomes are logged.</param>
    /// <param name="catalog">Which release is set up for a kind; the Assistant's own <see cref="RuntimeCatalog"/> when omitted.</param>
    public ManagedRuntimes(
        IntegrationLayout layout, IPackageDownloader downloader, TimeProvider clock, ILogger<ManagedRuntimes> logger, Func<RuntimeKind, RuntimeRelease?>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _layout = layout;
        _downloader = downloader;
        _clock = clock;
        _logger = logger;
        _catalog = catalog ?? RuntimeCatalog.For;
    }

    /// <inheritdoc/>
    public RuntimeRelease? ReleaseFor(RuntimeKind kind) => _catalog(kind);

    /// <inheritdoc/>
    public ManagedRuntime? Find(RuntimeKind kind) => _catalog(kind) is { } release ? Intact(release) : null;

    /// <inheritdoc/>
    public async Task<ManagedRuntime> EnsureAsync(RuntimeKind kind, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var release = _catalog(kind) ?? throw new InstallException(InstallFailure.RuntimeUnavailable, "I cannot set up what it needs on this kind of PC.");
        if (Intact(release) is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Intact(release) is { } already)
            {
                return already;
            }

            return await SetUpAsync(release, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public int RemoveUnused(IReadOnlySet<(RuntimeKind Kind, string Version)> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        var removed = 0;
        foreach (var kind in new[] { RuntimeKind.NodeJs, RuntimeKind.Python })
        {
            var folder = Path.GetDirectoryName(_layout.RuntimeDirectory(kind, "0"))!;
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(folder))
            {
                var version = Path.GetFileName(directory);
                if (!inUse.Contains((kind, version)) && _layout.IsInsideRuntimes(directory) && InstallFiles.DeleteDirectory(directory))
                {
                    removed++;
                    LogRemoved(_logger, kind, version);
                }
            }
        }

        return removed;
    }

    private async Task<ManagedRuntime> SetUpAsync(RuntimeRelease release, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var final = _layout.RuntimeDirectory(release.Kind, release.Version);
        var work = Path.Combine(_layout.RuntimesRoot, ".staging", Guid.NewGuid().ToString("N"));
        var unpacked = Path.Combine(work, "unpacked");
        var file = Path.Combine(work, "download" + (release.Archive == ArchiveKind.Zip ? ".zip" : ".tar.gz"));
        try
        {
            Directory.CreateDirectory(work);
            var lastWhole = -1;
            var bytes = new Progress<long>(received =>
            {
                var whole = (int)(received / 1_048_576);
                if (whole == lastWhole)
                {
                    return;
                }

                lastWhole = whole;
                var total = Math.Max(1, release.ApproximateMegabytes);
                progress?.Report(new InstallProgress(
                    InstallStep.DownloadingRuntime,
                    $"Downloading {release.DisplayName} ({Math.Min(whole, total).ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} MB)",
                    Math.Clamp((double)received / Math.Max(1, release.SizeBytes), 0, 1)));
            });
            progress?.Report(new InstallProgress(InstallStep.DownloadingRuntime, $"Downloading {release.DisplayName}", 0));
            await _downloader.DownloadAsync(release.DownloadUrl, file, release.Hash, release.SizeBytes + 1_048_576, bytes, cancellationToken).ConfigureAwait(false);

            progress?.Report(new InstallProgress(InstallStep.UnpackingRuntime, $"Setting up {release.DisplayName}"));
            await Task.Run(() => ArchiveExtractor.Extract(file, release.Archive, unpacked, release.TopFolder, ExtractionLimits.Default, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (!File.Exists(Path.Combine(unpacked, release.Executable)))
            {
                throw new InstallException(InstallFailure.RuntimeUnavailable, "What I downloaded is not the runtime I expected.");
            }

            // The marker is written last: a folder without it is not a runtime that is set up.
            var marker = new RuntimeMarker(1, release.Kind.ToString(), release.Version, release.Hash.ToString(), _clock.GetUtcNow());
            await File.WriteAllTextAsync(Path.Combine(unpacked, MarkerName), JsonSerializer.Serialize(marker), cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (Directory.Exists(final))
            {
                // What is there is not intact: it is replaced.
                InstallFiles.DeleteDirectory(final);
            }

            Directory.Move(unpacked, final);
            LogSetUp(_logger, release.Kind, release.Version);
            return Intact(release) ?? throw new InstallException(InstallFailure.RuntimeUnavailable, "I could not set up what it needs.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallFailure.DiskFailed, "I could not set up what it needs. Check that the disk has room.", exception);
        }
        finally
        {
            InstallFiles.DeleteDirectory(work);
        }
    }

    // The runtime of the release if its folder holds a marker for exactly this release and its program.
    private ManagedRuntime? Intact(RuntimeRelease release)
    {
        var directory = _layout.RuntimeDirectory(release.Kind, release.Version);
        var markerPath = Path.Combine(directory, MarkerName);
        var executable = Path.Combine(directory, release.Executable);
        try
        {
            if (!File.Exists(markerPath) || !File.Exists(executable))
            {
                return null;
            }

            var marker = JsonSerializer.Deserialize<RuntimeMarker>(File.ReadAllText(markerPath));
            return marker is { SchemaVersion: 1 } && marker.Kind == release.Kind.ToString() && marker.Version == release.Version
                && marker.Hash == release.Hash.ToString()
                ? new ManagedRuntime(release.Kind, release.Version, release.DisplayName, directory, executable)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 3180, Level = LogLevel.Information, Message = "Runtime {Kind} {Version} set up")]
    private static partial void LogSetUp(ILogger logger, RuntimeKind kind, string version);

    [LoggerMessage(EventId = 3181, Level = LogLevel.Information, Message = "Runtime {Kind} {Version} removed")]
    private static partial void LogRemoved(ILogger logger, RuntimeKind kind, string version);

    private sealed record RuntimeMarker(int SchemaVersion, string Kind, string Version, string Hash, DateTimeOffset InstalledAt);
}
