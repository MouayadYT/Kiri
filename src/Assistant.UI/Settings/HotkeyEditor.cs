using System.Windows.Input;
using Assistant.Core.Settings;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// One global shortcut in the Hotkeys page: what it opens, its keys, and the recording of new ones. Recording waits for
/// the user to press the keys they want; a combination that cannot work (no Alt, Ctrl or Windows key with the key, or the
/// same as another shortcut) is explained and recording goes on, and Esc gives it up.
/// </summary>
public sealed class HotkeyEditor : NotifyingObject
{
    private readonly Hotkey? _default;
    private readonly Action<Hotkey?> _commit;
    private readonly Func<Hotkey, string?> _usedBy;
    private readonly RelayCommand _record;
    private readonly RelayCommand _reset;
    private readonly RelayCommand _turnOff;
    private Hotkey? _shortcut;
    private bool _isRecording;
    private string _problem = string.Empty;

    internal HotkeyEditor(
        string title, string description, Hotkey? defaultShortcut, Action<Hotkey?> commit, Func<Hotkey, string?> usedBy)
    {
        Title = title;
        Description = description;
        _default = defaultShortcut;
        _commit = commit;
        _usedBy = usedBy;
        _record = new RelayCommand(StartRecording);
        _reset = new RelayCommand(_ => _commit(_default), _ => CanReset);
        _turnOff = new RelayCommand(_ => _commit(null), _ => Shortcut is not null);
    }

    /// <summary>What the shortcut opens.</summary>
    public string Title { get; }

    /// <summary>A sentence about it.</summary>
    public string Description { get; }

    /// <summary>The keys, or <see langword="null"/> when the shortcut is off.</summary>
    public Hotkey? Shortcut
    {
        get => _shortcut;
        private set
        {
            if (Set(ref _shortcut, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                _reset.RaiseCanExecuteChanged();
                _turnOff.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The keys as the user is shown them, such as <c>Alt + A</c>, or <c>Off</c>.</summary>
    public string DisplayText => Shortcut is { } shortcut ? shortcut.ToString().Replace("+", " + ", StringComparison.Ordinal) : "Off";

    /// <summary>Whether the page is waiting for the user to press the new keys.</summary>
    public bool IsRecording
    {
        get => _isRecording;
        private set => Set(ref _isRecording, value);
    }

    /// <summary>Why the keys the user pressed cannot be used, or empty.</summary>
    public string Problem
    {
        get => _problem;
        private set
        {
            if (Set(ref _problem, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    /// <summary>Whether there is a <see cref="Problem"/>.</summary>
    public bool HasProblem => _problem.Length > 0;

    /// <summary>Starts waiting for new keys.</summary>
    public ICommand RecordCommand => _record;

    /// <summary>Goes back to the shortcut the Assistant starts with.</summary>
    public ICommand ResetCommand => _reset;

    /// <summary>Turns the shortcut off.</summary>
    public ICommand TurnOffCommand => _turnOff;

    private bool CanReset => _default is null ? Shortcut is not null : Shortcut is null || !SettingsValidator.Same(Shortcut, _default);

    /// <summary>
    /// Offers the keys the user pressed while recording. Returns <see langword="true"/> and saves them when they can be
    /// the shortcut, and otherwise says why not in <see cref="Problem"/> and keeps waiting.
    /// </summary>
    public bool TryAccept(HotkeyModifiers modifiers, string? key)
    {
        if (HotkeyNames.Normalize(key) is not { } name)
        {
            Problem = "That key can't be used in a shortcut. Try a letter, a number or a function key.";
            return false;
        }

        var shortcut = new Hotkey(modifiers, name);
        if (!SettingsLimits.IsValidHotkey(shortcut))
        {
            Problem = "Hold Alt, Ctrl or the Windows key while you press the key you want.";
            return false;
        }

        if (_usedBy(shortcut) is { } other)
        {
            Problem = $"{shortcut.ToString().Replace("+", " + ", StringComparison.Ordinal)} is already used for {other}.";
            return false;
        }

        Problem = string.Empty;
        IsRecording = false;
        _commit(shortcut);
        return true;
    }

    /// <summary>Gives up recording, leaving the shortcut as it was.</summary>
    public void CancelRecording()
    {
        IsRecording = false;
        Problem = string.Empty;
    }

    internal void Show(Hotkey? shortcut) => Shortcut = shortcut;

    private void StartRecording()
    {
        Problem = string.Empty;
        IsRecording = true;
    }
}
