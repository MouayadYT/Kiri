using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Assistant.UI.Controls;

/// <summary>
/// Turns a resource key into the data template that the application's theme has under it, so a view model can name a
/// glyph (for example <c>Glyph.Files</c>) without holding any WPF object. A key the theme does not have, or none, gives
/// an empty template that draws nothing.
/// </summary>
public sealed class GlyphTemplateConverter : IValueConverter
{
    private static readonly DataTemplate Empty = new();

    /// <summary>The one converter, for use with <c>{x:Static}</c>.</summary>
    public static GlyphTemplateConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryFindResource(key) is DataTemplate template ? template : Empty;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
