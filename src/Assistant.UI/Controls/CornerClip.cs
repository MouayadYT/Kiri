using System.Windows;

namespace Assistant.UI.Controls;

/// <summary>
/// Clips an element to the smooth corners of a <see cref="PanelShape"/> of its own size, so content such as a
/// gallery's images is cut off by the same outline as a card.
/// </summary>
public static class CornerClip
{
    /// <summary>Identifies the CornerSize attached property: how far each corner reaches along the edges, in DIPs.</summary>
    public static readonly DependencyProperty CornerSizeProperty = DependencyProperty.RegisterAttached(
        "CornerSize", typeof(double), typeof(CornerClip), new PropertyMetadata(double.NaN, OnCornerSizeChanged));

    /// <summary>Gets how far the element's clipped corners reach, or NaN when it is not clipped.</summary>
    public static double GetCornerSize(FrameworkElement element) => (double)element.GetValue(CornerSizeProperty);

    /// <summary>Clips the element to corners that reach <paramref name="value"/> DIPs along its edges.</summary>
    public static void SetCornerSize(FrameworkElement element, double value) => element.SetValue(CornerSizeProperty, value);

    private static void OnCornerSizeChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        if (double.IsNaN((double)e.NewValue))
        {
            element.ClearValue(UIElement.ClipProperty);
            return;
        }

        element.SizeChanged += OnSizeChanged;
        Update(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element) =>
        element.Clip = PanelShape.CreateGeometry(new Rect(element.RenderSize), Math.Max(0, GetCornerSize(element)));
}
