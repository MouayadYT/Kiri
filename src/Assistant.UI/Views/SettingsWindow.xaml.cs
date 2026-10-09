using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Assistant.UI.Settings;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Frame;
using Assistant.Windows.Placement;

namespace Assistant.UI.Views;

/// <summary>
/// The Assistant's settings (PROJECT_SPEC §4.9): a resizable top-level window in the taskbar, with the sections in a
/// sidebar on the left and the open section's page on the right, drawn like the History window: Windows draws the frame
/// and a dark backdrop, and the window draws everything inside it, its own close, minimize and maximize buttons included.
/// Closing it only hides it, so it opens again as it was, showing the settings as they are saved by then.
/// </summary>
public partial class SettingsWindow : Window, ISettingsWindow
{
    // The window opens at its own size, or smaller on a smaller screen, leaving this much of the work area around it.
    private const double ScreenMargin = 24;

    // The hairline Windows draws around the window: the History window's, a neutral gray a little lighter than the workspace.
    private const int BorderColor = 0x3C3C3C;

    private readonly SettingsViewModel _settings;
    private readonly IWindowPlacementService _placement;
    private bool _placed;
    private bool _closing;

    public SettingsWindow(SettingsViewModel settings, IWindowFrameFactory frames, IWindowPlacementService placement)
    {
        InitializeComponent();
        _settings = settings;
        _placement = placement;
        DataContext = settings;
        WindowFrameHost.Attach(this, frames, new WindowFrameStyle(SystemBackdropKind.Mica, BorderColor));
        StateChanged += (_, _) => KeepContentOnScreen();
        DpiChanged += (_, _) => KeepContentOnScreen();

        // The activity page follows the log only while the window is in front of the user.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                _settings.Activity.StopFollowing();
            }
        };
    }

    /// <summary>The settings the window shows and changes.</summary>
    internal SettingsViewModel ViewModel => _settings;

    private void OnPageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || PageScroller.ScrollableHeight <= 0) return;
        PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <inheritdoc/>
    public void ShowAndActivate()
    {
        if (!_placed)
        {
            _placed = true;
            _placement.PlaceCenteredOnActiveMonitor(Handle, Width, Height, ScreenMargin);
        }

        // What is saved may have changed since the window was last shown, such as by loading a model file elsewhere.
        _ = _settings.LoadAsync();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Sections.Focus();
    }

    /// <inheritdoc/>
    public void ShowAndActivate(SettingsSection section)
    {
        // A section that is part of another is shown there.
        section = section switch { SettingsSection.Context => SettingsSection.Model, SettingsSection.Cleanup => SettingsSection.Privacy, _ => section };
        _settings.SelectedSection = _settings.Sections.First(item => item.Section == section);
        ShowAndActivate();
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Esc puts the window away, unless it is waiting for a shortcut, when Esc gives that up instead.
        if (e.Key == Key.Escape && !IsRecordingShortcut)
        {
            e.Handled = true;
            Hide();
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        // Its close button, Alt+F4 and the taskbar's Close window hide it. When the application shuts down, WPF closes it
        // regardless.
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Closes the window instead of hiding it, for tests.</summary>
    internal void CloseForGood()
    {
        _closing = true;
        _settings.Dispose();
        Close();
    }

    private bool IsRecordingShortcut =>
        _settings.Hotkeys.SearchOrAsk.IsRecording || _settings.Hotkeys.SelectedText.IsRecording
        || _settings.Hotkeys.VisualIntelligence.IsRecording;

    private void OnCloseExecuted(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnMinimizeExecuted(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    // A maximized window reaches past the screen by its resize border on every side; its content stays inside.
    private void KeepContentOnScreen()
    {
        if (WindowState == WindowState.Maximized)
        {
            var overhang = FrameMetrics.MaximizedOverhang(Handle) / VisualTreeHelper.GetDpi(this).DpiScaleX;
            Root.Margin = new Thickness(overhang);
        }
        else
        {
            Root.Margin = default;
        }
    }

    private nint Handle => new WindowInteropHelper(this).EnsureHandle();
}
