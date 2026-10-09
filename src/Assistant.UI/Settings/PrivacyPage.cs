using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Core.Settings;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>A choice of how long history is kept.</summary>
/// <param name="Value">The retention.</param>
/// <param name="Label">What the user is shown.</param>
public sealed record RetentionChoice(HistoryRetention Value, string Label);

/// <summary>
/// Privacy: Local Only mode, whether conversations are saved, how long they are kept, and the folders kept out of search. Every control takes
/// effect at once: old conversations are deleted by <c>HistoryRetentionService</c>, and the file search leaves the excluded folders out of what it
/// lists and of what answers are drawn from.
/// </summary>
public sealed class PrivacyPage : SettingsPage
{
    private bool _localOnly = true;
    private bool _historyEnabled;
    private RetentionChoice _retention;
    private readonly IClipboardHistory? _clipboard;
    private readonly Dispatcher? _dispatcher;
    private readonly RelayCommand _clearClipboardCommand;

    internal PrivacyPage(
        SettingsViewModel root, IClipboardHistory? clipboard = null, Dispatcher? dispatcher = null, Func<string?>? pickFolder = null)
        : base(root, SettingsSection.Privacy)
    {
        PickFolder = pickFolder ?? PickFolderWithDialog;
        AddExcludedFolderCommand = new RelayCommand(_ => AddExcludedFolder());
        RemoveExcludedFolderCommand = new RelayCommand(folder => RemoveExcludedFolder(folder as string));
        _retention = Retentions[0];
        _clipboard = clipboard;
        _dispatcher = dispatcher;
        _clearClipboardCommand = new RelayCommand(_ => ClearClipboardHistory(), _ => _clipboard is { Items.Count: > 0 });
        if (_clipboard is not null)
        {
            // The history changes on the clipboard watcher's thread; the page shows it on its own.
            _clipboard.Changed += (_, _) =>
            {
                if (_dispatcher is null || _dispatcher.CheckAccess())
                {
                    RefreshClipboardHistory();
                }
                else
                {
                    _dispatcher.BeginInvoke(RefreshClipboardHistory);
                }
            };
        }
    }

    /// <summary>
    /// What the page says about the Assistant's clipboard history (Settings, Permissions, Clipboard History): how many items it keeps now,
    /// or that it keeps none because it is off. It is in memory only, and clearing it forgets everything at once.
    /// </summary>
    public string ClipboardHistoryStatus => _clipboard switch
    {
        null => "",
        { IsEnabled: false } => "Off. Allow Clipboard History under Permissions to keep what you copy.",
        { Items.Count: 0 } => "On. Nothing is kept yet.",
        { Items.Count: 1 } => "On. 1 item is kept, in memory only.",
        var history => $"On. {history.Items.Count} items are kept, in memory only.",
    };

    /// <summary>Whether the page has a clipboard history to show and clear.</summary>
    public bool HasClipboardHistory => _clipboard is not null;

    /// <summary>Forgets everything the clipboard history kept, at once.</summary>
    public ICommand ClearClipboardHistoryCommand => _clearClipboardCommand;

    private void ClearClipboardHistory()
    {
        _clipboard?.Clear();
        RefreshClipboardHistory();
    }

    private void RefreshClipboardHistory()
    {
        OnPropertyChanged(nameof(ClipboardHistoryStatus));
        _clearClipboardCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The lengths of time history can be kept for.</summary>
    public IReadOnlyList<RetentionChoice> Retentions { get; } =
    [
        new(HistoryRetention.UntilDeleted, "Until I delete it"),
        new(HistoryRetention.NinetyDays, "90 days"),
        new(HistoryRetention.ThirtyDays, "30 days"),
        new(HistoryRetention.SevenDays, "7 days"),
    ];

    /// <summary>
    /// Local Only mode (PROJECT_SPEC §3.4): nothing leaves this PC. While it is on, searching the web with a picture is off, and turning it off
    /// does not send anything by itself: each picture is still sent only when the user confirms it.
    /// </summary>
    public bool LocalOnly
    {
        get => _localOnly;
        set
        {
            if (Set(ref _localOnly, value))
            {
                Commit(settings => settings with { Privacy = settings.Privacy with { LocalOnly = value } });
            }
        }
    }

    /// <summary>Whether conversations are saved. When off, nothing from a conversation is written to disk.</summary>
    public bool HistoryEnabled
    {
        get => _historyEnabled;
        set
        {
            if (Set(ref _historyEnabled, value))
            {
                Commit(settings => settings with { Privacy = settings.Privacy with { HistoryEnabled = value } });
            }
        }
    }

    /// <summary>How long saved conversations are kept.</summary>
    public RetentionChoice Retention
    {
        get => _retention;
        set
        {
            if (value is not null && Set(ref _retention, value))
            {
                Commit(settings => settings with { Privacy = settings.Privacy with { HistoryRetention = value.Value } });
            }
        }
    }

    /// <summary>The folders whose contents never appear in search results or grounded answers.</summary>
    public IReadOnlyList<string> ExcludedFolders { get; private set; } = [];

    /// <summary>Whether any folder is excluded.</summary>
    public bool HasExcludedFolders => ExcludedFolders.Count > 0;

    /// <summary>How the user is asked for a folder: the system's folder dialog, unless a test says otherwise.</summary>
    internal Func<string?> PickFolder { get; set; }

    /// <summary>Asks the user for a folder and keeps it out of search.</summary>
    public ICommand AddExcludedFolderCommand { get; }

    /// <summary>Lets search look in a folder again; the parameter is the folder as listed.</summary>
    public ICommand RemoveExcludedFolderCommand { get; }

    private void AddExcludedFolder()
    {
        if (IsApplying || PickFolder() is not { Length: > 0 } picked)
        {
            return;
        }

        var folder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(picked));
        if (!SettingsLimits.IsValidPath(folder) || ExcludedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)
            || ExcludedFolders.Count >= SettingsLimits.MaxExcludedFolders)
        {
            return;
        }

        SetExcluded([.. ExcludedFolders, folder]);
    }

    private void RemoveExcludedFolder(string? folder)
    {
        if (IsApplying || folder is null)
        {
            return;
        }

        SetExcluded([.. ExcludedFolders.Where(listed => !string.Equals(listed, folder, StringComparison.OrdinalIgnoreCase))]);
    }

    private void SetExcluded(IReadOnlyList<string> folders)
    {
        ExcludedFolders = folders;
        OnPropertyChanged(nameof(ExcludedFolders));
        OnPropertyChanged(nameof(HasExcludedFolders));
        Commit(settings => settings with { Privacy = settings.Privacy with { ExcludedFolders = folders } });
    }

    private static string? PickFolderWithDialog()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to keep out of search" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    internal override void Apply(AppSettings settings, bool fresh)
    {
        LocalOnly = settings.Privacy.LocalOnly;
        HistoryEnabled = settings.Privacy.HistoryEnabled;
        Retention = Retentions.FirstOrDefault(choice => choice.Value == settings.Privacy.HistoryRetention) ?? Retentions[0];
        ExcludedFolders = settings.Privacy.ExcludedFolders;
        OnPropertyChanged(nameof(ExcludedFolders));
        OnPropertyChanged(nameof(HasExcludedFolders));
        RefreshClipboardHistory();
    }
}
