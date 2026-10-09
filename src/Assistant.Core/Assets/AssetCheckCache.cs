using System.Text.Json;

namespace Assistant.Core.Assets;

/// <summary>
/// Remembers which files this PC has already checked the SHA-256 of, so that a multi-gigabyte model is read in full once and then only looked at
/// (PROJECT_SPEC §3.5, step 123). An entry vouches for a file only while its path, size and last-write time are what they were and the checksum is
/// the one the manifest now asks for; it is rebuildable data, and losing it only costs a new check.
/// </summary>
public interface IAssetCheckCache
{
    /// <summary>Whether the file was checked and found to have <paramref name="sha256"/>, and has not changed since.</summary>
    bool Vouches(string fullPath, long size, DateTime lastWriteUtc, string sha256);

    /// <summary>Remembers that the file, as it is now, has <paramref name="sha256"/>.</summary>
    void Record(string fullPath, long size, DateTime lastWriteUtc, string sha256);

    /// <summary>Forgets the file.</summary>
    void Forget(string fullPath);

    /// <summary>Writes what was remembered, if it changed. Does not throw.</summary>
    void Save();
}

/// <summary>A cache that remembers nothing, so every check reads every file.</summary>
public sealed class NoAssetCheckCache : IAssetCheckCache
{
    /// <summary>The one instance.</summary>
    public static NoAssetCheckCache Instance { get; } = new();

    private NoAssetCheckCache()
    {
    }

    /// <inheritdoc/>
    public bool Vouches(string fullPath, long size, DateTime lastWriteUtc, string sha256) => false;

    /// <inheritdoc/>
    public void Record(string fullPath, long size, DateTime lastWriteUtc, string sha256)
    {
    }

    /// <inheritdoc/>
    public void Forget(string fullPath)
    {
    }

    /// <inheritdoc/>
    public void Save()
    {
    }
}

/// <summary>
/// The cache in a JSON file in the app's cache folder. A file that cannot be read or written is treated as empty and never fails a check.
/// </summary>
public sealed class JsonAssetCheckCache : IAssetCheckCache
{
    private const int Version = 1;
    private const int MaxEntries = 256;

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;
    private bool _dirty;

    /// <summary>Creates the cache in the file at <paramref name="path"/>.</summary>
    public JsonAssetCheckCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc/>
    public bool Vouches(string fullPath, long size, DateTime lastWriteUtc, string sha256)
    {
        lock (_gate)
        {
            return Load().TryGetValue(fullPath, out var entry)
                && entry.Size == size && entry.ModifiedTicks == lastWriteUtc.Ticks
                && string.Equals(entry.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc/>
    public void Record(string fullPath, long size, DateTime lastWriteUtc, string sha256)
    {
        lock (_gate)
        {
            var entries = Load();
            if (!entries.ContainsKey(fullPath) && entries.Count >= MaxEntries)
            {
                return;
            }

            entries[fullPath] = new Entry(size, lastWriteUtc.Ticks, sha256.ToLowerInvariant());
            _dirty = true;
        }
    }

    /// <inheritdoc/>
    public void Forget(string fullPath)
    {
        lock (_gate)
        {
            _dirty |= Load().Remove(fullPath);
        }
    }

    /// <inheritdoc/>
    public void Save()
    {
        lock (_gate)
        {
            if (!_dirty || _entries is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(Version, _entries)));
                File.Move(temporary, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A cache that cannot be written only means the next check reads the files again.
            }
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= 1024 * 1024
                && JsonSerializer.Deserialize<Document>(File.ReadAllText(_path)) is { Version: Version, Files: { } files })
            {
                foreach (var (path, entry) in files.Take(MaxEntries))
                {
                    _entries[path] = entry;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _entries.Clear();
        }

        return _entries;
    }

    private sealed record Document(int Version, Dictionary<string, Entry> Files);

    private sealed record Entry(long Size, long ModifiedTicks, string Sha256);
}
