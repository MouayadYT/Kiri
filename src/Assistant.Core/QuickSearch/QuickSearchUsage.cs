using System.Security.Cryptography;
using System.Text;

namespace Assistant.Core.QuickSearch;

/// <summary>How often and how lately a result was run from the bar.</summary>
/// <param name="Uses">How many times, at most <see cref="QuickSearchUsage.MaxUses"/>.</param>
/// <param name="LastUsed">When it was last run.</param>
public sealed record QuickSearchUsageEntry(int Uses, DateTimeOffset LastUsed);

/// <summary>
/// What the user has run from the bar, so that what they use comes first (PROJECT_SPEC §4.1). It is only ever asked how much one
/// result was used, and told when one is run.
/// </summary>
public interface IQuickSearchUsage
{
    /// <summary>What is known of <paramref name="resultId"/> (<see cref="QuickSearchResult.Id"/>), or <see langword="null"/> when it was never run.</summary>
    QuickSearchUsageEntry? Find(string resultId);

    /// <summary>Notes that <paramref name="resultId"/> was run just now.</summary>
    void RecordUse(string resultId);

    /// <summary>Forgets everything.</summary>
    void Clear();
}

/// <summary>
/// What is kept of the usage between runs: only a salted hash of each result's id, how often and when. Nothing here names an
/// application, a file or a folder, so the file that holds it can be read by no one for what the user opened (PROJECT_SPEC §3.2).
/// </summary>
/// <param name="Salt">Random bytes, made once, that the hashes are taken with.</param>
/// <param name="Entries">Each result's hash (hex) and what is known of it.</param>
public sealed record QuickSearchUsageSnapshot(byte[] Salt, IReadOnlyDictionary<string, QuickSearchUsageEntry> Entries);

/// <summary>Where the usage is kept between runs.</summary>
public interface IQuickSearchUsageStorage
{
    /// <summary>Reads what was kept, or <see langword="null"/> when nothing was, or it cannot be read. It never throws.</summary>
    QuickSearchUsageSnapshot? Load();

    /// <summary>Keeps <paramref name="snapshot"/>. It never throws: usage that cannot be kept is only forgotten.</summary>
    void Save(QuickSearchUsageSnapshot snapshot);
}

/// <summary>
/// The app's <see cref="IQuickSearchUsage"/>: counts and times in memory, bounded to the most recently used
/// <see cref="Capacity"/> results, and kept between runs through an <see cref="IQuickSearchUsageStorage"/> as salted hashes.
/// </summary>
public sealed class QuickSearchUsage : IQuickSearchUsage
{
    /// <summary>The most results remembered; the one used longest ago is forgotten first.</summary>
    public const int DefaultCapacity = 500;

    /// <summary>The most uses counted for one result: past it, using it again changes nothing.</summary>
    public const int MaxUses = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<string, QuickSearchUsageEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly IQuickSearchUsageStorage? _storage;
    private byte[] _salt;

    /// <summary>Creates the usage, reading what <paramref name="storage"/> kept.</summary>
    /// <param name="clock">The clock uses are stamped with; the system's by default.</param>
    /// <param name="storage">Where it is kept between runs; without one it is only in memory.</param>
    /// <param name="capacity">How many results are remembered.</param>
    public QuickSearchUsage(TimeProvider? clock = null, IQuickSearchUsageStorage? storage = null, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _clock = clock ?? TimeProvider.System;
        _storage = storage;
        Capacity = capacity;
        _salt = RandomNumberGenerator.GetBytes(16);

        if (storage?.Load() is { Salt.Length: > 0 } snapshot)
        {
            _salt = snapshot.Salt;
            foreach (var (hash, entry) in snapshot.Entries)
            {
                if (hash.Length > 0 && entry.Uses > 0)
                {
                    _entries[hash] = entry with { Uses = Math.Min(entry.Uses, MaxUses) };
                }
            }

            Prune();
        }
    }

    /// <summary>How many results are remembered, at most.</summary>
    public int Capacity { get; }

    /// <summary>How many results are remembered now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc/>
    public QuickSearchUsageEntry? Find(string resultId)
    {
        ArgumentNullException.ThrowIfNull(resultId);
        lock (_gate)
        {
            return _entries.TryGetValue(Hash(resultId), out var entry) ? entry : null;
        }
    }

    /// <inheritdoc/>
    public void RecordUse(string resultId)
    {
        ArgumentException.ThrowIfNullOrEmpty(resultId);
        QuickSearchUsageSnapshot snapshot;
        lock (_gate)
        {
            var key = Hash(resultId);
            var now = _clock.GetUtcNow();
            _entries[key] = _entries.TryGetValue(key, out var known)
                ? new QuickSearchUsageEntry(Math.Min(known.Uses + 1, MaxUses), now)
                : new QuickSearchUsageEntry(1, now);
            Prune();
            snapshot = Snapshot();
        }

        _storage?.Save(snapshot);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        QuickSearchUsageSnapshot snapshot;
        lock (_gate)
        {
            _entries.Clear();
            snapshot = Snapshot();
        }

        _storage?.Save(snapshot);
    }

    private QuickSearchUsageSnapshot Snapshot() =>
        new(_salt, new Dictionary<string, QuickSearchUsageEntry>(_entries, StringComparer.Ordinal));

    // The one used longest ago goes first, so what the user still uses is what is kept.
    private void Prune()
    {
        while (_entries.Count > Capacity)
        {
            var oldest = _entries.MinBy(pair => pair.Value.LastUsed).Key;
            _entries.Remove(oldest);
        }
    }

    // A salted SHA-256 of the id, 16 bytes of it, in hex.
    private string Hash(string resultId)
    {
        var bytes = Encoding.UTF8.GetBytes(resultId);
        var input = new byte[_salt.Length + bytes.Length];
        _salt.CopyTo(input, 0);
        bytes.CopyTo(input, _salt.Length);
        return Convert.ToHexString(SHA256.HashData(input).AsSpan(0, 16));
    }
}
