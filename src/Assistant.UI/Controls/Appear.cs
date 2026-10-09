using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Assistant.UI.Controls;

/// <summary>
/// How a part of an answer comes into the conversation when it is new (the message reference): out of a blur, from clear to solid, rising a few
/// pixels into its place. Parts that belong together come one after another (<see cref="OrderProperty"/>): the words, then the card, then the
/// button under it. Nothing is animated where Windows has animations turned off, and what is drawn at rest is always the finished part, so a part
/// that is drawn again (the conversation is opened later, or scrolled back into view) simply stands there.
/// </summary>
public static class Appear
{
    /// <summary>Whether parts animate at all. Tests that draw a part the moment it is made turn it off, so that what they draw is the part at rest.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>How long a part takes to come in.</summary>
    public static readonly Duration Time = new(TimeSpan.FromMilliseconds(340));

    /// <summary>How long after the part before it a part begins.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(110);

    /// <summary>
    /// The part's place among the parts that come in together: 0 comes first, 1 a step later. Setting it is what makes the part come in when it is
    /// first shown; a part without it just stands there.
    /// </summary>
    public static readonly DependencyProperty OrderProperty = DependencyProperty.RegisterAttached(
        "Order", typeof(int), typeof(Appear), new PropertyMetadata(-1, OnOrderChanged));

    /// <summary>
    /// Raised, bubbling, when a part that comes in is first shown, whether or not it is animated: the conversation that holds it can bring it into view
    /// the way the reference does (a message that was sent rises to the top, with what led to it above, a scroll away).
    /// </summary>
    public static readonly RoutedEvent ArrivedEvent = EventManager.RegisterRoutedEvent(
        "Arrived", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(Appear));

    /// <summary>Gets the part's place.</summary>
    public static int GetOrder(DependencyObject element) => (int)element.GetValue(OrderProperty);

    /// <summary>Sets the part's place.</summary>
    public static void SetOrder(DependencyObject element, int value) => element.SetValue(OrderProperty, value);

    private static void OnOrderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && (int)e.NewValue >= 0)
        {
            element.Loaded -= OnLoaded;
            element.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;

        // Once: a part that is taken out of the tree and put back (a list that redraws) does not come in again.
        element.Loaded -= OnLoaded;
        element.RaiseEvent(new RoutedEventArgs(ArrivedEvent, element));
        if (!Enabled || !SystemParameters.ClientAreaAnimation || !element.IsVisible)
        {
            return;
        }

        var begin = TimeSpan.FromTicks(Step.Ticks * GetOrder(element));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // The part is clear until its turn comes, and is left solid, with no animation holding it, once it is in.
        var fade = new DoubleAnimation(0, 1, Time) { BeginTime = begin, EasingFunction = ease };
        element.Opacity = 0;
        fade.Completed += (_, _) =>
        {
            element.Opacity = 1;
            element.BeginAnimation(UIElement.OpacityProperty, null);
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        var rise = new TranslateTransform(0, 7);
        element.RenderTransform = rise;
        var slide = new DoubleAnimation(7, 0, Time) { BeginTime = begin, EasingFunction = ease };
        slide.Completed += (_, _) => element.RenderTransform = Transform.Identity;
        rise.BeginAnimation(TranslateTransform.YProperty, slide);

        var blur = new BlurEffect { Radius = 12, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        element.Effect = blur;
        var sharpen = new DoubleAnimation(12, 0, Time) { BeginTime = begin, EasingFunction = ease };
        sharpen.Completed += (_, _) => element.Effect = null;
        blur.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
    }
}
