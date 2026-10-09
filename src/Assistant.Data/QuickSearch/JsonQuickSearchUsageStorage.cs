using System.Text.Json;
using Assistant.Core.QuickSearch;

namespace Assistant.Data.QuickSearch;

/// <summary>
/// Keeps what the user runs from the bar (<see cref="IQuickSearchUsage"/>) between runs, in one small JSON file in the app's cache folder
/// (PROJECT_SPEC §3.5). Only salted hashes of the results' ids are written, with how many times and when: nothing in the file names an
/// application, a file or a folder, and deleting it loses nothing but the order the user's habits gave the results. A file that is
/// missing, damaged or from another version is simply no usage; a file that cannot be written is only not kept. It never throws, and
/// nothing it holds is logged.
/// </summary>
public sealed class JsonQuickSearchUsageStorage : IQuickSearchUsageStorage
{
    private const int Version = 1;

    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Creates the storage over <paramref name="path"/> (the file need not exist; its folder is made when it is first written).</summary>
    public JsonQuickSearchUsageStorage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc/>
    public QuickSearchUsageSnapshot? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                var document = JsonSerializer.Deserialize<UsageFile>(File.ReadAllBytes(_path));
                if (document is not { Version: Version, Salt.Length: > 0 } || document.Entries is null)
                {
                    return null;
                }

                var entries = new Dictionary<string, QuickSearchUsageEntry>(StringComparer.Ordinal);
                foreach (var entry in document.Entries)
                {
                    if (!string.IsNullOrEmpty(entry.Hash) && entry.Uses > 0)
                    {
                        entries[entry.Hash] = new QuickSearchUsageEntry(entry.Uses, entry.LastUsed);
                    }
                }

                return new QuickSearchUsageSnapshot(Convert.FromBase64String(document.Salt), entries);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                // A usage file that cannot be read is no usage; the next write replaces it.
                return null;
            }
        }
    }

    /// <inheritdoc/>
    public void Save(QuickSearchUsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            var temporary = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var document = new UsageFile
                {
                    Version = Version,
                    Salt = Convert.ToBase64String(snapshot.Salt),
                    Entries = [.. snapshot.Entries.Select(pair => new UsageEntry { Hash = pair.Key, Uses = pair.Value.Uses, LastUsed = pair.Value.LastUsed })],
                };
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document));

                // Written whole beside the file, then put in its place, so a file is never half written.
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Usage that cannot be kept is only forgotten.
                FileCleanup.TryDelete(temporary);
            }
        }
    }

    private sealed class UsageFile
    {
        public int Version { get; set; }

        public string Salt { get; set; } = "";

        public List<UsageEntry>? Entries { get; set; }
    }

    private sealed class UsageEntry
    {
        public string Hash { get; set; } = "";

        public int Uses { get; set; }

        public DateTimeOffset LastUsed { get; set; }
    }
}
