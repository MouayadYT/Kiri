using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assistant.Core.Memory;

/// <summary>
/// Keeps what the Assistant remembers in one JSON file (<c>memory.json</c> in the app's folder). It is read once, when first asked, and
/// tolerantly: an entry that cannot be read is left out, and a file that is not JSON is nothing remembered. Reading never writes. A change
/// is written all or nothing (a temporary file, then a replace), and a file that could not be read is copied to <c>memory.unreadable.json</c>
/// before it is first written over, so nothing the user had is lost.
/// </summary>
public sealed class JsonMemoryStore : IMemoryStore, IDisposable
{
    /// <summary>The name of the file, which lives directly inside the app's folder.</summary>
    public const string FileName = "memory.json";

    // A file bigger than this is not one the Assistant wrote.
    private const long MaxFileLength = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private IReadOnlyList<MemoryEntry>? _entries;
    private bool _unreadable;

    /// <summary>Creates the store over the file at <paramref name="path"/>.</summary>
    public JsonMemoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IReadOnlyList<MemoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries ??= Read();
            }
        }
    }

    /// <inheritdoc/>
    public async Task<MemoryEntry?> SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var kept = entry with
        {
            Text = MemoryRules.Clean(entry.Text),
            Key = Cut(entry.Key),
            Value = Cut(entry.Value),
        };
        if (kept.Text.Length == 0 || (kept.Kind != MemoryKind.Note && (kept.Key.Length == 0 || kept.Value.Length == 0)))
        {
            return null;
        }

        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = Entries.ToList();
            var at = entries.FindIndex(known => known.Id == kept.Id);
            if (at < 0 && kept.Kind != MemoryKind.Note)
            {
                at = entries.FindIndex(known => known.Kind == kept.Kind && string.Equals(known.Key, kept.Key, StringComparison.Ordinal));
            }

            // What is already remembered in the same words is not remembered twice.
            var folded = MemoryRules.Fold(kept.Text);
            if (kept.Kind == MemoryKind.Note && entries.Find(known => known.Kind == MemoryKind.Note && known.Id != kept.Id && MemoryRules.Fold(known.Text) == folded) is { } same)
            {
                if (at < 0)
                {
                    return same;
                }

                entries.Remove(same);
                at = entries.FindIndex(known => known.Id == kept.Id);
            }

            if (at >= 0)
            {
                // The entry keeps its id and the time it was first remembered.
                kept = kept with { Id = entries[at].Id, CreatedAt = entries[at].CreatedAt };
                if (kept == entries[at])
                {
                    return kept;
                }

                entries[at] = kept;
            }
            else
            {
                entries.Add(kept);
                while (entries.Count > MemoryRules.MaxEntries && entries.FindIndex(known => known.Kind == MemoryKind.Note && known.Id != kept.Id) is >= 0 and var oldest)
                {
                    entries.RemoveAt(oldest);
                }

                if (entries.Count > MemoryRules.MaxEntries)
                {
                    entries.RemoveAt(0);
                }
            }

            return await WriteAsync(entries, cancellationToken).ConfigureAwait(false) ? kept : null;
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = Entries.ToList();
            return entries.RemoveAll(known => known.Id == id) > 0 && await WriteAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _writing.Dispose();

    private static string Cut(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length > MemoryRules.MaxValueLength ? trimmed[..MemoryRules.MaxValueLength] : trimmed;
    }

    private List<MemoryEntry> Read()
    {
        var entries = new List<MemoryEntry>();
        try
        {
            if (!File.Exists(_path))
            {
                return entries;
            }

            if (new FileInfo(_path).Length > MaxFileLength)
            {
                _unreadable = true;
                return entries;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                _unreadable = true;
                return entries;
            }

            foreach (var item in list.EnumerateArray())
            {
                try
                {
                    if (item.Deserialize<MemoryEntry>(Options) is { } entry && entry.Id != Guid.Empty && MemoryRules.Clean(entry.Text) is { Length: > 0 } text
                        && Enum.IsDefined(entry.Kind) && entries.All(known => known.Id != entry.Id) && entries.Count < MemoryRules.MaxEntries)
                    {
                        entries.Add(entry with { Text = text, Key = Cut(entry.Key), Value = Cut(entry.Value) });
                    }
                }
                catch (JsonException)
                {
                    // An entry that cannot be read is left out; the rest are kept.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _unreadable = true;
            entries.Clear();
        }

        return entries;
    }

    private async Task<bool> WriteAsync(List<MemoryEntry> entries, CancellationToken cancellationToken)
    {
        var temporary = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (_unreadable && File.Exists(_path))
            {
                File.Copy(_path, Path.Combine(directory ?? string.Empty, Path.GetFileNameWithoutExtension(_path) + ".unreadable" + Path.GetExtension(_path)), overwrite: true);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new MemoryFile(1, entries), Options);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            _unreadable = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A temporary file that cannot be removed is overwritten by the next write.
            }

            if (exception is OperationCanceledException)
            {
                throw;
            }

            return false;
        }

        lock (_gate)
        {
            _entries = entries;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private sealed record MemoryFile(int SchemaVersion, IReadOnlyList<MemoryEntry> Entries);
}
