using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Assistant.UI.Messages;

namespace Assistant.UI.Controls;

/// <summary>
/// Puts a part of an answer away and brings it back, smoothly: the part closes upward into nothing (what is under it comes up as it goes) and opens the
/// same way. An answer's workings (its steps, a question once it is answered) fold away where they stood and are found again behind the button with
/// three dots under the answer, which folds them open and closed. A part that is closed takes no room and is not drawn at all.
/// </summary>
/// <remarks>
/// The part keeps its own size while it folds: the room it takes is made smaller with a margin and what shows of it is cut with a clip, so a card that
/// reaches past the text column keeps its sides to the last. Nothing is animated where Windows has animations turned off, before the part is on
/// screen, or where <see cref="Appear.Enabled"/> is off (tests): the part is then simply open or closed.
/// </remarks>
public static class Fold
{
    /// <summary>How long a part takes to fold.</summary>
    public static readonly Duration Time = new(TimeSpan.FromMilliseconds(280));

    // How far past its own sides a folding part is still drawn: a card's reach past the text column.
    private const double Reach = 64;

    /// <summary>Whether the part is open. Changing it while the part is on screen folds it; before that it is just so.</summary>
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(Fold), new PropertyMetadata(true, OnIsOpenChanged));

    /// <summary>How much of the part shows, from 0 (closed) to 1 (open). The panel that stacks the parts closes the gap before it by as much.</summary>
    public static readonly DependencyProperty AmountProperty = DependencyProperty.RegisterAttached(
        "Amount", typeof(double), typeof(Fold), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure, OnAmountChanged));

    /// <summary>
    /// Whether the part is open exactly while what it shows (its <see cref="FrameworkElement.DataContext"/>, a <see cref="MessageContent"/>) is not
    /// put away (<see cref="MessageContent.IsTucked"/>): set on the place of each part of an answer.
    /// </summary>
    public static readonly DependencyProperty FollowsTuckedProperty = DependencyProperty.RegisterAttached(
        "FollowsTucked", typeof(bool), typeof(Fold), new PropertyMetadata(false, OnFollowsTuckedChanged));

    private static readonly DependencyProperty FoldingProperty = DependencyProperty.RegisterAttached(
        "Folding", typeof(Folding), typeof(Fold), new PropertyMetadata(null));

    private static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "Watch", typeof(Watch), typeof(Fold), new PropertyMetadata(null));

    /// <summary>Gets whether the part is open.</summary>
    public static bool GetIsOpen(DependencyObject element) => (bool)element.GetValue(IsOpenProperty);

    /// <summary>Sets whether the part is open.</summary>
    public static void SetIsOpen(DependencyObject element, bool value) => element.SetValue(IsOpenProperty, value);

    /// <summary>Gets how much of the part shows.</summary>
    public static double GetAmount(DependencyObject element) => (double)element.GetValue(AmountProperty);

    /// <summary>Sets how much of the part shows.</summary>
    public static void SetAmount(DependencyObject element, double value) => element.SetValue(AmountProperty, value);

    /// <summary>Gets whether the part follows what it shows.</summary>
    public static bool GetFollowsTucked(DependencyObject element) => (bool)element.GetValue(FollowsTuckedProperty);

    /// <summary>Sets whether the part follows what it shows.</summary>
    public static void SetFollowsTucked(DependencyObject element, bool value) => element.SetValue(FollowsTuckedProperty, value);

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var open = (bool)e.NewValue;
        if (!element.IsLoaded || !Appear.Enabled || !SystemParameters.ClientAreaAnimation || PresentationSource.FromVisual(element) is null)
        {
            Settle(element, open);
            return;
        }

        var folding = (Folding?)element.GetValue(FoldingProperty);
        if (folding is null)
        {
            folding = new Folding(element.ReadLocalValue(FrameworkElement.MarginProperty), element.Margin);
            element.SetValue(FoldingProperty, folding);
            if (open)
            {
                // It was closed and takes its place again: it is laid out whole before it is drawn, so that how tall it is is known from the first frame.
                element.Visibility = Visibility.Visible;
                element.UpdateLayout();
                SetAmount(element, 0);
                Show(element, 0);
            }
        }

        var run = ++folding.Run;
        var from = GetAmount(element);
        var animation = new DoubleAnimation(from, open ? 1 : 0, Time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        animation.Completed += (_, _) =>
        {
            if (element.GetValue(FoldingProperty) is Folding current && current.Run == run)
            {
                Settle(element, open);
            }
        };
        element.BeginAnimation(AmountProperty, animation);
    }

    // The part at rest: open as it is drawn without any of this, or closed and gone.
    private static void Settle(FrameworkElement element, bool open)
    {
        var folding = (Folding?)element.GetValue(FoldingProperty);
        element.ClearValue(FoldingProperty);
        element.BeginAnimation(AmountProperty, null);
        SetAmount(element, open ? 1 : 0);
        if (folding is not null)
        {
            if (folding.LocalMargin == DependencyProperty.UnsetValue)
            {
                element.ClearValue(FrameworkElement.MarginProperty);
            }
            else
            {
                element.Margin = folding.Margin;
            }

            element.Clip = null;
            element.ClearValue(UIElement.OpacityProperty);
        }

        element.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnAmountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && element.GetValue(FoldingProperty) is not null)
        {
            Show(element, (double)e.NewValue);
        }
    }

    // Draws the part as far open as it is: the room it takes ends where its clip does, and it is as solid as it is open.
    private static void Show(FrameworkElement element, double amount)
    {
        if (element.GetValue(FoldingProperty) is not Folding folding)
        {
            return;
        }

        var natural = element.ActualHeight;
        var shown = Math.Max(0, natural * amount);
        var rest = folding.Margin;
        element.Margin = new Thickness(rest.Left, rest.Top * amount, rest.Right, (rest.Bottom * amount) - (natural - shown));
        var clip = new RectangleGeometry(new Rect(-Reach, 0, element.ActualWidth + (2 * Reach), shown));
        clip.Freeze();
        element.Clip = clip;
        element.Opacity = Math.Clamp(amount, 0, 1);
    }

    private static void OnFollowsTuckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.DataContextChanged -= OnContextChanged;
        (element.GetValue(WatchProperty) as Watch)?.Stop();
        element.ClearValue(WatchProperty);
        if ((bool)e.NewValue)
        {
            element.DataContextChanged += OnContextChanged;
            Follow(element);
        }
    }

    private static void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Follow((FrameworkElement)sender);

    private static void Follow(FrameworkElement element)
    {
        (element.GetValue(WatchProperty) as Watch)?.Stop();
        element.ClearValue(WatchProperty);
        if (element.DataContext is not MessageContent content)
        {
            return;
        }

        SetIsOpen(element, !content.IsTucked);
        if (content is INotifyPropertyChanged changing)
        {
            element.SetValue(WatchProperty, new Watch(element, content, changing));
        }
    }

    // What a fold in progress has to put back.
    private sealed class Folding(object localMargin, Thickness margin)
    {
        public object LocalMargin { get; } = localMargin;

        public Thickness Margin { get; } = margin;

        public int Run { get; set; }
    }

    // Follows one part's content. The content is held weakly by the event it listens to, so a part that is gone does not keep its place alive.
    private sealed class Watch
    {
        private readonly WeakReference<FrameworkElement> _element;
        private readonly MessageContent _content;
        private readonly INotifyPropertyChanged _changing;

        public Watch(FrameworkElement element, MessageContent content, INotifyPropertyChanged changing)
        {
            _element = new WeakReference<FrameworkElement>(element);
            _content = content;
            _changing = changing;
            PropertyChangedEventManager.AddHandler(changing, OnChanged, nameof(MessageContent.IsTucked));
        }

        public void Stop() => PropertyChangedEventManager.RemoveHandler(_changing, OnChanged, nameof(MessageContent.IsTucked));

        private void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_element.TryGetTarget(out var element) && ReferenceEquals(element.DataContext, _content))
            {
                SetIsOpen(element, !_content.IsTucked);
            }
        }
    }
}
