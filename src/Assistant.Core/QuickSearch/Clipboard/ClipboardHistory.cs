using System.Text;

namespace Assistant.Core.QuickSearch.Clipboard;

/// <summary>
/// One piece of text the user copied, as the Assistant's own clipboard history keeps it (PROJECT_SPEC §4.1, §3.2): the text is private
/// content, so it is never printed or logged.
/// </summary>
/// <param name="Id">A fresh id for this item, which is not made from its text.</param>
/// <param name="Text">What was copied.</param>
/// <param name="CopiedAt">When it was last copied.</param>
public sealed record ClipboardHistoryItem(string Id, string Text, DateTimeOffset CopiedAt)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Length = {Text.Length}");
        return true;
    }
}

/// <summary>The privacy limits of the clipboard history: how much it keeps, and for how long.</summary>
/// <param name="MaxItems">The most items kept; the oldest goes first. At most <see cref="HardMaxItems"/>.</param>
/// <param name="MaxItemCharacters">
/// The longest text kept. Text longer than this is not kept at all, rather than cut: a cut copy pastes wrongly, and a long copy is
/// rarely something to search for.
/// </param>
/// <param name="MaxAge">How long an item is kept; an older one is forgotten.</param>
public sealed record ClipboardHistoryLimits(int MaxItems, int MaxItemCharacters, TimeSpan MaxAge)
{
    /// <summary>The most items any limit allows.</summary>
    public const int HardMaxItems = 50;

    /// <summary>The limits the app uses: the last 20 items, none over 4,000 characters, none older than a day.</summary>
    public static ClipboardHistoryLimits Default { get; } = new(20, 4000, TimeSpan.FromDays(1));
}

/// <summary>
/// The short history of what the user copied that the Assistant keeps for the bar's Clipboard category (PROJECT_SPEC §4.1). It is the
/// Assistant's own: Windows' clipboard history is never read. It is kept in memory only, never written anywhere, and it is off until
/// the user allows it (Clipboard History permission, PROJECT_SPEC §4.9): while it is off nothing is kept and nothing is listed.
/// </summary>
public interface IClipboardHistory
{
    /// <summary>Raised, from any thread, when an item was added, removed or the history was cleared.</summary>
    event EventHandler? Changed;

    /// <summary>Whether the user has allowed it. While it is off, copies are ignored and there is nothing to list.</summary>
    bool IsEnabled { get; }

    /// <summary>The privacy limits it keeps to.</summary>
    ClipboardHistoryLimits Limits { get; }

    /// <summary>
    /// Turns the history on or off. Turning it off forgets everything at once, so nothing that was copied before outlives the user's
    /// choice.
    /// </summary>
    void SetEnabled(bool enabled);

    /// <summary>The items kept, newest first. Empty while it is off.</summary>
    IReadOnlyList<ClipboardHistoryItem> Items { get; }

    /// <summary>The item with <paramref name="id"/>, or <see langword="null"/>.</summary>
    ClipboardHistoryItem? Find(string id);

    /// <summary>
    /// Keeps <paramref name="text"/> as the newest item. Nothing is kept while the history is off, or for text that is empty, only
    /// whitespace or longer than the limit. Text that is already in the history moves to the front and is not kept twice.
    /// </summary>
    /// <returns>Whether it was kept.</returns>
    bool Add(string text);

    /// <summary>Takes one item out.</summary>
    /// <returns>Whether there was such an item.</returns>
    bool Remove(string id);

    /// <summary>Forgets everything.</summary>
    void Clear();
}

/// <summary>
/// The app's <see cref="IClipboardHistory"/>, in memory: bounded by <see cref="ClipboardHistoryLimits"/>, off until switched on, forgotten
/// when switched off or cleared, and gone when the app closes.
/// </summary>
public sealed class ClipboardHistory : IClipboardHistory
{
    private readonly object _gate = new();
    private readonly List<ClipboardHistoryItem> _items = [];
    private readonly TimeProvider _clock;
    private bool _enabled;

    /// <summary>Creates the history, off.</summary>
    /// <param name="limits">The privacy limits; <see cref="ClipboardHistoryLimits.Default"/> by default.</param>
    /// <param name="clock">The clock that dates what is copied and ages it; the system's by default.</param>
    public ClipboardHistory(ClipboardHistoryLimits? limits = null, TimeProvider? clock = null)
    {
        Limits = limits ?? ClipboardHistoryLimits.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(Limits.MaxItems, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Limits.MaxItems, ClipboardHistoryLimits.HardMaxItems);
        ArgumentOutOfRangeException.ThrowIfLessThan(Limits.MaxItemCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Limits.MaxAge, TimeSpan.Zero);
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public ClipboardHistoryLimits Limits { get; }

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ClipboardHistoryItem> Items
    {
        get
        {
            lock (_gate)
            {
                Expire();
                return [.. _items];
            }
        }
    }

    /// <inheritdoc/>
    public void SetEnabled(bool enabled)
    {
        var changed = false;
        lock (_gate)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (!enabled && _items.Count > 0)
            {
                _items.Clear();
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc/>
    public ClipboardHistoryItem? Find(string id)
    {
        lock (_gate)
        {
            Expire();
            return _items.FirstOrDefault(item => item.Id == id);
        }
    }

    /// <inheritdoc/>
    public bool Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > Limits.MaxItemCharacters)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_enabled)
            {
                return false;
            }

            Expire();

            // The same text again is the same item, now newest; it keeps its id so a row on screen stays that row.
            var known = _items.FindIndex(item => string.Equals(item.Text, text, StringComparison.Ordinal));
            var id = known >= 0 ? _items[known].Id : Guid.NewGuid().ToString("N");
            if (known >= 0)
            {
                _items.RemoveAt(known);
            }

            _items.Insert(0, new ClipboardHistoryItem(id, text, _clock.GetUtcNow()));
            if (_items.Count > Limits.MaxItems)
            {
                _items.RemoveRange(Limits.MaxItems, _items.Count - Limits.MaxItems);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc/>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(item => item.Id == id) == 0)
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return;
            }

            _items.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Items older than the limit are forgotten, wherever they are asked about. Called with the lock held.
    private void Expire()
    {
        var oldest = _clock.GetUtcNow() - Limits.MaxAge;
        _items.RemoveAll(item => item.CopiedAt < oldest);
    }
}
