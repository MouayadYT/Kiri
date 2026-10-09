using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Assistant.UI.Controls;

/// <summary>Shows an element while a Boolean is false: the opposite of <see cref="BooleanToVisibilityConverter"/>.</summary>
public sealed class InvertedVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
