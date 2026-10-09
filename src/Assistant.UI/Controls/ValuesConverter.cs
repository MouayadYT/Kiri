using System.Globalization;
using System.Windows.Data;

namespace Assistant.UI.Controls;

/// <summary>Gives the values of several bindings as one list, in their order, for a template that draws from more than one of its host's properties.</summary>
public sealed class ValuesConverter : IMultiValueConverter
{
    /// <inheritdoc/>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.ToArray();

    /// <inheritdoc/>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
