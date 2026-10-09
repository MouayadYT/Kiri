using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Assets;

/// <summary>
/// The packaged assets of an installed copy (<see cref="IPackagedAssets"/>, PROJECT_SPEC §3.5, step 123): reads each kind's manifest from the
/// assets folder beside the program files, tells where each asset stands and checks it with <see cref="AssetGroupChecker"/>. It never runs, loads or
/// changes a file, logs only the asset's identifier, its state and counts (never a path), and holds no contents.
/// </summary>
public sealed partial class PackagedAssets : IPackagedAssets, IDisposable
{
    private readonly PackagedAssetPaths _paths;
    private readonly IAssetCheckCache _cache;
    private readonly IAppEventBus? _events;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<(AssetKind, string), Task<AssetGroupState>> _running = [];

    // What the last full check found for each asset, and how its files looked then: a peek that finds them the same says what the check found, which a
    // peek cannot learn by itself (it reads no contents), so that a result the page showed does not turn back into "not checked yet".
    private readonly Dictionary<(AssetKind, string), (AssetGroupState State, string Fingerprint)> _results = [];
    private readonly ManifestSlot[] _slots = [new(), new()];

    /// <summary>Creates the service over the assets at <paramref name="paths"/>.</summary>
    /// <param name="paths">Where the packaged assets are.</param>
    /// <param name="cache">Remembers the files already checked; none when omitted.</param>
    /// <param name="events">Told when a check begins and ends; nobody is told when omitted.</param>
    /// <param name="logger">Where the outcomes are logged; nowhere when omitted.</param>
    public PackagedAssets(
        PackagedAssetPaths paths, IAssetCheckCache? cache = null, IAppEventBus? events = null, ILogger<PackagedAssets>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _cache = cache ?? NoAssetCheckCache.Instance;
        _events = events;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PackagedAssets>.Instance;
    }

    /// <inheritdoc/>
    public PackagedAssetPaths Paths => _paths;

    /// <inheritdoc/>
    public IReadOnlyList<string> GroupIds(AssetKind kind) =>
        ReadManifest(kind).Manifest?.Groups.Select(group => group.Id).ToArray() ?? [];

    /// <inheritdoc/>
    public AssetGroupState Peek(AssetKind kind, string groupId)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        lock (_gate)
        {
            if (_running.ContainsKey((kind, groupId)))
            {
                return AssetGroupState.Of(groupId, AssetGroupStatus.Checking);
            }
        }

        var (manifest, unreadable) = ReadManifest(kind);
        if (unreadable)
        {
            return new AssetGroupState(groupId, AssetGroupStatus.Damaged, [PackagedAssetPaths.ManifestFileName]);
        }

        if (manifest?.Find(groupId) is not { } group)
        {
            return AssetGroupState.Of(groupId, AssetGroupStatus.NotPackaged);
        }

        var directory = GroupDirectory(kind, groupId);
        var peeked = AssetGroupChecker.CheckAsync(directory, group, AssetCheckMode.Peek, _cache, CancellationToken.None).GetAwaiter().GetResult();
        if (peeked.Status == AssetGroupStatus.Unverified)
        {
            lock (_gate)
            {
                if (_results.TryGetValue((kind, groupId), out var known) && known.Fingerprint == AssetGroupChecker.Fingerprint(directory, group))
                {
                    return known.State;
                }
            }
        }

        return peeked;
    }

    /// <inheritdoc/>
    public Task<AssetGroupState> VerifyAsync(
        AssetKind kind, string groupId, AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        if (mode == AssetCheckMode.Peek)
        {
            return Task.FromResult(Peek(kind, groupId));
        }

        Task<AssetGroupState> work;
        lock (_gate)
        {
            if (!_running.TryGetValue((kind, groupId), out var running))
            {
                running = Task.Run(() => RunAsync(kind, groupId, mode, _shutdown.Token));
                _running[(kind, groupId)] = running;
            }

            work = running;
        }

        return work.WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task VerifyAllAsync(AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default)
    {
        foreach (var kind in new[] { AssetKind.Model, AssetKind.Voice })
        {
            foreach (var id in GroupIds(kind))
            {
                // An asset that cannot be read is a state of its own (Unreadable) and does not stop the others being checked.
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyAsync(kind, id, mode, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task EnsureModelUsableAsync(ModelFiles files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var groups = new List<string>();
        foreach (var path in new[] { files.ModelPath, files.ProjectorPath, files.ChatTemplatePath })
        {
            if (!string.IsNullOrWhiteSpace(path) && GroupOf(path) is { } id && !groups.Contains(id, StringComparer.Ordinal))
            {
                groups.Add(id);
            }
        }

        foreach (var id in groups)
        {
            var state = await VerifyAsync(AssetKind.Model, id, AssetCheckMode.Verify, cancellationToken).ConfigureAwait(false);

            // An asset the manifest does not list is not expected of it: the user's own model, placed beside the packaged ones.
            if (state.Status is not (AssetGroupStatus.Verified or AssetGroupStatus.NotPackaged))
            {
                throw new AssetIntegrityException(AssetKind.Model, state);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _shutdown.Cancel();

    // The folder inside the packaged models folder that holds a file, as the group it would belong to; null for a file outside it.
    private string? GroupOf(string path)
    {
        var root = _paths.ModelsDirectory + Path.DirectorySeparatorChar;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = full[root.Length..];
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        var id = separator < 0 ? string.Empty : relative[..separator];
        return AssetManifestReader.IsValidGroupId(id) ? id : null;
    }

    private string GroupDirectory(AssetKind kind, string groupId) => Path.Combine(_paths.DirectoryOf(kind), groupId);

    private async Task<AssetGroupState> RunAsync(AssetKind kind, string groupId, AssetCheckMode mode, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        AssetGroupState state;
        try
        {
            var (manifest, unreadable) = ReadManifest(kind);
            if (unreadable)
            {
                state = new AssetGroupState(groupId, AssetGroupStatus.Damaged, [PackagedAssetPaths.ManifestFileName]);
            }
            else if (manifest?.Find(groupId) is not { } group)
            {
                state = AssetGroupState.Of(groupId, AssetGroupStatus.NotPackaged);
            }
            else
            {
                await PublishAsync(kind, AssetGroupState.Of(groupId, AssetGroupStatus.Checking)).ConfigureAwait(false);
                var directory = GroupDirectory(kind, groupId);

                // Taken before the files are read: a file that changes while it is read no longer matches it, so the result is not taken for the new file's.
                var fingerprint = AssetGroupChecker.Fingerprint(directory, group);
                state = await AssetGroupChecker.CheckAsync(directory, group, mode, _cache, cancellationToken).ConfigureAwait(false);

                // Only what was settled: a file that could not be read is looked at again next time, not remembered as unread.
                if (state.Status is AssetGroupStatus.Verified or AssetGroupStatus.Damaged)
                {
                    lock (_gate)
                    {
                        _results[(kind, groupId)] = (state, fingerprint);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state = AssetGroupState.Of(groupId, AssetGroupStatus.Unreadable);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove((kind, groupId));
            }
        }

        LogChecked(_logger, kind, groupId, state.Status, state.ProblemFiles.Count, timer.ElapsedMilliseconds);
        await PublishAsync(kind, state).ConfigureAwait(false);
        return state;
    }

    private async Task PublishAsync(AssetKind kind, AssetGroupState state)
    {
        if (_events is null)
        {
            return;
        }

        try
        {
            await _events.PublishAsync(new AssetStateChanged(kind, state)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A listener that fails does not fail the check; the bus logs it.
        }
    }

    // The manifest of a kind, read again only when the file's size or time stamp changed. Unreadable means a file is there that cannot be trusted.
    private (AssetManifest? Manifest, bool Unreadable) ReadManifest(AssetKind kind)
    {
        var slot = _slots[(int)kind];
        var path = _paths.ManifestOf(kind);
        lock (slot)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    slot.Manifest = null;
                    slot.Unreadable = false;
                    slot.Length = -1;
                    return (null, false);
                }

                if (slot.Length == info.Length && slot.Modified == info.LastWriteTimeUtc)
                {
                    return (slot.Manifest, slot.Unreadable);
                }

                slot.Length = info.Length;
                slot.Modified = info.LastWriteTimeUtc;
                slot.Manifest = info.Length > AssetManifestReader.MaxBytes ? null : AssetManifestReader.Parse(File.ReadAllBytes(path));
                slot.Unreadable = slot.Manifest is null;
            }
            catch (Exception exception) when (exception is AssetManifestException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                slot.Manifest = null;
                slot.Unreadable = true;
                slot.Length = -1;
            }

            return (slot.Manifest, slot.Unreadable);
        }
    }

    [LoggerMessage(
        EventId = 2140,
        Level = LogLevel.Information,
        Message = "Checked the packaged {Kind} asset {ModelId}: {Status}, {ProblemFiles} files at fault, {ElapsedMs} ms")]
    private static partial void LogChecked(ILogger logger, AssetKind kind, string modelId, AssetGroupStatus status, int problemFiles, long elapsedMs);

    private sealed class ManifestSlot
    {
        public long Length = -1;
        public DateTime Modified;
        public AssetManifest? Manifest;
        public bool Unreadable;
    }
}
