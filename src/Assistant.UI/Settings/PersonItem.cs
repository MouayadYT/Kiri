using System.Collections.ObjectModel;
using System.Windows.Input;
using Assistant.Core.People;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>A choice of what kind of address a number, address or username is.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Label">What the user is shown.</param>
public sealed record IdentifierKindChoice(PersonIdentifierKind Kind, string Label);

/// <summary>One number, address or username of a person being edited, with the service it is for.</summary>
public sealed class IdentifierItem : NotifyingObject
{
    private IdentifierKindChoice _kind;
    private string _value;
    private string _service;

    internal IdentifierItem(PersonItem owner, PersonIdentifier identifier)
    {
        _kind = PersonItem.KindChoices.First(choice => choice.Kind == identifier.Kind);
        _value = identifier.Value;
        _service = identifier.Service;
        RemoveCommand = new RelayCommand(_ => owner.RemoveIdentifier(this));
    }

    /// <summary>What kind of address it is.</summary>
    public IdentifierKindChoice Kind
    {
        get => _kind;
        set => Set(ref _kind, value ?? _kind);
    }

    /// <summary>The kinds an address can be, for the list the user picks from.</summary>
    public IReadOnlyList<IdentifierKindChoice> Kinds => PersonItem.KindChoices;

    /// <summary>The number, address or username.</summary>
    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? string.Empty);
    }

    /// <summary>The messaging service it is for, or empty for any.</summary>
    public string Service
    {
        get => _service;
        set => Set(ref _service, value ?? string.Empty);
    }

    /// <summary>Takes this address out of the person being edited.</summary>
    public ICommand RemoveCommand { get; }

    internal PersonIdentifier ToIdentifier() => new(_kind.Kind, _value, _service);
}

/// <summary>
/// One person in Settings > People (PROJECT_SPEC §4.9, step 112): the name, the other names, how they relate to the user, and the numbers, addresses and usernames a
/// messaging provider can reach them by, as the user edits them. Nothing is kept until the user clicks Save, and then it is kept as the store tidies it
/// (<see cref="PersonRules"/>); what cannot be kept is explained in words that never repeat what was typed, and the person stays open to fix. Removing a person asks once more
/// first. Aliases and relationships are typed as a list separated by commas. Nothing here logs, and nothing leaves this PC.
/// </summary>
public sealed class PersonItem : NotifyingObject
{
    /// <summary>The kinds of address a person has, in the order the user chooses from them.</summary>
    internal static readonly IdentifierKindChoice[] KindChoices =
    [
        new(PersonIdentifierKind.Phone, "Phone"),
        new(PersonIdentifierKind.Email, "Email"),
        new(PersonIdentifierKind.Username, "Username"),
        new(PersonIdentifierKind.ChatName, "Chat name"),
    ];

    private readonly PeoplePage _page;
    private readonly IPersonStore _store;
    private readonly RelayCommand _save;
    private readonly RelayCommand _cancel;
    private readonly RelayCommand _remove;
    private readonly RelayCommand _confirmRemove;
    private Person _saved;
    private string _name = string.Empty;
    private string _aliases = string.Empty;
    private string _relationships = string.Empty;
    private bool _isEditing;
    private bool _isNew;
    private bool _busy;
    private bool _confirmingRemove;
    private string _message = string.Empty;

    internal PersonItem(PeoplePage page, IPersonStore store, Person person, bool isNew)
    {
        _page = page;
        _store = store;
        _saved = person;
        _isNew = isNew;
        _isEditing = isNew;
        Fill(person);
        EditCommand = new RelayCommand(_ => IsEditing = true, _ => !IsEditing);
        _save = new RelayCommand(_ => _ = SaveAsync(), _ => IsEditing && !Busy);
        _cancel = new RelayCommand(_ => Cancel(), _ => IsEditing && !Busy);
        _remove = new RelayCommand(_ => ConfirmingRemove = true, _ => !Busy && !ConfirmingRemove && !IsNew);
        _confirmRemove = new RelayCommand(_ => _ = RemoveAsync(), _ => !Busy && ConfirmingRemove);
        KeepCommand = new RelayCommand(_ => ConfirmingRemove = false);
        AddIdentifierCommand = new RelayCommand(_ => AddIdentifier(), _ => IsEditing && Identifiers.Count < PersonRules.MaxIdentifiers);
    }

    /// <summary>The person's id.</summary>
    public Guid Id => _saved.Id;

    /// <summary>How a person may relate to the user, offered as buttons under the field so that the usual ones need no typing.</summary>
    public static IReadOnlyList<string> RelationshipSuggestions { get; } =
        ["Brother", "Sister", "Mom", "Dad", "Partner", "Wife", "Husband", "Son", "Daughter", "Friend", "Colleague", "Boss"];

    /// <summary>The suggestions, for the list a person's editor shows.</summary>
    public IReadOnlyList<string> Suggestions => RelationshipSuggestions;

    private ICommand? _addRelationship;

    /// <summary>Adds the relationship it is given to the field, unless it is there already.</summary>
    public ICommand AddRelationshipCommand => _addRelationship ??= new RelayCommand(parameter =>
    {
        if (parameter is not string relationship || relationship.Length == 0 || !IsEditing)
        {
            return;
        }

        var known = Split(_relationships);
        if (!known.Contains(relationship, StringComparer.CurrentCultureIgnoreCase))
        {
            Relationships = string.Join(", ", [.. known, relationship]);
        }
    });

    /// <summary>The person's initials, for the disc drawn before their name: the first letters of the first two words of the name, or a question mark.</summary>
    public string Initials
    {
        get
        {
            var words = Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(word => char.IsLetterOrDigit(word[0])).Take(2).ToArray();
            return _isNew && _name.Trim().Length == 0 || words.Length == 0 ? "?" : string.Concat(words.Select(word => char.ToUpper(word[0], System.Globalization.CultureInfo.CurrentCulture)));
        }
    }


    /// <summary>Shows the person as the store keeps them now, unless the user is editing them: someone else's change never takes what is being typed.</summary>
    internal void Show(Person person)
    {
        if (_isEditing || _busy || person.Id != _saved.Id)
        {
            return;
        }

        _saved = person;
        Fill(person);
    }

    /// <summary>The name the person is known by, as it is kept; while a new person is not kept yet, what was typed.</summary>
    public string Title => _isNew && _name.Trim().Length > 0 ? _name.Trim() : _isNew ? "New person" : _saved.DisplayName;

    /// <summary>A line that says what is known of the person: how they relate to the user, what else they are called, and how many ways there are to reach them.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (_saved.Relationships.Count > 0)
            {
                parts.Add(string.Join(", ", _saved.Relationships));
            }

            if (_saved.Aliases.Count > 0)
            {
                parts.Add("Also called " + string.Join(", ", _saved.Aliases));
            }

            parts.Add(_saved.Identifiers.Count switch
            {
                0 => "No way to reach them yet",
                1 => "1 way to reach them",
                var count => $"{count} ways to reach them",
            });
            return _isNew ? "Not saved yet" : string.Join(" · ", parts);
        }
    }

    /// <summary>The name, as typed.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Initials));
            }
        }
    }

    /// <summary>The other names the person is called, separated by commas, as typed.</summary>
    public string Aliases
    {
        get => _aliases;
        set => Set(ref _aliases, value ?? string.Empty);
    }

    /// <summary>How the person relates to the user ("Brother"), separated by commas if there are several, as typed.</summary>
    public string Relationships
    {
        get => _relationships;
        set => Set(ref _relationships, value ?? string.Empty);
    }

    /// <summary>The numbers, addresses and usernames being edited.</summary>
    public ObservableCollection<IdentifierItem> Identifiers { get; } = [];

    /// <summary>The kinds an address can be, for the list the user picks from.</summary>
    public IReadOnlyList<IdentifierKindChoice> Kinds => KindChoices;

    /// <summary>Whether the person is open for editing.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (Set(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(IsShowing));
                Refresh();
            }
        }
    }

    /// <summary>Whether the person is shown as a line and not open for editing.</summary>
    public bool IsShowing => !_isEditing;

    /// <summary>Whether the person has not been kept yet.</summary>
    public bool IsNew
    {
        get => _isNew;
        private set
        {
            if (Set(ref _isNew, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                Refresh();
            }
        }
    }

    /// <summary>Whether a change is being kept or a person is being removed.</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>Whether the user was asked to confirm removing the person.</summary>
    public bool ConfirmingRemove
    {
        get => _confirmingRemove;
        private set
        {
            if (Set(ref _confirmingRemove, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>The question asked before removing the person.</summary>
    public string RemoveQuestion => $"Remove {_saved.DisplayName}? Their name, aliases and the ways to reach them are deleted from this PC.";

    /// <summary>What went wrong with the last Save or Remove, or empty.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    /// <summary>Whether there is a <see cref="Message"/>.</summary>
    public bool HasMessage => _message.Length > 0;

    /// <summary>Opens the person for editing.</summary>
    public ICommand EditCommand { get; }

    /// <summary>Keeps what was typed.</summary>
    public ICommand SaveCommand => _save;

    /// <summary>Closes the person, dropping what was typed; a person who was not kept yet is dropped.</summary>
    public ICommand CancelCommand => _cancel;

    /// <summary>Asks whether to remove the person.</summary>
    public ICommand RemoveCommand => _remove;

    /// <summary>Removes the person, after the question.</summary>
    public ICommand ConfirmRemoveCommand => _confirmRemove;

    /// <summary>Takes the question back.</summary>
    public ICommand KeepCommand { get; }

    /// <summary>Adds an empty number, address or username to edit.</summary>
    public ICommand AddIdentifierCommand { get; }

    internal void RemoveIdentifier(IdentifierItem item)
    {
        Identifiers.Remove(item);
        Refresh();
    }

    private void AddIdentifier()
    {
        Identifiers.Add(new IdentifierItem(this, new PersonIdentifier(PersonIdentifierKind.Phone, string.Empty)));
        Refresh();
    }

    private async Task SaveAsync()
    {
        Busy = true;
        Message = string.Empty;
        try
        {
            var person = _saved with
            {
                DisplayName = _name,
                Aliases = Split(_aliases),
                Relationships = Split(_relationships),
                Identifiers = [.. Identifiers.Select(item => item.ToIdentifier())],
            };
            _saved = await _store.SaveAsync(person).ConfigureAwait(true);
            Fill(_saved);
            IsNew = false;
            IsEditing = false;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            _page.Saved(this);
        }
        catch (PersonValidationException failure)
        {
            Message = failure.Message;
        }
        catch (PersonStoreException)
        {
            Message = "The person couldn't be saved. Check that the disk has room, then try again.";
        }
        finally
        {
            Busy = false;
        }
    }

    private void Cancel()
    {
        if (_isNew)
        {
            _page.Dropped(this);
            return;
        }

        Fill(_saved);
        Message = string.Empty;
        IsEditing = false;
    }

    private async Task RemoveAsync()
    {
        Busy = true;
        try
        {
            await _store.DeleteAsync(_saved.Id).ConfigureAwait(true);
            _page.Dropped(this);
        }
        catch (PersonStoreException)
        {
            ConfirmingRemove = false;
            Message = "The person couldn't be removed. Check that the disk has room, then try again.";
        }
        finally
        {
            Busy = false;
        }
    }

    // Shows a person in the fields, dropping whatever was typed.
    private void Fill(Person person)
    {
        Name = person.DisplayName;
        Aliases = string.Join(", ", person.Aliases);
        Relationships = string.Join(", ", person.Relationships);
        Identifiers.Clear();
        foreach (var identifier in person.Identifiers)
        {
            Identifiers.Add(new IdentifierItem(this, identifier));
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(RemoveQuestion));
    }

    // A list typed with commas, semicolons or line breaks between its parts.
    private static string[] Split(string text) => text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Refresh()
    {
        _save.RaiseCanExecuteChanged();
        _cancel.RaiseCanExecuteChanged();
        _remove.RaiseCanExecuteChanged();
        _confirmRemove.RaiseCanExecuteChanged();
        ((RelayCommand)EditCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddIdentifierCommand).RaiseCanExecuteChanged();
    }
}
