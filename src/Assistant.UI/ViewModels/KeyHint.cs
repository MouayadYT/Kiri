namespace Assistant.UI.ViewModels;

/// <summary>
/// The keys that run a command, as a row draws them at its right edge: a glyph for the modifier key followed by the
/// key itself, such as the Command key's symbol and <c>1</c>.
/// </summary>
/// <param name="ModifierGlyphKey">
/// The key, in the theme's resources, of the data template that draws the modifier (for example <c>Glyph.CommandKey</c>
/// in <c>Themes/Controls/Launcher.xaml</c>).
/// </param>
/// <param name="Key">The key pressed with it, as written.</param>
public sealed record KeyHint(string ModifierGlyphKey, string Key);
