using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Assistant.UI.Controls;

/// <summary>Turns a list item's nesting depth into its left margin: <see cref="Indent"/> DIPs per level.</summary>
public sealed class ListIndentConverter : IValueConverter
{
    /// <summary>How far each level of nesting moves an item to the right, in DIPs.</summary>
    public double Indent { get; set; }

    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int depth and > 0 ? new Thickness(depth * Indent, 0, 0, 0) : new Thickness();

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
