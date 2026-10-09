using Assistant.Core.ModelHosting;
using Assistant.Windows.Tray;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Tray;

/// <summary>
/// The menu behind the Assistant's icon in the notification area (PROJECT_SPEC §4.9): Open Assistant, New Conversation, Settings, Pause
/// Local AI (Resume Local AI while it is paused) and Exit. A click on the icon is Open Assistant, which opens the full window (the bar is the
/// shortcut's). The controller owns what each line does
/// by calling what the app already has; the icon itself is <see cref="INotificationAreaIcon"/>. It logs which command ran, never what the
/// user did with it.
/// </summary>
internal sealed class TrayController : IDisposable
{
    /// <summary>The ids of the menu's lines.</summary>
    internal const int OpenId = 1;
    internal const int NewConversationId = 2;
    internal const int SettingsId = 3;
    internal const int PauseId = 4;
    internal const int ExitId = 5;

    internal const string OpenText = "Open Assistant";
    internal const string NewConversationText = "New Conversation";
    internal const string SettingsText = "Settings";
    internal const string PauseText = "Pause Local AI";
    internal const string ResumeText = "Resume Local AI";
    internal const string ExitText = "Exit";
    internal const string Tooltip = "Assistant";
    internal const string PausedTooltip = "Assistant (local AI paused)";
    internal const string GamePausedTooltip = "Assistant (game mode: local AI paused)";

    private readonly INotificationAreaIcon _icon;
    private readonly Action _open;
    private readonly Action _newConversation;
    private readonly Action _openSettings;
    private readonly ILocalAiPause _pause;
    private readonly Action _exit;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Creates the controller.</summary>
    /// <param name="icon">The icon in the notification area.</param>
    /// <param name="open">Opens the Assistant: its full window.</param>
    /// <param name="newConversation">Opens the Assistant on a new, empty conversation.</param>
    /// <param name="openSettings">Opens the Settings window.</param>
    /// <param name="pause">Pauses and resumes the local AI.</param>
    /// <param name="exit">Ends the Assistant.</param>
    /// <param name="post">Runs an action on the UI thread; a change of the pause may be announced on another.</param>
    /// <param name="logger">The log.</param>
    public TrayController(
        INotificationAreaIcon icon, Action open, Action newConversation, Action openSettings, ILocalAiPause pause, Action exit,
        Action<Action> post, ILogger logger)
    {
        _icon = icon;
        _open = open;
        _newConversation = newConversation;
        _openSettings = openSettings;
        _pause = pause;
        _exit = exit;
        _post = post;
        _logger = logger;
        _icon.Selected += OnSelected;
        _icon.CommandInvoked += OnCommand;
        _pause.Changed += OnPauseChanged;
        Refresh();
    }

    /// <summary>Puts the icon in the notification area. Returns whether Windows took it (it may be added later, when the taskbar starts).</summary>
    public bool Start()
    {
        var shown = _icon.Show();
        TrayLog.IconShown(_logger, shown);
        return shown;
    }

    /// <summary>The lines of the menu as they are now.</summary>
    internal IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new TrayMenuItem(OpenId, OpenText) { IsDefault = true },
        new TrayMenuItem(NewConversationId, NewConversationText),
        TrayMenuItem.Separator,
        new TrayMenuItem(SettingsId, SettingsText),
        new TrayMenuItem(PauseId, _pause.IsPaused ? ResumeText : PauseText),
        TrayMenuItem.Separator,
        new TrayMenuItem(ExitId, ExitText),
    ];

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Selected -= OnSelected;
        _icon.CommandInvoked -= OnCommand;
        _pause.Changed -= OnPauseChanged;
        _icon.Dispose();
    }

    private void Refresh()
    {
        _icon.Menu = BuildMenu();
        _icon.Tooltip = _pause.Reason switch
        {
            LocalAiPauseReason.None => Tooltip,
            LocalAiPauseReason.GameMode => GamePausedTooltip,
            _ => PausedTooltip,
        };
    }

    private void OnSelected(object? sender, EventArgs e) => Run(OpenId);

    private void OnCommand(object? sender, int command) => Run(command);

    private void OnPauseChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (!_disposed)
        {
            Refresh();
        }
    });

    private void Run(int command)
    {
        try
        {
            switch (command)
            {
                case OpenId:
                    TrayLog.CommandRun(_logger, "open");
                    _open();
                    break;
                case NewConversationId:
                    TrayLog.CommandRun(_logger, "new-conversation");
                    _newConversation();
                    break;
                case SettingsId:
                    TrayLog.CommandRun(_logger, "settings");
                    _openSettings();
                    break;
                case PauseId:
                    TrayLog.CommandRun(_logger, _pause.IsPaused ? "resume" : "pause");
                    TogglePause();
                    break;
                case ExitId:
                    TrayLog.CommandRun(_logger, "exit");
                    _exit();
                    break;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TrayLog.CommandFailed(_logger, exception);
        }
    }

    private void TogglePause()
    {
        if (_pause.IsPaused)
        {
            _pause.Resume();
            return;
        }

        // The menu does not wait for the model to unload; the icon shows the pause as soon as it is set (Changed), before that is done.
        _ = PauseAsync();
    }

    private async Task PauseAsync()
    {
        try
        {
            await _pause.PauseAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TrayLog.CommandFailed(_logger, exception);
        }
    }
}
