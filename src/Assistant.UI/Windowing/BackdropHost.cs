using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Assistant.Windows.Backdrop;

namespace Assistant.UI.Windowing;

/// <summary>A piece of glass as it is drawn at one moment: how much of it shows, and where the blur beneath it goes.</summary>
internal readonly record struct BackdropGlass(double Opacity, BackdropRegion Region);

/// <summary>
/// Connects a WPF window to an <see cref="IWindowBackdrop"/>: the backdrop blurs what lies behind one glass element,
/// and <see cref="Backdrop.IsBlurred"/> on the window tells its styles whether to draw that glass translucent or opaque.
/// </summary>
internal sealed class BackdropHost
{
    private readonly Window _window;
    private readonly FrameworkElement _glass;
    private readonly Func<Size, Size> _cornerRadii;
    private readonly IWindowBackdropFactory _factory;
    private readonly bool _ownsWindow;
    private IWindowBackdrop? _backdrop;

    private BackdropHost(
        Window window, FrameworkElement glass, Func<Size, Size> cornerRadii, IWindowBackdropFactory factory,
        bool ownsWindow)
    {
        _window = window;
        _glass = glass;
        _cornerRadii = cornerRadii;
        _factory = factory;
        _ownsWindow = ownsWindow;

        window.SourceInitialized += OnSourceInitialized;
        window.DpiChanged += (_, _) => MatchItself();
        window.Closed += OnClosed;
        glass.SizeChanged += (_, _) => MatchItself();
    }

    /// <summary>
    /// While set, the blur does not follow the glass's size by itself: whoever set it gives the blur each glass it is to match
    /// (<see cref="Measure"/>, then <see cref="Give"/> when the time has come), so that nothing reaches the blur out of turn.
    /// </summary>
    public bool IsHeld { get; set; }

    /// <summary>
    /// Blurs the backdrop of <paramref name="glass"/> for the lifetime of <paramref name="window"/>. Call it before the
    /// window is shown. The blurred area is the glass element's bounds, with elliptical corners whose radii, in DIPs,
    /// <paramref name="cornerRadii"/> returns for the element's size. It follows the element's size by itself; call
    /// <see cref="MatchGlass"/> after anything else changes how the glass is drawn. A window with more than one piece
    /// of glass gives each its own backdrop; only one of them, the one with <paramref name="ownsWindow"/> set, becomes
    /// the window's owner, which keeps the blur beneath it (the others follow the window's stacking themselves).
    /// </summary>
    public static BackdropHost Attach(
        Window window, FrameworkElement glass, Func<Size, Size> cornerRadii, IWindowBackdropFactory factory,
        bool ownsWindow = true) =>
        new(window, glass, cornerRadii, factory, ownsWindow);

    /// <summary>
    /// Matches the blur to the glass as it is drawn now: its bounds and corners after every render transform between
    /// it and the window, and its opacity combined with its ancestors'. Call it for each frame of an animation that
    /// moves, scales or fades the glass.
    /// </summary>
    public void MatchGlass()
    {
        if (Measure() is { } glass)
        {
            Give(glass);
        }
    }

    /// <summary>
    /// The glass as it is drawn now, for <see cref="Give"/>: what <see cref="MatchGlass"/> does at once, in two steps. Windows moves the blur's
    /// own window sooner than it shows what the glass's window has just drawn, so a glass that changes size quickly keeps what it finds for a
    /// moment before giving it, or the blur would run ahead of it. Null while there is nothing to match.
    /// </summary>
    public BackdropGlass? Measure()
    {
        if (_backdrop is null || PresentationSource.FromVisual(_window)?.CompositionTarget is not { } target)
        {
            return null;
        }

        // A glass whose container is collapsed keeps the size it was last given, but is not drawn: no blur for it.
        var size = IsCollapsed() ? Size.Empty : _glass.RenderSize;
        var toDevice = target.TransformToDevice;
        if (size.IsEmpty)
        {
            return new BackdropGlass(0, default);
        }

        var bounds = _glass.TransformToAncestor(_window).TransformBounds(new Rect(size));
        var radii = _cornerRadii(size);
        var scaleX = size.Width > 0 ? bounds.Width / size.Width : 1;
        var scaleY = size.Height > 0 ? bounds.Height / size.Height : 1;
        return new BackdropGlass(EffectiveOpacity(), new BackdropRegion(
            bounds.X * toDevice.M11, bounds.Y * toDevice.M22,
            bounds.Width * toDevice.M11, bounds.Height * toDevice.M22,
            radii.Width * scaleX * toDevice.M11, radii.Height * scaleY * toDevice.M22));
    }

    private void MatchItself()
    {
        if (!IsHeld)
        {
            MatchGlass();
        }
    }

    /// <summary>Matches the blur to a glass that <see cref="Measure"/> found.</summary>
    public void Give(BackdropGlass glass)
    {
        if (_backdrop is null)
        {
            return;
        }

        if (!glass.Region.IsEmpty)
        {
            _backdrop.SetOpacity(glass.Opacity);
        }

        _backdrop.SetRegion(glass.Region);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var interop = new WindowInteropHelper(_window);
        _backdrop = _factory.Create(interop.Handle);
        if (_backdrop.Handle != 0 && _ownsWindow)
        {
            // An owned window always stays above its owner, which keeps the backdrop beneath this window.
            interop.Owner = _backdrop.Handle;
        }

        _backdrop.IsBlurredChanged += OnIsBlurredChanged;
        Backdrop.SetIsBlurred(_window, _backdrop.IsBlurred);
        MatchGlass();
    }

    private void OnIsBlurredChanged(object? sender, EventArgs e) =>
        Backdrop.SetIsBlurred(_window, _backdrop?.IsBlurred == true);

    // Whether the glass or anything above it, up to the window, is collapsed.
    private bool IsCollapsed()
    {
        for (DependencyObject? element = _glass; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is UIElement { Visibility: Visibility.Collapsed })
            {
                return true;
            }

            if (ReferenceEquals(element, _window))
            {
                break;
            }
        }

        return false;
    }

    // The glass's opacity, as the opacities of it and every ancestor up to the window multiply together.
    private double EffectiveOpacity()
    {
        var opacity = 1.0;
        for (DependencyObject? element = _glass; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is UIElement visual)
            {
                opacity *= visual.Opacity;
            }

            if (ReferenceEquals(element, _window))
            {
                break;
            }
        }

        return opacity;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_backdrop is not null)
        {
            _backdrop.IsBlurredChanged -= OnIsBlurredChanged;
            _backdrop.Dispose();
            _backdrop = null;
        }
    }
}
