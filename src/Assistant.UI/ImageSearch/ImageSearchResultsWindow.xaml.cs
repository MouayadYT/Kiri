using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Frame;
using Assistant.Windows.Placement;

namespace Assistant.UI.ImageSearch;

/// <summary>Shows what an image search found.</summary>
internal interface IImageSearchResultsWindow
{
    /// <summary>Shows <paramref name="results"/> in the results window, bringing it forward; one that is already open shows these instead.</summary>
    void ShowResults(ImageSearchResultsViewModel results);
}

/// <summary>
/// The image search results panel (PROJECT_SPEC §4.6): a window of its own, drawn like the History window (a dark Windows backdrop and
/// its own buttons) and laid out from its reference, with a size of its own. There is one: a new search shows its results in it.
/// Closing it, and Esc, only hide it, and let go of the results, which are not kept.
/// </summary>
internal sealed partial class ImageSearchResultsWindow : Window, IImageSearchResultsWindow
{
    // The window opens at its own size, or smaller on a smaller screen, leaving this much of the work area around it.
    private const double ScreenMargin = 24;

    // The hairline Windows draws around the window: the History window's.
    private const int BorderColor = 0x3C3C3C;

    private readonly IWindowPlacementService _placement;
    private bool _placed;
    private bool _closing;

    public ImageSearchResultsWindow(IWindowFrameFactory frames, IWindowPlacementService placement)
    {
        InitializeComponent();
        _placement = placement;
        WindowFrameHost.Attach(this, frames, new WindowFrameStyle(SystemBackdropKind.Mica, BorderColor));

        // What was found is not kept once the window is put away.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                DataContext = null;
            }
        };
    }

    /// <summary>The results the window shows now, or <see langword="null"/> when it holds none.</summary>
    internal ImageSearchResultsViewModel? Results => DataContext as ImageSearchResultsViewModel;

    /// <inheritdoc/>
    public void ShowResults(ImageSearchResultsViewModel results)
    {
        ArgumentNullException.ThrowIfNull(results);
        DataContext = results;
        Scroller.ScrollToTop();
        if (!_placed)
        {
            _placed = true;
            _placement.PlaceCenteredOnActiveMonitor(Handle, Width, Height, ScreenMargin);
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        // Its close button, Alt+F4 and the taskbar's Close window hide it; when the application shuts down, WPF closes it regardless.
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
        Close();
    }

    private void OnCloseExecuted(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);


    private nint Handle => new WindowInteropHelper(this).EnsureHandle();
}

/// <summary>Shows results in the results window, which is created the first time there are some.</summary>
internal sealed class LazyImageSearchResultsWindow(Func<ImageSearchResultsWindow> create) : IImageSearchResultsWindow
{
    private readonly Lazy<ImageSearchResultsWindow> _window = new(create);

    /// <inheritdoc/>
    public void ShowResults(ImageSearchResultsViewModel results) => _window.Value.ShowResults(results);
}
