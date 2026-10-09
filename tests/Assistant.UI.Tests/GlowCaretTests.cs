using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.UI.Controls;
using Assistant.Windows.Input;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void GlowCaretReplacesTheSystemCaretAndFollowsTheInsertionPoint() => RunSta(() =>
    {
        var (window, input) = CreateFocusedInput();
        try
        {
            var editor = Editor(input);
            var caret = Part<GlowCaret>(input, "PART_Caret");
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(editor.CaretBrush).Color);
            Assert.True(input.Caret!.IsShown);
            Assert.Equal(2, caret.Width);
            AssertCaretAt(editor, caret, 0);

            // Typing moves it to the end of the new text.
            editor.Text = "Synthetic text";
            editor.CaretIndex = editor.Text.Length;
            editor.UpdateLayout();
            AssertCaretAt(editor, caret, editor.Text.Length);
            var end = Canvas.GetLeft(caret);

            // Arrow keys and clicks move the insertion point, and the caret with it.
            EditingCommands.MoveLeftByCharacter.Execute(null, editor);
            editor.UpdateLayout();
            Assert.Equal(editor.Text.Length - 1, editor.CaretIndex);
            AssertCaretAt(editor, caret, editor.CaretIndex);
            Assert.True(Canvas.GetLeft(caret) < end);
            editor.CaretIndex = 4;
            editor.UpdateLayout();
            AssertCaretAt(editor, caret, 4);

            // It is a line tall, like the text.
            Assert.Equal(editor.GetRectFromCharacterIndex(4).Height, caret.Height, 3);
            Assert.InRange(caret.Height, 26, 32);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GlowCaretShowsOnlyWithFocusAndNoSelection() => RunSta(() =>
    {
        var (window, input) = CreateFocusedInput();
        var other = new TextBox();
        ((Grid)window.Content).Children.Add(other);
        try
        {
            var editor = Editor(input);
            var caret = Part<GlowCaret>(input, "PART_Caret");
            editor.Text = "Synthetic text";
            editor.Select(0, 4);
            Assert.False(input.Caret!.IsShown);
            Assert.Equal(Visibility.Hidden, caret.Visibility);
            editor.Select(4, 0);
            Assert.True(input.Caret.IsShown);

            other.Focus();
            Assert.False(input.Caret.IsShown);
            input.FocusInput();
            Assert.True(input.Caret.IsShown);
            input.IsEnabled = false;
            Assert.False(input.Caret.IsShown);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GlowCaretBlinksAndStaysLitWhileMoving() => RunSta(() =>
    {
        var (window, input) = CreateFocusedInput();
        try
        {
            var editor = Editor(input);
            var caret = Part<GlowCaret>(input, "PART_Caret");
            var tracker = input.Caret!;
            Assert.True(tracker.IsBlinkOn);
            Assert.Equal(1, caret.Opacity);

            tracker.Blink();
            Assert.False(tracker.IsBlinkOn);
            tracker.Blink();
            Assert.True(tracker.IsBlinkOn);
            tracker.Blink();

            // Typing relights it at once.
            editor.Text = "x";
            Assert.True(tracker.IsBlinkOn);
            Assert.Equal(1, caret.Opacity);

            // The blink rate is the user's, when blinking is on.
            if (CaretBlink.Interval is { } interval) Assert.True(interval > TimeSpan.Zero);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GlowCaretWorksInTheExpandedMultilineStyle() => RunSta(() =>
    {
        var (window, input) = CreateFocusedInput(expanded: true);
        try
        {
            var editor = Editor(input);
            var caret = Part<GlowCaret>(input, "PART_Caret");
            editor.Text = "First line\nSecond line";
            editor.CaretIndex = 3;
            editor.UpdateLayout();
            var firstLine = Canvas.GetTop(caret);
            editor.CaretIndex = editor.Text.Length;
            editor.UpdateLayout();
            AssertCaretAt(editor, caret, editor.Text.Length);
            Assert.True(Canvas.GetTop(caret) > firstLine + 10);
            Assert.InRange(caret.Height, 17, 22);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GlowCaretGlowsBlueAboveAndWarmBelowAndFeathersOut() => RunSta(() =>
    {
        var caret = new GlowCaret
        {
            Width = 2, Height = 30, GlowRadius = 12,
            UpperGlow = Color.FromRgb(0xB0, 0xE2, 0xFF), LowerGlow = Color.FromRgb(0xFF, 0xE1, 0x96),
        };
        var host = new Border { Width = 40, Height = 60, Background = Brushes.Black, Child = new Canvas { Children = { caret } } };
        Canvas.SetLeft(caret, 19);
        Canvas.SetTop(caret, 15);
        host.Measure(new Size(40, 60));
        host.Arrange(new Rect(0, 0, 40, 60));
        var bitmap = new RenderTargetBitmap(40, 60, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new byte[40 * 60 * 4];
        bitmap.CopyPixels(pixels, 40 * 4, 0);
        (int R, int G, int B) At(int x, int y) => (pixels[((y * 40) + x) * 4 + 2], pixels[((y * 40) + x) * 4 + 1], pixels[((y * 40) + x) * 4]);

        // A white core; cool beside its upper part and warm beside its lower part.
        Assert.True(At(20, 30).R > 240 && At(20, 30).B > 240);
        var upper = At(17, 19);
        var lower = At(17, 41);
        Assert.True(upper.B > upper.R + 10, $"upper glow {upper}");
        Assert.True(lower.R > lower.B + 10, $"lower glow {lower}");

        // It fades step by step to nothing, with no hard edge, to the sides and past the ends.
        var row = Enumerable.Range(0, 18).Select(d => At(18 - d, 30).G).ToArray();
        for (var d = 1; d < row.Length; d++)
        {
            Assert.True(row[d] <= row[d - 1], $"glow brightens outward at {d}");
            Assert.True(row[d - 1] - row[d] < 90, $"hard edge at {d}");
        }

        Assert.True(row[0] > 120 && row[^1] < 3);
        Assert.True(At(20, 12).B > 20 && At(20, 2).B < 3);
        Assert.True(At(20, 49).R > 20 && At(20, 59).R < 3);
    });

    private static (Window Window, PromptInputControl Input) CreateFocusedInput(bool expanded = false)
    {
        var input = CreateInput(expanded);
        var window = new Window
        {
            Left = -10000, Top = -10000, Opacity = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            SizeToContent = SizeToContent.WidthAndHeight, Content = new Grid { Children = { input } },
        };
        window.Show();
        window.Activate();
        Assert.True(input.FocusInput(), "The input must take keyboard focus for this test.");
        window.UpdateLayout();
        return (window, input);
    }

    // The caret's center sits on the insertion point's leading edge, a line tall from the line's top.
    private static void AssertCaretAt(TextBox editor, GlowCaret caret, int index)
    {
        var host = (UIElement)VisualTreeHelper.GetParent(caret);
        var expected = editor.TranslatePoint(editor.GetRectFromCharacterIndex(index).TopLeft, host);
        Assert.Equal(expected.X, Canvas.GetLeft(caret) + (caret.Width / 2), 3);
        Assert.Equal(expected.Y, Canvas.GetTop(caret), 3);
        Assert.Equal(Visibility.Visible, caret.Visibility);
    }
}
