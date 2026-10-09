using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Assistant.Core.Domain;

namespace Assistant.UI.Controls;

/// <summary>
/// Shows text with the words a search matched picked out (PROJECT_SPEC §4.3): set <c>SearchHighlight.Text</c> on a
/// <see cref="TextBlock"/> instead of its <see cref="TextBlock.Text"/>, and <c>SearchHighlight.Matches</c> to where the
/// matches are. With none, it is plain text. The block's own font, size and color are what the rest uses, and the matches
/// are set in bolder type and the bright text color.
/// </summary>
public static class SearchHighlight
{
    /// <summary>The text to show.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SearchHighlight), new PropertyMetadata(string.Empty, OnChanged));

    /// <summary>Where the matches are in <see cref="TextProperty"/>: a start and a length each.</summary>
    public static readonly DependencyProperty MatchesProperty = DependencyProperty.RegisterAttached(
        "Matches", typeof(IReadOnlyList<TextMatch>), typeof(SearchHighlight), new PropertyMetadata(null, OnChanged));

    /// <summary>Gets the text <paramref name="block"/> shows.</summary>
    public static string GetText(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return (string)block.GetValue(TextProperty);
    }

    /// <summary>Sets the text <paramref name="block"/> shows.</summary>
    public static void SetText(TextBlock block, string? value)
    {
        ArgumentNullException.ThrowIfNull(block);
        block.SetValue(TextProperty, value ?? string.Empty);
    }

    /// <summary>Gets where the matches are in the text of <paramref name="block"/>.</summary>
    public static IReadOnlyList<TextMatch>? GetMatches(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return (IReadOnlyList<TextMatch>?)block.GetValue(MatchesProperty);
    }

    /// <summary>Sets where the matches are in the text of <paramref name="block"/>.</summary>
    public static void SetMatches(TextBlock block, IReadOnlyList<TextMatch>? value)
    {
        ArgumentNullException.ThrowIfNull(block);
        block.SetValue(MatchesProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var text = GetText(block);

        // Text is what assistive technology reads, and it does not follow the runs by itself: it is set to the plain words
        // first, which makes them the block's content, and the runs then take that place.
        block.SetCurrentValue(TextBlock.TextProperty, text);
        block.Inlines.Clear();

        var position = 0;
        foreach (var match in GetMatches(block) ?? [])
        {
            // Matches that do not lie inside the text, or overlap the one before, are ignored.
            if (match.Start < position || match.Length <= 0 || match.End > text.Length)
            {
                continue;
            }

            Add(block, text[position..match.Start], highlighted: false);
            Add(block, text.Substring(match.Start, match.Length), highlighted: true);
            position = match.End;
        }

        Add(block, text[position..], highlighted: false);
    }

    private static void Add(TextBlock block, string text, bool highlighted)
    {
        if (text.Length == 0)
        {
            return;
        }

        var run = new Run(text);
        if (highlighted)
        {
            run.FontWeight = FontWeights.SemiBold;
            if (block.TryFindResource("Brush.Text.Message") is Brush brush)
            {
                run.Foreground = brush;
            }
        }

        block.Inlines.Add(run);
    }
}
