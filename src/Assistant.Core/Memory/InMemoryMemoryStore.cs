namespace Assistant.Core.Memory;

/// <summary>What is remembered, kept in memory alone: for a host with no folder of its own, and for tests. It follows the same rules as the file.</summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly object _gate = new();
    private List<MemoryEntry> _entries = [];

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IReadOnlyList<MemoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries;
            }
        }
    }

    /// <inheritdoc/>
    public Task<MemoryEntry?> SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var kept = entry with { Text = MemoryRules.Clean(entry.Text), Key = entry.Key.Trim(), Value = entry.Value.Trim() };
        if (kept.Text.Length == 0 || (kept.Kind != MemoryKind.Note && (kept.Key.Length == 0 || kept.Value.Length == 0)))
        {
            return Task.FromResult<MemoryEntry?>(null);
        }

        lock (_gate)
        {
            var entries = _entries.ToList();
            var at = entries.FindIndex(known => known.Id == kept.Id);
            if (at < 0 && kept.Kind != MemoryKind.Note)
            {
                at = entries.FindIndex(known => known.Kind == kept.Kind && string.Equals(known.Key, kept.Key, StringComparison.Ordinal));
            }

            var folded = MemoryRules.Fold(kept.Text);
            if (kept.Kind == MemoryKind.Note && entries.Find(known => known.Kind == MemoryKind.Note && known.Id != kept.Id && MemoryRules.Fold(known.Text) == folded) is { } same)
            {
                if (at < 0)
                {
                    return Task.FromResult<MemoryEntry?>(same);
                }

                entries.Remove(same);
                at = entries.FindIndex(known => known.Id == kept.Id);
            }

            if (at >= 0)
            {
                kept = kept with { Id = entries[at].Id, CreatedAt = entries[at].CreatedAt };
                entries[at] = kept;
            }
            else
            {
                entries.Add(kept);
                if (entries.Count > MemoryRules.MaxEntries)
                {
                    entries.RemoveAt(0);
                }
            }

            _entries = entries;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult<MemoryEntry?>(kept);
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        bool removed;
        lock (_gate)
        {
            var entries = _entries.ToList();
            removed = entries.RemoveAll(known => known.Id == id) > 0;
            _entries = entries;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(removed);
    }
}
