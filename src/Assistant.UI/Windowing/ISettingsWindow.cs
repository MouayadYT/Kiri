using System.Windows;

namespace Assistant.UI.Windowing;

/// <summary>The Settings window, as whatever opens it sees it.</summary>
internal interface ISettingsWindow
{
    /// <summary>
    /// Brings the window forward, restored if it was minimized, showing the settings as they are saved now. The first
    /// time, it opens on the monitor the user is working on.
    /// </summary>
    void ShowAndActivate();

    /// <summary>As <see cref="ShowAndActivate()"/>, with the page of <paramref name="section"/> open.</summary>
    void ShowAndActivate(Assistant.UI.Settings.SettingsSection section) => ShowAndActivate();
}

/// <summary>Opens the Settings window: from the "demo settings" command now, and from the tray menu when there is one.</summary>
internal interface ISettingsLauncher
{
    /// <summary>Opens the window, or brings the open one forward.</summary>
    void Show();

    /// <summary>Opens the window on the page of <paramref name="section"/>, or brings the open one forward and shows that page.</summary>
    void Show(Assistant.UI.Settings.SettingsSection section) => Show();
}

/// <summary>Opens the Settings window on the UI thread, creating it the first time it is needed.</summary>
internal sealed class SettingsLauncher : ISettingsLauncher
{
    private readonly Lazy<ISettingsWindow> _window;

    public SettingsLauncher(Func<ISettingsWindow> window) => _window = new Lazy<ISettingsWindow>(window);

    /// <inheritdoc/>
    public void Show()
    {
        if (Application.Current is { } application)
        {
            application.Dispatcher.InvokeAsync(() => _window.Value.ShowAndActivate());
        }
    }

    /// <inheritdoc/>
    public void Show(Assistant.UI.Settings.SettingsSection section)
    {
        if (Application.Current is { } application)
        {
            application.Dispatcher.InvokeAsync(() => _window.Value.ShowAndActivate(section));
        }
    }
}
