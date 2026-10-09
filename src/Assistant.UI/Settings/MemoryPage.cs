using System.Collections.ObjectModel;
using System.Windows.Input;
using Assistant.Core.Memory;
using Assistant.Core.Settings;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// One thing the Assistant remembers, in Settings > Memory, with Edit and Forget beside it. A note (something the user said about themselves) is
/// rewritten as it is typed. What stands for something is rewritten by what it stands for: where the Clock window opens by its two percentages, what
/// "the AC" is by the name of another device of the user's home, and where someone's messages go by the name of a service. What was typed and
/// cannot be read as that is said, and nothing is changed. Nothing here logs, and nothing leaves this PC.
/// </summary>
public sealed class MemoryItem : NotifyingObject
{
    private readonly MemoryPage _page;
    private readonly RelayCommand _save;
    private readonly RelayCommand _cancel;
    private MemoryEntry _saved;
    private string _text;
    private bool _isEditing;

    internal MemoryItem(MemoryPage page, MemoryEntry entry)
    {
        _page = page;
        _saved = entry;
        _text = entry.Text;
        EditCommand = new RelayCommand(_ => IsEditing = true, _ => CanEdit && !IsEditing);
        _save = new RelayCommand(_ => _ = _page.SaveAsync(this), _ => IsEditing && MemoryRules.Clean(_text).Length > 0);
        _cancel = new RelayCommand(_ => Cancel(), _ => IsEditing);
        ForgetCommand = new RelayCommand(_ => _ = _page.ForgetAsync(this));
    }

    /// <summary>The entry's id.</summary>
    public Guid Id => _saved.Id;

    /// <summary>The entry as it is kept.</summary>
    internal MemoryEntry Saved => _saved;

    /// <summary>What it says, as it is kept or as it is being rewritten.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? string.Empty))
            {
                _save.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What kind of thing it is, in a word or two: "Note", "Home", "Messages", "Preference".</summary>
    public string KindLabel => _saved.Kind switch
    {
        MemoryKind.HomeDevice => "Home",
        MemoryKind.MessageRoute or MemoryKind.MessageChat => "Messages",
        MemoryKind.Preference => "Preference",
        _ => "Note",
    };

    /// <summary>
    /// Whether it can be rewritten: everything the user told the Assistant or chose can, each by what it is. Which chat is a person's on a service is
    /// learned when a message is sent there and can only be forgotten (it is then looked for again).
    /// </summary>
    public bool CanEdit => _saved.Kind != MemoryKind.MessageChat;

    /// <summary>What to type, under the field while it is rewritten; empty for a note.</summary>
    public string EditHint => _saved.Kind switch
    {
        MemoryKind.Preference when _saved.Key == Assistant.Core.Clock.ClockPlace.Key =>
            "Change the two percentages: how far across the display, and how far down. 0% is the left or top edge, 100% the right or bottom.",
        MemoryKind.HomeDevice => "Change the device's name to another device of your Home Assistant, as it is called there.",
        MemoryKind.MessageRoute => "Change the service their messages go through, such as iMessage, WhatsApp or Beeper.",
        _ => string.Empty,
    };

    /// <summary>Whether there is an <see cref="EditHint"/>.</summary>
    public bool HasEditHint => EditHint.Length > 0;

    /// <summary>Whether it is being rewritten.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (Set(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(IsShowing));
                _save.RaiseCanExecuteChanged();
                _cancel.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether it is shown as a line, not as a field.</summary>
    public bool IsShowing => !_isEditing;

    /// <summary>Opens a note to be rewritten.</summary>
    public ICommand EditCommand { get; }

    /// <summary>Keeps what was typed.</summary>
    public ICommand SaveCommand => _save;

    /// <summary>Puts the note back as it was kept.</summary>
    public ICommand CancelCommand => _cancel;

    /// <summary>Forgets it.</summary>
    public ICommand ForgetCommand { get; }

    // Shows what is kept now, unless the user is in the middle of rewriting it.
    internal void Show(MemoryEntry entry)
    {
        _saved = entry;
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(EditHint));
        OnPropertyChanged(nameof(HasEditHint));
        if (!_isEditing)
        {
            Text = entry.Text;
        }
    }

    internal void Kept(MemoryEntry entry)
    {
        _saved = entry;
        Text = entry.Text;
        IsEditing = false;
    }

    private void Cancel()
    {
        Text = _saved.Text;
        IsEditing = false;
    }
}

/// <summary>
/// Memory: what the Assistant remembers for the user between conversations. The notes it took when the user told it something about themselves or
/// asked it to remember something, what the user calls the devices in their home, which chat a person's messages go to, and where the Clock window
/// opens. Everything is listed, a note can be rewritten or added by hand, and anything can be forgotten. It is kept in one file on this PC and is not
/// part of <see cref="AppSettings"/>: the page reads it when the window opens and follows it while the window is open, so that what is remembered in
/// a conversation shows at once.
/// </summary>
public sealed class MemoryPage : SettingsPage, IDisposable
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly IMemoryStore? _store;
    private readonly Assistant.Core.Home.IHomeAssistant? _home;
    private readonly TimeProvider _clock;
    private readonly RelayCommand _add;
    private string _newText = string.Empty;
    private string _notice = string.Empty;
    private bool _disposed;

    internal MemoryPage(SettingsViewModel root, IMemoryStore? store = null, TimeProvider? clock = null, Assistant.Core.Home.IHomeAssistant? home = null)
        : base(root, SettingsSection.Memory)
    {
        _store = store;
        _home = home;
        _clock = clock ?? TimeProvider.System;
        _add = new RelayCommand(_ => _ = AddAsync(), _ => _store is not null && MemoryRules.Clean(_newText).Length > 0);
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasNoItems));
        };
        if (_store is not null)
        {
            _store.Changed += OnStoreChanged;
        }
    }

    /// <summary>What is remembered, the newest first.</summary>
    public ObservableCollection<MemoryItem> Items { get; } = [];

    /// <summary>Whether the page can remember anything at all.</summary>
    public bool HasStore => _store is not null;

    /// <summary>Whether anything is remembered.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Whether nothing is, so the page says what memory is for.</summary>
    public bool HasNoItems => Items.Count == 0;

    /// <summary>What the page says memory is.</summary>

    /// <summary>What the page says when nothing is remembered.</summary>
    public string EmptyText => "Nothing yet.";

    /// <summary>A note the user is typing to add.</summary>
    public string NewText
    {
        get => _newText;
        set
        {
            if (Set(ref _newText, value ?? string.Empty))
            {
                _add.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Adds the note in <see cref="NewText"/>.</summary>
    public ICommand AddCommand => _add;

    /// <summary>What the page says when something could not be kept, or empty.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    /// <summary>Whether there is a <see cref="Notice"/>.</summary>
    public bool HasNotice => _notice.Length > 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_store is not null)
            {
                _store.Changed -= OnStoreChanged;
            }
        }
    }

    /// <summary>Lists what is remembered, as it is now. It never throws.</summary>
    public void Load() => Sync();

    // Memory is not a setting: nothing here depends on the settings.
    internal override void Apply(AppSettings settings, bool fresh)
    {
    }

    internal async Task SaveAsync(MemoryItem item)
    {
        if (_store is null)
        {
            return;
        }

        // What stands for something is rewritten by what it stands for; what was typed and cannot be read as that changes nothing, and is said.
        var (rewritten, problem) = await RewriteAsync(item.Saved, item.Text).ConfigureAwait(true);
        if (rewritten is null)
        {
            Notice = problem;
            return;
        }

        var kept = await _store.SaveAsync(rewritten).ConfigureAwait(true);
        if (kept is null)
        {
            Notice = "That couldn't be saved. Check that the disk has room, then try again.";
            return;
        }

        Notice = string.Empty;
        item.Kept(kept);
        Sync();
    }

    // The entry as it is kept once the user rewrote it as typed, or why it cannot be.
    private async Task<(MemoryEntry? Entry, string Problem)> RewriteAsync(MemoryEntry saved, string typed)
    {
        switch (saved.Kind)
        {
            case MemoryKind.Preference when saved.Key == Assistant.Core.Clock.ClockPlace.Key:
                return Assistant.Core.Clock.ClockPlace.Rewritten(saved, typed) is { } place
                    ? (place, string.Empty)
                    : (null, "Type where the Clock window opens as two percentages from 0 to 100, such as: 94% across and 36% down.");

            case MemoryKind.HomeDevice:
            {
                // "When you say "ac" at home, you mean AC IR Bridge.": the device named last is the one meant, among the user's own.
                var said = QuotedIn(saved.Text);
                var name = After(typed, "you mean") ?? typed;
                if (_home is null)
                {
                    return (null, "Home Assistant is not connected, so the device cannot be changed here. Forget this, and you will be asked again.");
                }

                try
                {
                    await _home.LoadAsync().ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.IO.IOException or System.Net.Http.HttpRequestException)
                {
                    // What is known of the devices already is looked in.
                }

                var wanted = MemoryRules.Fold(name);
                var named = _home.Known.Where(device => MemoryRules.Fold(device.Name) == wanted).ToList();
                if (named.Count != 1)
                {
                    named = wanted.Length >= 3 ? [.. _home.Known.Where(device => MemoryRules.Fold(device.Name).Contains(wanted, StringComparison.Ordinal))] : [];
                }

                return named.Count == 1 && said.Length > 0
                    ? (saved with { Text = $"When you say \"{said}\" at home, you mean {named[0].Name}.", Value = named[0].Id }, string.Empty)
                    : (null, "Type the name of one device of your Home Assistant, as it is called there, after “you mean”.");
            }

            case MemoryKind.MessageRoute:
            {
                // "Messages to Sami go through iMessage in Beeper.": the service named is the one; which chat that is, is found each time.
                var service = Assistant.Core.Messaging.MessagingServices.Find(After(typed, "go through") ?? typed);
                var person = Between(saved.Text, "Messages to ", " go through");
                return service.Length > 0 && person.Length > 0
                    ? (saved with
                    {
                        Text = $"Messages to {person} go through {service}.",
                        Value = Assistant.Tools.Messaging.MessageRoutes.ForService(service),
                    }, string.Empty)
                    : (null, "Type the service their messages should go through, such as iMessage, WhatsApp or Beeper.");
            }

            case MemoryKind.MessageChat:
                return (null, "This one is learned when a message is sent and cannot be typed. Forget it, and the chat is looked for again.");

            case MemoryKind.Preference:
                return (null, "This one is chosen in a conversation and cannot be typed. Forget it, and you will be asked again.");

            default:
                return (saved with { Text = typed }, string.Empty);
        }
    }

    private static string QuotedIn(string text)
    {
        var open = text.IndexOf('"', StringComparison.Ordinal);
        var close = open < 0 ? -1 : text.IndexOf('"', open + 1);
        return close > open ? text[(open + 1)..close] : string.Empty;
    }

    private static string? After(string text, string words)
    {
        var at = text.LastIndexOf(words, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : text[(at + words.Length)..].Trim().TrimEnd('.').Trim();
    }

    private static string Between(string text, string before, string after)
    {
        var start = text.IndexOf(before, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf(after, start + before.Length, StringComparison.Ordinal);
        return end > start ? text[(start + before.Length)..end].Trim() : string.Empty;
    }

    internal async Task ForgetAsync(MemoryItem item)
    {
        if (_store is null)
        {
            return;
        }

        await _store.DeleteAsync(item.Id).ConfigureAwait(true);
        Notice = string.Empty;
        Sync();
    }

    private async Task AddAsync()
    {
        if (_store is null)
        {
            return;
        }

        var kept = await _store.SaveAsync(MemoryEntry.Note(_newText, _clock.GetUtcNow())).ConfigureAwait(true);
        if (kept is null)
        {
            Notice = "That couldn't be saved. Check that the disk has room, then try again.";
            return;
        }

        Notice = string.Empty;
        NewText = string.Empty;
        Sync();
    }

    // Something was remembered or forgotten, here or in a conversation: the list is brought up to date, on the window's own thread.
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            Sync();
        }
        else
        {
            _ui.Post(_ => Sync(), null);
        }
    }

    // Brings the list up to date without touching a note the user is rewriting.
    private void Sync()
    {
        if (_store is null || _disposed)
        {
            return;
        }

        var entries = _store.Entries.Reverse().ToList();
        foreach (var gone in Items.Where(item => entries.All(entry => entry.Id != item.Id)).ToList())
        {
            Items.Remove(gone);
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (Items.FirstOrDefault(item => item.Id == entry.Id) is { } known)
            {
                known.Show(entry);
                var at = Items.IndexOf(known);
                if (at != index && index < Items.Count)
                {
                    Items.Move(at, index);
                }

                continue;
            }

            Items.Insert(Math.Min(index, Items.Count), new MemoryItem(this, entry));
        }
    }
}
