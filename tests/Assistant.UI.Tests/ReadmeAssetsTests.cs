using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Assistant.UI.Animation;
using Assistant.UI.Controls;
using Xunit;

namespace Assistant.UI.Tests;

// The headings of the README are pictures of the Working pill with the section's name in it ("Features", "Installation"): the pill's own
// spinner (SearchingIndicator, held at one pose), its own type and its own measures, on a glass that needs no wallpaper behind it. They are
// drawn here, by the controls themselves, into ASSISTANT_UI_RENDER_DIR when that is set, and copied to docs/images from there.
public sealed partial class PromptInputControlTests
{
    private static readonly string[] ReadmeHeadings = ["Features", "Installation", "Shortcuts", "Updates", "Build from source", "Credits", "License"];

    [Fact]
    public void TheReadmesHeadingsAreTheWorkingPillWithTheSectionsNameInIt() => RunSta(() => WithTheme(() =>
    {
        foreach (var heading in ReadmeHeadings)
        {
            var pill = HeadingPill(heading);
            var window = new Window
            {
                Content = new Border { Padding = new Thickness(8), Child = pill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top },
                SizeToContent = SizeToContent.WidthAndHeight, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            };
            try
            {
                window.Show();
                Pump();

                // The pill's own measures: 53 tall, the spinner 10 from its left, the words 59 from it and 20.5 from its right.
                Assert.InRange(pill.ActualHeight, 52.5, 53.5);
                var label = Descendants<TextBlock>(pill).Single();
                var spinner = Descendants<SearchingIndicator>(pill).Single();
                Assert.Equal(heading, label.Text);
                Assert.InRange(label.TranslatePoint(default, pill).X, 58.5, 59.5);
                Assert.InRange(pill.ActualWidth - label.TranslatePoint(new Point(label.ActualWidth, 0), pill).X, 20, 21);
                Assert.InRange(spinner.TranslatePoint(new Point(spinner.ActualWidth / 2, 0), pill).X - (spinner.ActualWidth / 2), 9.5, 10.5);
                Assert.True(spinner.Lengths.Max() > spinner.Lengths.Min(), "The spinner is held mid-turn, its droplets of different sizes.");
                RenderFixture(pill, "readme-" + heading.ToLowerInvariant().Replace(' ', '-') + ".png", 2);
            }
            finally
            {
                window.Close();
            }
        }
    }));

    private static Border HeadingPill(string words)
    {
        var resources = Application.Current.Resources;
        var spinner = new SearchingIndicator
        {
            Motion = (DropletRingMotion)resources["Motion.SearchingRing"],
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0), // (one in from the pill's 10, for the rim)
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.1, 1.1),
            HeldPeak = 5.7, // the pose of the reference: the fullest droplet at the top, the next to its left
        };
        var label = new TextBlock
        {
            Text = words,
            FontFamily = (FontFamily)resources["Font.Text"],
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)resources["Brush.Text.Activity"],
            Margin = new Thickness(0, 0, 0, 1.5),
        };
        var content = new Grid();
        content.Children.Add(spinner);
        // The rim takes one of the pill's own units on each side, so what is inside it is set one in from the pill's measures.
        content.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(58, 0, 19.5, 0), Child = label });

        // The glass as it reads over a dark picture: nearly black at the top, lifting to a grey at the bottom, with the faint rim of the pill.
        var glass = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        glass.GradientStops.Add(new GradientStop(Color.FromRgb(0x1C, 0x1F, 0x1D), 0));
        glass.GradientStops.Add(new GradientStop(Color.FromRgb(0x3B, 0x3F, 0x3C), 0.55));
        glass.GradientStops.Add(new GradientStop(Color.FromRgb(0x66, 0x6B, 0x67), 1));
        return new Border
        {
            Height = 53,
            CornerRadius = new CornerRadius(26.5),
            Background = glass,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Left,
            SnapsToDevicePixels = true,
        };
    }
}
