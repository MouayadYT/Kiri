using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Assistant.Windows.Frame;

namespace Assistant.UI.Windowing;

/// <summary>
/// Gives a WPF window with custom chrome the frame Windows draws (<see cref="IWindowFrame"/>): WPF's own background
/// is made transparent so the system backdrop shows behind the window's surfaces, and <see cref="Backdrop.IsBlurred"/>
/// on the window tells its styles whether to draw those surfaces translucent or opaque.
/// </summary>
internal sealed class WindowFrameHost
{
    private readonly Window _window;
    private readonly IWindowFrameFactory _factory;
    private readonly WindowFrameStyle _style;
    private IWindowFrame? _frame;

    private WindowFrameHost(Window window, IWindowFrameFactory factory, WindowFrameStyle style)
    {
        _window = window;
        _factory = factory;
        _style = style;
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnClosed;
    }

    /// <summary>
    /// Gives <paramref name="window"/> a frame in <paramref name="style"/> for its lifetime. Call it before the window
    /// is shown.
    /// </summary>
    public static WindowFrameHost Attach(Window window, IWindowFrameFactory factory, WindowFrameStyle style) =>
        new(window, factory, style);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = Colors.Transparent;
        }

        _frame = _factory.Apply(handle, _style);
        _frame.IsTranslucentChanged += OnIsTranslucentChanged;
        Backdrop.SetIsBlurred(_window, _frame.IsTranslucent);
    }

    private void OnIsTranslucentChanged(object? sender, EventArgs e) =>
        Backdrop.SetIsBlurred(_window, _frame?.IsTranslucent == true);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_frame is not null)
        {
            _frame.IsTranslucentChanged -= OnIsTranslucentChanged;
            _frame.Dispose();
            _frame = null;
        }
    }
}
