using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Assistant.Windows.Input;

namespace Assistant.UI.Controls;

/// <summary>
/// Keeps a <see cref="GlowCaret"/> on a text box's insertion point in place of its own caret, which the text box keeps
/// but draws transparent. The glow caret follows typing, clicks, arrow keys and scrolling; shows only while the text
/// box has keyboard focus and nothing is selected; and blinks at the user's caret blink rate, steady while it moves.
/// </summary>
internal sealed class CaretTracker : IDisposable
{
    // Blinking fades rather than snaps, unless Windows animation effects are off.
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(110);

    private readonly TextBox _editor;
    private readonly GlowCaret _caret;
    private readonly Func<TimeSpan?> _blinkInterval;
    private readonly Func<bool> _animationsEnabled;
    private readonly DispatcherTimer _blink;
    private bool _followingLayout;
    private bool _on = true;

    public CaretTracker(TextBox editor, GlowCaret caret)
        : this(editor, caret, () => CaretBlink.Interval, () => SystemParameters.ClientAreaAnimation)
    {
    }

    internal CaretTracker(TextBox editor, GlowCaret caret, Func<TimeSpan?> blinkInterval, Func<bool> animationsEnabled)
    {
        _editor = editor;
        _caret = caret;
        _blinkInterval = blinkInterval;
        _animationsEnabled = animationsEnabled;
        _blink = new DispatcherTimer(DispatcherPriority.Normal, editor.Dispatcher);
        _blink.Tick += (_, _) => Blink();

        editor.GotKeyboardFocus += OnFocusChanged;
        editor.LostKeyboardFocus += OnFocusChanged;
        editor.IsEnabledChanged += OnEnabledOrVisibleChanged;
        editor.IsVisibleChanged += OnEnabledOrVisibleChanged;
        editor.SelectionChanged += OnMoved;
        editor.TextChanged += OnMoved;
        editor.SizeChanged += OnStateChanged;
        editor.Unloaded += OnUnloaded;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrolled));
        caret.Visibility = Visibility.Hidden;
        Update(restartBlink: true);
    }

    /// <summary>Whether the glow caret currently shows at the insertion point.</summary>
    internal bool IsShown => _caret.Visibility == Visibility.Visible;

    /// <summary>Whether the caret is in the "on" half of its blink.</summary>
    internal bool IsBlinkOn => _on;

    /// <summary>Runs one blink step at once, as the blink timer does.</summary>
    internal void Blink()
    {
        _on = !_on;
        SetOpacity(_on ? 1 : 0, animate: true);
    }

    public void Dispose()
    {
        _blink.Stop();
        FollowLayout(false);
        _editor.GotKeyboardFocus -= OnFocusChanged;
        _editor.LostKeyboardFocus -= OnFocusChanged;
        _editor.IsEnabledChanged -= OnEnabledOrVisibleChanged;
        _editor.IsVisibleChanged -= OnEnabledOrVisibleChanged;
        _editor.SelectionChanged -= OnMoved;
        _editor.TextChanged -= OnMoved;
        _editor.SizeChanged -= OnStateChanged;
        _editor.Unloaded -= OnUnloaded;
        _editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrolled));
        _caret.Visibility = Visibility.Hidden;
    }

    private void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => Update(restartBlink: true);

    private void OnStateChanged(object sender, EventArgs e) => Update(restartBlink: false);

    private void OnEnabledOrVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update(restartBlink: true);

    private void OnMoved(object sender, RoutedEventArgs e) => Update(restartBlink: true);

    private void OnScrolled(object sender, ScrollChangedEventArgs e) => Update(restartBlink: false);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        FollowLayout(false);
        _blink.Stop();
    }

    // After text changes, the insertion point is only known once the text is laid out again, which happens before it
    // is drawn: following layout keeps the caret on the same frame as the text.
    private void OnLayoutUpdated(object? sender, EventArgs e) => Update(restartBlink: false);

    private void Update(bool restartBlink)
    {
        var active = _editor.IsKeyboardFocused && _editor.IsEnabled && _editor.IsVisible;
        FollowLayout(active);
        if (!active || _editor.SelectionLength > 0 || !TryPlace())
        {
            _blink.Stop();
            _caret.Visibility = Visibility.Hidden;
            return;
        }

        _caret.Visibility = Visibility.Visible;
        if (restartBlink || !_blink.IsEnabled)
        {
            // A caret that moves stays lit, and starts blinking again from there.
            _on = true;
            SetOpacity(1, animate: false);
            _blink.Stop();
            if (_blinkInterval() is { } interval)
            {
                _blink.Interval = interval;
                _blink.Start();
            }
        }
    }

    // Puts the caret on the insertion point, which must be inside the text box's visible text.
    private bool TryPlace()
    {
        if (VisualTreeHelper.GetParent(_caret) is not UIElement host || !_editor.IsMeasureValid)
        {
            return false;
        }

        Rect rect;
        try
        {
            rect = _editor.GetRectFromCharacterIndex(_editor.CaretIndex);
        }
        catch (InvalidOperationException)
        {
            // The text is mid-layout; the next layout pass places the caret.
            return false;
        }

        if (rect.IsEmpty || rect.Height <= 0 || rect.Bottom < 0 || rect.Top > _editor.ActualHeight ||
            rect.X < -0.5 || rect.X > _editor.ActualWidth + 0.5)
        {
            return false;
        }

        var origin = _editor.TranslatePoint(rect.TopLeft, host);
        _caret.Height = rect.Height;
        Canvas.SetLeft(_caret, origin.X - (_caret.Width / 2));
        Canvas.SetTop(_caret, origin.Y);
        return true;
    }

    private void SetOpacity(double opacity, bool animate)
    {
        if (animate && _animationsEnabled())
        {
            _caret.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, FadeDuration));
        }
        else
        {
            _caret.BeginAnimation(UIElement.OpacityProperty, null);
            _caret.Opacity = opacity;
        }
    }

    private void FollowLayout(bool follow)
    {
        if (follow != _followingLayout)
        {
            _followingLayout = follow;
            if (follow) _editor.LayoutUpdated += OnLayoutUpdated;
            else _editor.LayoutUpdated -= OnLayoutUpdated;
        }
    }
}
