using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Domain;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void CardShowsTheImageMostRecentlyAttachedWhereverItIs() => RunSta(() =>
    {
        var (a, b) = (Picture("A"), Picture("B"));
        HistoryConversationViewModel Card(params MessageViewModel[] messages) =>
            new(Guid.NewGuid(), messages, ReferenceNow, new FixedClock(ReferenceNow));

        // Text, image A, text, image B: B.
        Assert.Same(b, Card(
            User("Hi"), Answer("Hello."),
            User("What is this?", a), Answer("A photo."),
            User("Thanks"), Answer("You’re welcome."),
            User("And this?", b), Answer("Another photo.")).Image);

        // Image A, then only text: A.
        Assert.Same(a, Card(
            User("What is this?", a), Answer("A photo."),
            User("Tell me more"), Answer("It is a photo of a valley."),
            User("Where?"), Answer("In the mountains.")).Image);

        // Several images on one message: the last one attached.
        Assert.Same(b, Card(User("Compare these", a, b), Answer("They differ.")).Image);

        // Text only, or pictures only in answers: no image.
        Assert.Null(Card(User("Hi"), Answer("Hello.")).Image);
        var gallery = Answer("I found 2 photos.");
        gallery.Content.Add(new ImageCollection([a, b]));
        Assert.Null(Card(User("Show my photos"), gallery).Image);

        // A later image attached as the conversation goes on becomes the card's.
        var question = User("What is this?", a);
        var card = Card(question, Answer("A photo."));
        Assert.Same(a, card.Image);
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        card.Update([question, Answer("A photo."), User("And this?", b), Answer("Another.")], ReferenceNow.AddMinutes(1));
        Assert.Same(b, card.Image);
        Assert.Contains(changed, name => name is null or nameof(HistoryConversationViewModel.Image));
    });

    [Fact]
    public void HistoryCardFollowsTheNewestAttachedImageAsTheConversationGoesOn() => RunSta(() => WithTheme(() =>
    {
        var (window, history, _) = CreateHistoryWindow();
        var (lake, valley) = (DemoImages.Photos()[1], DemoImages.Valley());
        var id = Guid.NewGuid();
        List<MessageViewModel> messages = [User("I’m planning a hike"), Answer("Late summer is a good time to go.")];
        history.Open(id, messages, ReferenceNow.AddMinutes(-3));
        try
        {
            window.Show();
            Pump();
            var list = Named<ListBox>(window, "Conversations");
            ListBoxItem CardItem() => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            CardThumbnail Thumbnail() => Descendants<CardThumbnail>(CardItem()).Single();

            // Text only: the preview, no image.
            Assert.Equal(Visibility.Collapsed, Thumbnail().Visibility);
            Assert.Equal(Visibility.Visible, Descendants<TextBlock>(CardItem()).Last().Visibility);

            // An image attached to a later question: the card shows it, whichever message it came with.
            messages.AddRange([User("Where is this?", valley), Answer("A valley in the Rockies.")]);
            history.Open(id, messages, ReferenceNow.AddMinutes(-2));
            Pump();
            Assert.Equal(Visibility.Visible, Thumbnail().Visibility);
            Assert.Same(valley.Thumbnail, Thumbnail().Source);
            Assert.Equal(Visibility.Collapsed, Descendants<TextBlock>(CardItem()).Last().Visibility);

            // More text, then a newer image: the card switches to it.
            messages.AddRange([User("How long is the trail?"), Answer("About 12 km."), User("And this one?", lake), Answer("A glacial lake.")]);
            history.Open(id, messages, ReferenceNow.AddMinutes(-1));
            Pump();
            Assert.Same(lake.Thumbnail, Thumbnail().Source);
            Assert.Equal("Sample photo of a mountain lake", AutomationName(Thumbnail()));

            // The list's row shows the same one.
            history.Layout = HistoryLayout.List;
            Pump();
            var rows = Named<ListBox>(window, "ConversationRows");
            var row = RowOf(rows, history.Conversations[0]);
            var rowThumbnail = Descendants<System.Windows.Shapes.Rectangle>(row).Single(shape => shape.Name == "Thumbnail");
            Assert.Equal(Visibility.Visible, rowThumbnail.Visibility);
            Assert.Same(lake.Thumbnail, ((ImageBrush)rowThumbnail.Fill).ImageSource);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void CardThumbnailSetsTheImageIntoTheCardsGlass() => RunSta(() =>
    {
        var size = new Size(136.9, 102);
        var (width, height) = (size.Width, size.Height);
        var outline = CardThumbnail.CreateOutline(size, 46.5, 18.6, 32, 5.8, 7.2, 12.5);
        bool Image(double x, double y) => outline.FillContains(new Point(x, y), 0.001, ToleranceType.Absolute);
        bool Mirrored(double x, double y) => Image(width - x, y);

        // The column: the image under the title, its top corners strongly rounded, the glass beside it.
        Assert.True(Image(width / 2, 0.5));
        Assert.True(Image(19.5, 40));
        Assert.True(Image(width / 2, 60));
        Assert.False(Image(20, 1));
        Assert.False(Mirrored(20, 1));
        Assert.True(Image(18.6 + 32, 0.5));
        Assert.False(Image(10, 40));
        Assert.False(Image(17.8, 80));
        Assert.False(Mirrored(17.8, 80));

        // The glass beside the column ends 12.5 above the card's bottom in a rounded tip: the image's side flares
        // outward just above it, and the image fills below it out to the card's outline.
        Assert.True(Image(18.2, 87.5));
        Assert.False(Image(16.5, 87.5));
        Assert.True(Mirrored(18.2, 87.5));
        Assert.False(Mirrored(16.5, 87.5));
        Assert.True(Image(12.8, height - 10.5));
        Assert.True(Image(16, height - 8));
        Assert.True(Image(width / 2, height - 0.5));
        Assert.True(Mirrored(16, height - 8));

        // The tip stands clear of the card's outline, with a sliver of the image between them: across the card just
        // above the tip's lowest point, outside the card, image, glass, then image again.
        var runs = new List<bool>();
        for (var x = 0.0; x < width / 2; x += 0.02)
        {
            var inside = Image(x, height - 13);
            if (runs.Count == 0 || runs[^1] != inside) runs.Add(inside);
        }

        Assert.Equal([false, true, false, true], runs);

        // The card's own bottom corners cut the image off.
        Assert.False(Image(1, height - 1));
        Assert.False(Mirrored(1, height - 1));

        // Too small to hold glass beside it, the image just fills the card's outline.
        var small = CardThumbnail.CreateOutline(new Size(30, 20), 46.5, 18.6, 32, 5.8, 7.2, 12.5);
        Assert.True(small.FillContains(new Point(15, 10)));
        Assert.False(small.FillContains(new Point(0.5, 19.5)));
    });

    [Fact]
    public void HistoryGlassIsNeutralAndTakesItsHueFromTheBackdrop() => RunSta(() => WithTheme(() =>
    {
        // Every layer is black, white or gray, never tinted: translucent over the backdrop, or opaque without one.
        string[] translucent =
        [
            "Brush.Surface.Workspace", "Brush.Surface.Sidebar", "Brush.Surface.HistoryCard",
            "Brush.Surface.HistoryCardHover", "Brush.Surface.HistoryCardSelected", "Brush.Surface.HistoryRowHover",
            "Brush.Surface.HistoryRowSelected", "Brush.Control.Glass", "Brush.Control.GlassOpen",
        ];
        foreach (var key in translucent)
        {
            var color = ThemeColor(key);
            Assert.True(color.R == color.G && color.G == color.B, $"{key} is neutral.");
            Assert.InRange(color.A, 1, 254);
        }

        foreach (var key in new[] { "Brush.Surface.WorkspaceOpaque", "Brush.Surface.SidebarOpaque", "Brush.Surface.MenuOpaque" })
        {
            var color = ThemeColor(key);
            Assert.True(color.R == color.G && color.G == color.B, $"{key} is neutral.");
            Assert.Equal(255, color.A);
        }

        var rim = Assert.IsType<LinearGradientBrush>(Application.Current.FindResource("Brush.Stroke.HistoryCardRim"));
        Assert.All(rim.GradientStops, stop => Assert.Equal((255, 255, 255), (stop.Color.R, stop.Color.G, stop.Color.B)));

        // Over Mica as measured over the reference's green and yellow wallpaper, and over a purple one, the workspace
        // is the darkest layer, the sidebar lighter, and the cards and glass buttons lighter again, alike; each keeps
        // the backdrop's hue.
        foreach (var (backdrop, warm) in new[] { (Color.FromRgb(32, 33, 26), true), (Color.FromRgb(36, 26, 44), false) })
        {
            var workspace = Over(ThemeColor("Brush.Surface.Workspace"), backdrop);
            var sidebar = Over(ThemeColor("Brush.Surface.Sidebar"), backdrop);
            var card = Over(ThemeColor("Brush.Surface.HistoryCard"), sidebar);
            var button = Over(ThemeColor("Brush.Control.Glass"), sidebar);
            Assert.True(Light(workspace) < Light(backdrop) && Light(backdrop) < Light(sidebar) && Light(sidebar) + 15 < Light(card));
            Assert.InRange(Light(button) - Light(card), -12, 12);
            Assert.All(new[] { workspace, sidebar, card, button }, color =>
                Assert.True(warm ? color.R > color.B : color.B > color.G, $"{color} keeps the backdrop's hue."));
        }

        static Color Over(Color layer, Color backdrop)
        {
            var alpha = layer.A / 255.0;
            byte Mix(byte top, byte bottom) => (byte)Math.Round((top * alpha) + (bottom * (1 - alpha)));
            return Color.FromRgb(Mix(layer.R, backdrop.R), Mix(layer.G, backdrop.G), Mix(layer.B, backdrop.B));
        }

        static double Light(Color color) => (color.R + color.G + color.B) / 3.0;
    }));

    [Fact]
    public void SelectedCardWithAnImageMatchesTheReference() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        // The reference's selected card: a two-line title over a screenshot, ringed in blue. Synthetic content only.
        var (window, history, _) = CreateHistoryWindow();
        var screenshot = DemoImages.Screenshots(1)[0];
        history.Open(Guid.NewGuid(), [User("Color Inversion", screenshot), Answer("Inverting colors swaps each for its opposite.")],
            ReferenceNow.AddDays(-1));
        try
        {
            window.Show();
            Pump();
            var list = Named<ListBox>(window, "Conversations");
            var card = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.True(card.IsSelected);
            var thumbnail = Descendants<CardThumbnail>(card).Single();
            Assert.Same(screenshot.Thumbnail, thumbnail.Source);

            // The image starts 5.4 under the title's two lines, about where the reference's does.
            Assert.Equal(82.3, BoundsIn(card, thumbnail).Top, 0);

            // Drawn at the reference's scale, for comparison with it.
            var sidebar = Named<Grid>(window, "Sidebar");
            var bounds = BoundsIn(sidebar, card);
            bounds.Inflate(8, 8);
            RenderFixture(sidebar, "history-card-image.png", 2.127, bounds);
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    private static MessageViewModel User(string text, params ImageItem[] attachments) =>
        new(MessageRole.User, text, attachments);

    private static MessageViewModel Answer(string text) => new(MessageRole.Assistant, text);

    // A one-pixel stand-in picture, already in memory.
    private static ImageItem Picture(string name) =>
        new(name, BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4));

    private static T TemplatePart<T>(Control control, string name) where T : class =>
        Assert.IsType<T>(control.Template.FindName(name, control));

    // The colors along a line across an element, from start in its own coordinates, drawn at scale.
    private static Color[] ScanRow(FrameworkElement element, Point start, double length, double scale)
    {
        var bitmap = Render(element, scale, new Rect(start.X, start.Y, length, 1 / scale));
        var pixels = new byte[bitmap.PixelWidth * 4];
        bitmap.CopyPixels(new Int32Rect(0, 0, bitmap.PixelWidth, 1), pixels, bitmap.PixelWidth * 4, 0);
        return Enumerable.Range(0, bitmap.PixelWidth)
            .Select(i => Color.FromArgb(pixels[(i * 4) + 3], pixels[(i * 4) + 2], pixels[(i * 4) + 1], pixels[i * 4]))
            .ToArray();
    }
}
