using System.Windows;
using Assistant.UI.Views;
using Assistant.Windows.Audio;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>Shows the orb preview, a window for watching the assistant orb move before the voice pipeline exists.</summary>
internal interface IOrbPreview
{
    /// <summary>Opens the preview, or brings the open one forward.</summary>
    void Show();
}

/// <summary>Opens one <see cref="OrbPreviewWindow"/> at a time, on the UI thread.</summary>
internal sealed class OrbPreviewLauncher : IOrbPreview
{
    private readonly Func<OrbPreviewWindow> _create;
    private OrbPreviewWindow? _window;

    public OrbPreviewLauncher(IMicrophoneLevelMeter meter) : this(() => new OrbPreviewWindow(meter))
    {
    }

    internal OrbPreviewLauncher(Func<OrbPreviewWindow> create) => _create = create;

    /// <inheritdoc/>
    public void Show()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.Dispatcher.InvokeAsync(() =>
        {
            if (_window is null)
            {
                _window = _create();
                _window.Closed += (_, _) => _window = null;
                _window.Show();
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
        });
    }
}
