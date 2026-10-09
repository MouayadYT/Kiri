using System.Reflection;
using System.Windows.Input;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.Core.Updates;
using Assistant.UI.Updates;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>Someone who made the Assistant, as the About page names them: their name on GitHub and their page there.</summary>
public sealed record AboutCreator(string Name, string Url);

/// <summary>
/// About: which build this is, who made it, whether a newer one is out, and where the Assistant keeps what it keeps. The one thing to do here is to look
/// for an update, which asks GitHub for the project's latest release when the button is pressed.
/// </summary>
public sealed class AboutPage : SettingsPage
{
    internal const string CheckingText = "Checking…";
    internal const string UpToDateText = "You're running the latest version.";
    internal const string FailedText = "Couldn't check right now. Try again later.";

    private readonly IUpdateChecker? _updates;
    private readonly RelayCommand _check;
    private readonly RelayCommand _openUpdate;
    private AvailableUpdate? _found;
    private string _updateStatus = "";
    private bool _checking;

    internal AboutPage(SettingsViewModel root, AppPaths paths, IUpdateChecker? updates = null)
        : base(root, SettingsSection.About)
    {
        _updates = updates;
        Version = typeof(AboutPage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? typeof(AboutPage).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        DataFolder = paths.RootDirectory;
        SettingsFile = paths.SettingsFilePath;
        _check = new RelayCommand(_ => Checking = CheckAsync(), _ => _updates is not null && !_checking);
        _openUpdate = new RelayCommand(_ => OpenPage(_found?.ReleaseUrl ?? ProjectUrl), _ => _found is not null);
        OpenLinkCommand = new RelayCommand(parameter =>
        {
            if (parameter is string url)
            {
                OpenPage(url);
            }
        });
    }

    /// <summary>The Assistant's name.</summary>
    public string Name => "Assistant";

    /// <summary>The build's version.</summary>
    public string Version { get; }

    /// <summary>Who made it.</summary>
    public IReadOnlyList<AboutCreator> Creators { get; } =
    [
        new("MouayadYT", "https://github.com/MouayadYT"),
        new("MuhannadYT", "https://github.com/MuhannadYT"),
    ];

    /// <summary>The project's page on GitHub.</summary>
    public string ProjectUrl => GitHubUpdateChecker.ProjectUrl;

    /// <summary>Opens the page its parameter names (a creator's, the project's) in the browser.</summary>
    public ICommand OpenLinkCommand { get; }

    /// <summary>Whether there is something to look for updates with.</summary>
    public bool CanCheckForUpdates => _updates is not null;

    /// <summary>Asks GitHub now whether a newer release is out.</summary>
    public ICommand CheckForUpdatesCommand => _check;

    /// <summary>What the last look found, in a few words; empty until the button is pressed.</summary>
    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (Set(ref _updateStatus, value))
            {
                OnPropertyChanged(nameof(HasUpdateStatus));
            }
        }
    }

    /// <summary>Whether there is an <see cref="UpdateStatus"/>.</summary>
    public bool HasUpdateStatus => _updateStatus.Length > 0;

    /// <summary>Whether a newer release was found, so that its page is offered.</summary>
    public bool HasUpdate => _found is not null;

    /// <summary>Opens the newer release's page in the browser.</summary>
    public ICommand OpenUpdateCommand => _openUpdate;

    /// <summary>The folder the Assistant keeps its data in.</summary>
    public string DataFolder { get; }

    /// <summary>The file these settings are saved in.</summary>
    public string SettingsFile { get; }

    /// <summary>Opens a page in the user's browser; a test gives its own.</summary>
    internal Action<string> OpenPage { get; set; } = UpdateNotifier.OpenInBrowser;

    /// <summary>The look that the button started last, or a finished task; for tests.</summary>
    internal Task Checking { get; private set; } = Task.CompletedTask;

    internal override void Apply(AppSettings settings, bool fresh)
    {
    }

    private async Task CheckAsync()
    {
        if (_updates is null || _checking)
        {
            return;
        }

        _checking = true;
        _check.RaiseCanExecuteChanged();
        UpdateStatus = CheckingText;
        try
        {
            var result = await _updates.CheckNowAsync().ConfigureAwait(true);
            _found = result.Update;
            UpdateStatus = result.Update is { } update
                ? $"Version {update.NewVersion.TrimStart('v', 'V')} is available."
                : result.Failed ? FailedText : UpToDateText;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _found = null;
            UpdateStatus = FailedText;
        }
        finally
        {
            _checking = false;
            _check.RaiseCanExecuteChanged();
            _openUpdate.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasUpdate));
        }
    }
}
