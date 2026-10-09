using System.Collections.ObjectModel;
using System.Windows.Input;
using Assistant.Core.People;
using Assistant.Core.Settings;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// People (PROJECT_SPEC §4.9, step 112): the people the user tells the Assistant about, each with a name, other names, how they relate to the user ("Brother"), and the
/// phone numbers, addresses and usernames a messaging provider will use to reach them. They are kept on this PC in the Assistant's own database, and what "my brother" means is
/// worked out from this list and never by a connected app. The page lists them, adds, edits and removes them, and has a place to try a name ("my brother") and see who the Assistant
/// takes it to be, or what it would ask. Nothing is sent anywhere and nothing is logged. People are not settings: the list is read when the window is opened (<see cref="LoadAsync"/>),
/// and is not part of <see cref="AppSettings"/>. It also follows the store while the window is open: someone the Assistant was told about in a conversation
/// ("Who is your brother?") is in the list as soon as they are remembered, with nobody having to type them in.
/// </summary>
public sealed class PeoplePage : SettingsPage, IDisposable
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private bool _disposed;
    private readonly IPersonStore? _store;
    private readonly IPersonResolver? _resolver;
    private readonly RelayCommand _add;
    private readonly RelayCommand _check;
    private string _notice = string.Empty;
    private string _checkText = string.Empty;
    private string _checkResult = string.Empty;
    private bool _loading;
    private int _checkRun;

    internal PeoplePage(SettingsViewModel root, IPersonStore? store = null, IPersonResolver? resolver = null)
        : base(root, SettingsSection.People)
    {
        _store = store;
        _resolver = resolver;
        _add = new RelayCommand(_ => Add(), _ => _store is not null && !People.Any(person => person.IsNew) && People.Count < PersonRules.MaxPeople);
        _check = new RelayCommand(_ => _ = CheckAsync(), _ => _resolver is not null && PersonText.Fold(_checkText).Length > 0);
        People.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPeople));
            OnPropertyChanged(nameof(HasNoPeople));
            _add.RaiseCanExecuteChanged();
        };
        if (_store is not null)
        {
            _store.Changed += OnStoreChanged;
        }
    }

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

    // Someone was kept or removed, here or in a conversation: the list is brought up to date, on the window's own thread.
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            _ = SyncAsync();
        }
        else
        {
            _ui.Post(_ => _ = SyncAsync(), null);
        }
    }

    /// <summary>
    /// Brings the list up to date with the store without touching what the user has open: people who were kept elsewhere are added where their name
    /// goes, people who were removed go, and a person who is not being edited shows what is kept now. It never throws.
    /// </summary>
    internal async Task SyncAsync()
    {
        if (_store is null || _disposed || _loading)
        {
            return;
        }

        IReadOnlyList<Person> people;
        try
        {
            people = await _store.ListAsync().ConfigureAwait(true);
        }
        catch (PersonStoreException)
        {
            return;
        }

        foreach (var gone in People.Where(item => !item.IsNew && !item.IsEditing && people.All(person => person.Id != item.Id)).ToList())
        {
            People.Remove(gone);
        }

        foreach (var person in people)
        {
            if (People.FirstOrDefault(item => item.Id == person.Id) is { } known)
            {
                known.Show(person);
                continue;
            }

            var item = new PersonItem(this, _store, person, isNew: false);
            People.Add(item);
            Saved(item);
        }
    }

    /// <summary>The people, ordered by name; a person being added comes last until they are kept.</summary>
    public ObservableCollection<PersonItem> People { get; } = [];

    /// <summary>Whether the page can keep people at all.</summary>
    public bool HasStore => _store is not null;

    /// <summary>Whether anyone is listed.</summary>
    public bool HasPeople => People.Count > 0;

    /// <summary>Whether no one is, so the page says what people are for.</summary>
    public bool HasNoPeople => People.Count == 0;

    /// <summary>What the page says about what people are for.</summary>

    /// <summary>What the page says when no one is listed.</summary>
    public string EmptyText => "No one yet.";

    /// <summary>What the page says when the people could not be read, or empty.</summary>
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

    /// <summary>Whether the list is being read.</summary>
    public bool IsLoading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    /// <summary>Starts adding a person.</summary>
    public ICommand AddCommand => _add;

    /// <summary>A name the user types to see who the Assistant takes it to be: "my brother".</summary>
    public string CheckText
    {
        get => _checkText;
        set
        {
            if (Set(ref _checkText, value ?? string.Empty))
            {
                CheckResult = string.Empty;
                _check.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Who the Assistant takes <see cref="CheckText"/> to be, or what it would ask.</summary>
    public string CheckResult
    {
        get => _checkResult;
        private set
        {
            if (Set(ref _checkResult, value))
            {
                OnPropertyChanged(nameof(HasCheckResult));
            }
        }
    }

    /// <summary>Whether there is a <see cref="CheckResult"/>.</summary>
    public bool HasCheckResult => _checkResult.Length > 0;

    /// <summary>Whether the page can try a name.</summary>
    public bool CanCheck => _resolver is not null;

    /// <summary>Tries the name in <see cref="CheckText"/>.</summary>
    public ICommand CheckCommand => _check;

    /// <summary>
    /// Reads the people and lists them, dropping any being edited (the window shows a fresh list each time it is opened). It never throws: when the people
    /// cannot be read, the page says so.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var people = await _store.ListAsync(cancellationToken).ConfigureAwait(true);
            People.Clear();
            foreach (var person in people)
            {
                People.Add(new PersonItem(this, _store, person, isNew: false));
            }

            Notice = string.Empty;
        }
        catch (PersonStoreException)
        {
            Notice = "The people couldn't be read. Check that the disk has room, then open Settings again.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // People are not settings: nothing here depends on them.
    internal override void Apply(AppSettings settings, bool fresh)
    {
    }

    // A person was kept: put them where their name goes in the list.
    internal void Saved(PersonItem item)
    {
        var from = People.IndexOf(item);
        if (from < 0)
        {
            return;
        }

        // Everyone already kept whose name goes before this one's (or is the same) stays ahead of them; anyone being added is last.
        var to = People.Count(other => !ReferenceEquals(other, item) && !other.IsNew
            && string.Compare(other.Title, item.Title, StringComparison.CurrentCultureIgnoreCase) <= 0);
        to = Math.Clamp(to, 0, People.Count - 1);
        if (to != from)
        {
            People.Move(from, to);
        }

        _add.RaiseCanExecuteChanged();
    }

    // A person was removed, or a person that was never kept was given up.
    internal void Dropped(PersonItem item)
    {
        People.Remove(item);
        _add.RaiseCanExecuteChanged();
    }

    private void Add()
    {
        if (_store is null)
        {
            return;
        }

        People.Add(new PersonItem(this, _store, Person.Create(string.Empty, DateTimeOffset.UtcNow), isNew: true));
    }

    private async Task CheckAsync()
    {
        if (_resolver is null)
        {
            return;
        }

        var run = ++_checkRun;
        var asked = _checkText;
        try
        {
            var resolution = await _resolver.ResolveAsync(asked).ConfigureAwait(true);

            // Only the latest question is answered: what was typed since is a different question.
            if (run == _checkRun && asked == _checkText)
            {
                CheckResult = resolution.Person is { } person
                    ? $"That is {person.DisplayName}."
                    : resolution.Message;
            }
        }
        catch (PersonStoreException)
        {
            if (run == _checkRun)
            {
                CheckResult = "The people couldn't be read right now.";
            }
        }
    }
}
