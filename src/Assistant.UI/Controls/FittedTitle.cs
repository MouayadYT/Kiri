using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// A title that fits its width in at most <see cref="MaxLines"/> lines, as the History window's conversation cards do
/// in its reference: in large type when it fits, and otherwise in smaller type, cut short with an ellipsis if it still
/// does not. A word too long for a line of its own is hyphenated. Its typeface, weight and color are inherited.
/// </summary>
public sealed class FittedTitle : FrameworkElement
{
    /// <summary>Identifies the <see cref="Text"/> property.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FittedTitle),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextChanged));

    /// <summary>Identifies the <see cref="LargeFontSize"/> property.</summary>
    public static readonly DependencyProperty LargeFontSizeProperty = Register(nameof(LargeFontSize), 18);

    /// <summary>Identifies the <see cref="LargeLineHeight"/> property.</summary>
    public static readonly DependencyProperty LargeLineHeightProperty = Register(nameof(LargeLineHeight), 18);

    /// <summary>Identifies the <see cref="SmallFontSize"/> property.</summary>
    public static readonly DependencyProperty SmallFontSizeProperty = Register(nameof(SmallFontSize), 14);

    /// <summary>Identifies the <see cref="SmallLineHeight"/> property.</summary>
    public static readonly DependencyProperty SmallLineHeightProperty = Register(nameof(SmallLineHeight), 14);

    private static readonly DependencyPropertyKey IsReducedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsReduced), typeof(bool), typeof(FittedTitle), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="IsReduced"/> property.</summary>
    public static readonly DependencyProperty IsReducedProperty = IsReducedPropertyKey.DependencyProperty;

    /// <summary>Identifies the <see cref="MaxLines"/> property.</summary>
    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines), typeof(int), typeof(FittedTitle),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is int and > 0);

    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    public FittedTitle()
    {
        AddVisualChild(_text);
        AddLogicalChild(_text);
    }

    /// <summary>The title.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The type size while the title fits in <see cref="MaxLines"/> lines of it.</summary>
    public double LargeFontSize
    {
        get => (double)GetValue(LargeFontSizeProperty);
        set => SetValue(LargeFontSizeProperty, value);
    }

    /// <summary>How far apart lines of the large type are.</summary>
    public double LargeLineHeight
    {
        get => (double)GetValue(LargeLineHeightProperty);
        set => SetValue(LargeLineHeightProperty, value);
    }

    /// <summary>The type size of a title too long for the large type.</summary>
    public double SmallFontSize
    {
        get => (double)GetValue(SmallFontSizeProperty);
        set => SetValue(SmallFontSizeProperty, value);
    }

    /// <summary>How far apart lines of the small type are.</summary>
    public double SmallLineHeight
    {
        get => (double)GetValue(SmallLineHeightProperty);
        set => SetValue(SmallLineHeightProperty, value);
    }

    /// <summary>The most lines the title takes.</summary>
    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    /// <summary>Whether the title is in the small type, having not fitted in the large.</summary>
    public bool IsReduced
    {
        get => (bool)GetValue(IsReducedProperty);
        private set => SetValue(IsReducedPropertyKey, value);
    }

    /// <summary>The text element that draws the title.</summary>
    internal TextBlock TextBlock => _text;

    /// <inheritdoc/>
    protected override int VisualChildrenCount => 1;

    /// <inheritdoc/>
    protected override System.Collections.IEnumerator LogicalChildren => new[] { _text }.GetEnumerator();

    /// <inheritdoc/>
    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _text : throw new ArgumentOutOfRangeException(nameof(index));

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = new Size(availableSize.Width, double.PositiveInfinity);
        Use(LargeFontSize, LargeLineHeight, double.PositiveInfinity, availableSize.Width);
        _text.Measure(width);

        // Lines of the large type are LargeLineHeight each; a title that needs more of them than allowed is reduced.
        var reduced = _text.DesiredSize.Height > (MaxLines * LargeLineHeight) + 0.5;
        IsReduced = reduced;
        if (reduced)
        {
            Use(SmallFontSize, SmallLineHeight, MaxLines * SmallLineHeight, availableSize.Width);
            _text.Measure(width);
        }

        return _text.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _text.Arrange(new Rect(finalSize));
        return finalSize;
    }

    // Sets the type and, as the reference does, hyphenates only a word too long for a line of its own.
    private void Use(double fontSize, double lineHeight, double maxHeight, double width)
    {
        _text.FontSize = fontSize;
        _text.LineHeight = lineHeight;
        _text.MaxHeight = maxHeight;
        _text.IsHyphenationEnabled = HasWordWiderThan(width);
    }

    private bool HasWordWiderThan(double width)
    {
        if (double.IsInfinity(width))
        {
            return false;
        }

        var typeface = new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(word =>
            new FormattedText(word, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, _text.FontSize,
                Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace > width);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FittedTitle)d)._text.Text = (string)e.NewValue ?? "";

    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(
        name, typeof(double), typeof(FittedTitle),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsMeasure),
        size => size is double length && length > 0 && double.IsFinite(length));
}
