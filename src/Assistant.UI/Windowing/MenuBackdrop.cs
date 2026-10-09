using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Assistant.Windows.Backdrop;

namespace Assistant.UI.Windowing;

/// <summary>
/// Blurs what lies behind a context menu's glass while the menu is open, as <see cref="BackdropHost"/> does for a
/// window, and tells the menu's styles through <see cref="Backdrop.IsBlurred"/> whether to draw that glass translucent
/// or opaque (PROJECT_SPEC §4.0). The backdrop is created each time the menu opens, for the popup window it opens in,
/// follows the glass while the popup fades in and out, and is released once the menu has closed.
/// </summary>
internal sealed class MenuBackdrop
{
    /// <summary>Identifies the HasBackdrop attached property.</summary>
    public static readonly DependencyProperty HasBackdropProperty = DependencyProperty.RegisterAttached(
        "HasBackdrop", typeof(bool), typeof(MenuBackdrop), new PropertyMetadata(false));

    // Longer than any popup fade Windows' menu animations use; the blur stops following the glass after it.
    private static readonly TimeSpan FadeLimit = TimeSpan.FromSeconds(1);

    private readonly ContextMenu _menu;
    private readonly string _glassName;
    private readonly Func<Size, Size> _cornerRadii;
    private readonly IWindowBackdropFactory _factory;
    private IWindowBackdrop? _backdrop;
    private HwndSource? _source;
    private FrameworkElement? _glass;
    private bool _following;
    private long _followingSince;

    private MenuBackdrop(ContextMenu menu, string glassName, Func<Size, Size> cornerRadii, IWindowBackdropFactory factory)
    {
        _menu = menu;
        _glassName = glassName;
        _cornerRadii = cornerRadii;
        _factory = factory;
        SetHasBackdrop(menu, true);
        menu.Opened += OnOpened;
        menu.Closed += OnClosed;
        DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu))
            .AddValueChanged(menu, OnIsOpenChanged);
    }

    /// <summary>
    /// Whether a blurred backdrop is put behind the menu's glass when it opens (<see cref="Attach"/>). Translucent glass with
    /// nothing blurred behind it is only a faint tint over what lies under it, so a menu without a backdrop keeps its
    /// glass opaque even where the window it is in is blurred, which a menu inherits.
    /// </summary>
    public static bool GetHasBackdrop(DependencyObject element) => (bool)element.GetValue(HasBackdropProperty);

    /// <summary>Sets <see cref="GetHasBackdrop"/>.</summary>
    internal static void SetHasBackdrop(DependencyObject element, bool value) => element.SetValue(HasBackdropProperty, value);

    /// <summary>
    /// Blurs the backdrop of the element named <paramref name="glassName"/> in <paramref name="menu"/>'s template
    /// whenever the menu is open. The blurred area is the element's bounds, with elliptical corners whose radii, in
    /// DIPs, <paramref name="cornerRadii"/> returns for the element's size.
    /// </summary>
    public static MenuBackdrop Attach(
        ContextMenu menu, string glassName, Func<Size, Size> cornerRadii, IWindowBackdropFactory factory) =>
        new(menu, glassName, cornerRadii, factory);

    private void OnOpened(object sender, RoutedEventArgs e)
    {
        Release();
        if (PresentationSource.FromVisual(_menu) is not HwndSource source)
        {
            return;
        }

        _source = source;
        _glass = _menu.Template?.FindName(_glassName, _menu) as FrameworkElement;
        _backdrop = _factory.Create(source.Handle);
        _backdrop.IsBlurredChanged += OnIsBlurredChanged;
        Backdrop.SetIsBlurred(_menu, _backdrop.IsBlurred);
        MatchGlass();

        // The popup may fade in; the blur fades with it.
        Follow();
    }

    // The popup may fade out before it closes; the blur fades with it.
    private void OnIsOpenChanged(object? sender, EventArgs e)
    {
        if (!_menu.IsOpen && _backdrop is not null)
        {
            Follow();
        }
    }

    private void OnClosed(object sender, RoutedEventArgs e) => Release();

    private void Follow()
    {
        _followingSince = Environment.TickCount64;
        if (!_following)
        {
            _following = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    // Opening, the blur follows the glass until it is fully shown; closing, until the popup has gone.
    private void OnRendering(object? sender, EventArgs e)
    {
        var opacity = MatchGlass();
        if ((_menu.IsOpen && opacity >= 1)
            || TimeSpan.FromMilliseconds(Environment.TickCount64 - _followingSince) > FadeLimit)
        {
            StopFollowing();
        }
    }

    private void OnIsBlurredChanged(object? sender, EventArgs e) =>
        Backdrop.SetIsBlurred(_menu, _backdrop?.IsBlurred == true);

    // Matches the blur to the glass as drawn now, and returns the glass's opacity.
    private double MatchGlass()
    {
        if (_backdrop is null || _glass is null || _source?.RootVisual is not Visual root
            || _source.CompositionTarget is not { } target || !_glass.IsDescendantOf(root))
        {
            return 1;
        }

        var size = _glass.RenderSize;
        var toDevice = target.TransformToDevice;
        var bounds = _glass.TransformToAncestor(root).TransformBounds(new Rect(size));
        var radii = _cornerRadii(size);
        var opacity = EffectiveOpacity(root);
        _backdrop.SetOpacity(opacity);
        _backdrop.SetRegion(new BackdropRegion(
            bounds.X * toDevice.M11, bounds.Y * toDevice.M22,
            bounds.Width * toDevice.M11, bounds.Height * toDevice.M22,
            radii.Width * toDevice.M11, radii.Height * toDevice.M22));
        return opacity;
    }

    // The glass's opacity, as the opacities of it and every ancestor up to the popup's root multiply together.
    private double EffectiveOpacity(Visual root)
    {
        var opacity = 1.0;
        for (DependencyObject? element = _glass; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is UIElement visual)
            {
                opacity *= visual.Opacity;
            }

            if (ReferenceEquals(element, root))
            {
                break;
            }
        }

        return opacity;
    }

    private void StopFollowing()
    {
        if (_following)
        {
            _following = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void Release()
    {
        StopFollowing();
        if (_backdrop is not null)
        {
            _backdrop.IsBlurredChanged -= OnIsBlurredChanged;
            _backdrop.Dispose();
            _backdrop = null;
        }

        _source = null;
        _glass = null;
    }
}
