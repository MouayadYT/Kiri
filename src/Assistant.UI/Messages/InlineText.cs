using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Assistant.UI.Messages;

/// <summary>
/// Shows a block's text with its Markdown emphasis (PROJECT_SPEC §4.2): set <c>InlineText.Markdown</c> on a
/// <see cref="TextBlock"/> instead of its <see cref="TextBlock.Text"/>, and it holds a run for each style that
/// <see cref="InlineMarkdown"/> reads, and a link for each address. The block's own font, size and color are what the
/// plain words use.
/// </summary>
public static class InlineText
{
    /// <summary>The text to show, with Markdown emphasis; the block's <see cref="TextBlock.Inlines"/> follow it.</summary>
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown", typeof(string), typeof(InlineText), new PropertyMetadata(null, OnMarkdownChanged));

    // What a click on a link does. Only web and mail addresses get this far (InlineMarkdown).
    private static Action<Uri> _openLink = OpenInShell;

    /// <summary>Gets the text <paramref name="block"/> shows.</summary>
    public static string? GetMarkdown(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return (string?)block.GetValue(MarkdownProperty);
    }

    /// <summary>Sets the text <paramref name="block"/> shows.</summary>
    public static void SetMarkdown(TextBlock block, string? value)
    {
        ArgumentNullException.ThrowIfNull(block);
        block.SetValue(MarkdownProperty, value);
    }

    /// <summary>Replaces what a click on a link does, for tests; returns what puts the default back.</summary>
    internal static IDisposable OverrideLinkOpener(Action<Uri> opener)
    {
        var previous = _openLink;
        _openLink = opener;
        return new Restore(() => _openLink = previous);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var spans = InlineMarkdown.Parse((string?)e.NewValue ?? string.Empty);

        // Text is what assistive technology reads, and it does not follow the runs by itself: it is set to the plain words
        // first, which makes them the block's content, and the runs then take that place.
        block.SetCurrentValue(TextBlock.TextProperty, string.Concat(spans.Select(span => span.Text)));
        block.Inlines.Clear();
        foreach (var span in spans)
        {
            block.Inlines.Add(Create(block, span));
        }
    }

    private static Inline Create(TextBlock block, InlineSpan span)
    {
        var run = new Run(span.Text);
        if (span.Style.HasFlag(InlineStyle.Bold))
        {
            run.FontWeight = FontWeights.Bold;
        }

        if (span.Style.HasFlag(InlineStyle.Italic))
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (span.Style.HasFlag(InlineStyle.Strikethrough))
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }

        if (span.Style.HasFlag(InlineStyle.Code))
        {
            run.FontFamily = Find(block, "Font.Code") as FontFamily ?? new FontFamily("Consolas");
            run.Background = Find(block, "Brush.Surface.InlineCode") as Brush ?? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            run.Foreground = Find(block, "Brush.Text.Code") as Brush ?? run.Foreground;
        }

        if (span.Url is null || !Uri.TryCreate(span.Url, UriKind.Absolute, out var uri))
        {
            return run;
        }

        var link = new Hyperlink(run) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        if (Find(block, "Brush.Text.Link") is Brush brush)
        {
            link.Foreground = brush;
        }

        link.RequestNavigate += OnRequestNavigate;
        return link;
    }

    private static object? Find(FrameworkElement element, string key) => element.TryFindResource(key);

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        _openLink(e.Uri);
    }

    // The user clicked the address: the system opens it with whatever handles it.
    private static void OpenInShell(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Nothing handles it, or it could not be started: the link does nothing.
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
