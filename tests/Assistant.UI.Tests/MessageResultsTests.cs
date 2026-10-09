using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void ADateIsWrittenAsAPersonWouldBesideAMessage()
    {
        // The reference's own: an old date is the short date with a two-digit year.
        Assert.Equal("4/25/26", SearchResultMetadata.FormatDate(new DateTimeOffset(2026, 4, 25, 18, 30, 0, SampleNow.Offset), SampleNow, English));

        // Today is the time of day, yesterday is a word, and the rest of the past week is the weekday.
        Assert.Equal("9:41 AM", Normalize(SearchResultMetadata.FormatDate(SampleNow.AddMinutes(-19), SampleNow, English)));
        Assert.Equal("Yesterday", SearchResultMetadata.FormatDate(SampleNow.AddDays(-1), SampleNow, English));
        Assert.Equal("Saturday", SearchResultMetadata.FormatDate(SampleNow.AddDays(-3), SampleNow, English));
        Assert.Equal("Wednesday", SearchResultMetadata.FormatDate(SampleNow.AddDays(-6), SampleNow, English));
        Assert.Equal("9/22/26", SearchResultMetadata.FormatDate(SampleNow.AddDays(-7), SampleNow, English));

        // The day is the reader's, wherever the message was written: 11 pm the night before is still yesterday.
        Assert.Equal("Yesterday", SearchResultMetadata.FormatDate(new DateTimeOffset(2026, 9, 29, 6, 0, 0, TimeSpan.Zero), SampleNow, English));

        // Other cultures keep their own order, and a later date is written out too.
        Assert.Equal("25.04.26", SearchResultMetadata.FormatDate(new DateTimeOffset(2026, 4, 25, 18, 30, 0, SampleNow.Offset), SampleNow, CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("10/1/26", SearchResultMetadata.FormatDate(SampleNow.AddDays(2), SampleNow, English));
    }

    [Fact]
    public void AMessageResultCarriesItsAvatarNameSnippetAndDate()
    {
        var command = new RecordingCommand();
        AvatarParticipant[] people = [new("J"), new(), new("AB")];
        var message = SearchResultFactory.Message(
            "Weekend plans", "Anyone up for a hike?", people, command, SampleNow.AddDays(-1), SampleNow, culture: English);

        Assert.Equal(SearchResultKind.Message, message.Kind);
        Assert.Equal("Weekend plans", message.Title);
        Assert.Equal("Anyone up for a hike?", message.Subtitle);
        Assert.Equal("Yesterday", message.Detail);
        Assert.True(message.Icon.HasParticipants);
        Assert.Equal(3, message.Icon.Participants!.Count);
        Assert.Same(SearchResultBadge.Messages, Assert.Single(message.Badges));
        Assert.Equal(SearchResultRowSize.Standard, message.Size);
        Assert.Equal("Weekend plans, Anyone up for a hike?, Yesterday", message.AutomationName);
        message.Activate();
        Assert.Equal(1, command.Count);

        // A status stands in for the date.
        var draft = SearchResultFactory.Message("Sam", "See you at eight", [new()], command, SampleNow, SampleNow, "Draft", English);
        Assert.Equal("Draft", draft.Detail);

        // A contact has its person's avatar, and the line that matched under the name.
        var contact = SearchResultFactory.Contact("Alex Morgan", new("AM"), command, "Mobile", "Missed");
        Assert.Equal(SearchResultKind.Contact, contact.Kind);
        Assert.Equal(("Mobile", "Missed"), (contact.Subtitle, contact.Detail));
        Assert.Equal("AM", Assert.Single(contact.Icon.Participants!).Initials);
        Assert.Empty(contact.Badges);
    }

    [Fact]
    public void ResultsAreGroupedByKindInAFixedOrderWithTheTopFewOfEach()
    {
        SearchResultViewModel Make(SearchResultKind kind, string title) => new(kind, title, new RecordingCommand());
        var results = new[]
        {
            Make(SearchResultKind.Message, "m1"), Make(SearchResultKind.App, "a1"), Make(SearchResultKind.Message, "m2"),
            Make(SearchResultKind.Contact, "c1"), Make(SearchResultKind.Message, "m3"), Make(SearchResultKind.File, "f1"),
            Make(SearchResultKind.App, "a2"),
        };

        // Apps, files, contacts, then messages; each keeps the order it came in, and stops at the most asked for.
        var sections = SearchResultGrouping.GroupByKind(results, maxPerGroup: 2);
        Assert.Equal([["a1", "a2"], ["f1"], ["c1"], ["m1", "m2"]], sections.Select(s => s.Items.Select(i => i.Title).ToArray()));
        Assert.All(sections, section => Assert.Equal("", section.Title));

        // Headers are for the ones that want them, and the order is the caller's to change.
        var titled = SearchResultGrouping.GroupByKind(
            results, titled: true, order: [SearchResultKind.Message, SearchResultKind.Contact]);
        Assert.Equal(["Messages", "Contacts"], titled.Select(section => section.Title));
        Assert.Equal(3, titled[0].Items.Count);

        Assert.Empty(SearchResultGrouping.GroupByKind([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchResultGrouping.GroupByKind(results, maxPerGroup: 0));
    }

    [Fact]
    public void AGroupAvatarsDiscsFitOnItsDiscAndNeverOverlap()
    {
        for (var count = 1; count <= AvatarLayout.MaxParticipants; count++)
        {
            var slots = AvatarLayout.For(count);
            Assert.Equal(count, slots.Count);
            foreach (var slot in slots)
            {
                Assert.True(Math.Sqrt((slot.X * slot.X) + (slot.Y * slot.Y)) + slot.Radius <= 18.001, $"{count}: {slot} leaves the disc");
            }

            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var (a, b) = (slots[i], slots[j]);
                    var distance = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    Assert.True(distance >= a.Radius + b.Radius - 0.001, $"{count}: {a} and {b} overlap");
                }
            }
        }

        // The first is always the largest, and more than seven are not drawn.
        Assert.All(Enumerable.Range(2, 6), count => Assert.Equal(AvatarLayout.For(count).Max(slot => slot.Radius), AvatarLayout.For(count)[0].Radius));
        Assert.Equal(7, AvatarLayout.For(12).Count);
        Assert.Empty(AvatarLayout.For(0));
        Assert.Equal(new AvatarSlot(0, 0, 18), Assert.Single(AvatarLayout.For(1)));
    }

    [Fact]
    public void AnAvatarDrawsPhotosInitialsAndSilhouettesOnDiscs() => RunSta(() => WithTheme(() =>
    {
        var photo = DemoImages.Portrait(0);
        var view = new AvatarView
        {
            Width = 36, Height = 36,
            Participants = [new(Photo: photo), new("J"), new(Photo: DemoImages.Portrait(1)), new("AB"), new("SA"), new(Photo: DemoImages.Portrait(2)), new()],
        };
        view.BeginInit();
        view.EndInit();
        Layout(view);
        var pixels = Pixels(view);

        // Outside the disc there is nothing, and inside it the group's discs sit on the translucent one.
        Assert.Equal(0, pixels(0, 0).A);
        Assert.Equal(0, pixels(35, 35).A);
        Assert.True(pixels(18, 34).A > 40, "the cluster's disc is missing");

        // Each person is where the reference has them: the first's photo the largest, upper left, the second's initials
        // on a periwinkle disc, upper right.
        var (photoSlot, initialsSlot) = (AvatarLayout.For(7)[0], AvatarLayout.For(7)[1]);
        var onPhoto = pixels(18 + photoSlot.X, 18 + photoSlot.Y);
        Assert.True(onPhoto.A > 250);
        var onInitials = pixels(18 + initialsSlot.X - 2.2, 18 + initialsSlot.Y + 2.2);
        Assert.True(onInitials.A > 250 && onInitials.B > onInitials.R, $"periwinkle expected, got {onInitials}");

        // One person is a disc of their own, filling the square: initials show as light letters on it, and without
        // either, a silhouette shows as a head over shoulders.
        var alone = new AvatarView { Width = 36, Height = 36, Participants = [new("J")] };
        alone.BeginInit();
        alone.EndInit();
        Layout(alone);
        var disc = Pixels(alone);
        Assert.True(disc(18, 2).A > 250 && disc(2, 18).A > 250 && disc(0, 0).A == 0);
        Assert.Contains(Enumerable.Range(8, 20).SelectMany(y => Enumerable.Range(8, 20).Select(x => disc(x, y))), c => c.R > 235 && c.G > 235);

        var silhouette = new AvatarView { Width = 36, Height = 36, Participants = [new()] };
        silhouette.BeginInit();
        silhouette.EndInit();
        Layout(silhouette);
        var figure = Pixels(silhouette);
        Assert.True(figure(18, 12).R > figure(5, 24).R + 30, "the head should be lighter than the disc");
        Assert.True(figure(18, 30).R > figure(5, 24).R + 30, "the shoulders should be lighter than the disc");

        RenderFixture(view, "avatar-group-2x.png", 6);
    }));

    // The color of the pixel at a point of an element's own square, in DIPs (rendered 8x, so sub-DIP points read true).
    private static Func<double, double, Color> Pixels(FrameworkElement element)
    {
        var bitmap = Render(element, 8);
        var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
        return (x, y) =>
        {
            var offset = ((int)(y * 8) * bitmap.PixelWidth + (int)(x * 8)) * 4;

            // Premultiplied BGRA: fully opaque pixels read as themselves.
            return Color.FromArgb(data[offset + 3], data[offset + 2], data[offset + 1], data[offset]);
        };
    }

    [Fact]
    public void MessageAndContactResultsSitInTheReferencesPanel() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var results = new SearchResultsViewModel(new PlaceholderSearchResults(new FixedTime(SampleNow)));
        var assistant = CreateAssistant(frames: frames, results: results);
        var (window, bar, _) = assistant;
        var culture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = English;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var host = GridNamed(window, "SurfaceHost");
            var list = Named<ItemsControl>(window, "ResultsList");
            Named<PromptInputControl>(window, "PromptInput").Text = "demo";
            Pump();
            FinishPanel(frames);

            // Conversations, then people, then the rest, and the row that would search Messages last, each a section.
            Assert.Equal([5, 3, 1, 1, 1], results.Sections.Select(section => section.Items.Count));
            var rows = Descendants<SearchResultRow>(list).ToArray();
            Assert.Equal(11, rows.Length);
            var group = rows[0];

            // The first conversation is the reference's: an Arabic name, a group's seven-person avatar with the Messages
            // mark on its corner, a snippet cut short with an ellipsis, and its old date at the right.
            Assert.Equal("\u0627\u0644\u0625\u062e\u0648\u0629", group.Title);
            Assert.Equal("4/25/26", group.Detail);
            var avatar = Descendants<AvatarView>(Part<ContentPresenter>(group, "Icon")).Single();
            Assert.Equal(7, avatar.Participants!.Count);
            Assert.Equal(Visibility.Visible, avatar.Visibility);
            AssertRect(new Rect(45 + 25.5, 190 + 13, 36, 36), BoundsIn(host, avatar));
            Assert.Single(Part<ItemsControl>(group, "Badges").Items);
            Assert.Equal(TextTrimming.CharacterEllipsis, Part<TextBlock>(group, "Subtitle").TextTrimming);
            Assert.Equal(45 + 79.25, BoundsIn(host, Part<TextBlock>(group, "Title")).Left, 1);
            Assert.Equal(45 + 520 - 29, BoundsIn(host, Part<TextBlock>(group, "Detail")).Right, 1);

            // Each has its own treatment: a single person's initials, a photo, a status instead of a date.
            Assert.Equal(["4/25/26", "8:00 AM", "Yesterday", "Saturday", "Draft"], rows.Take(5).Select(row => row.Detail).Select(Normalize));
            Assert.Single(Descendants<AvatarView>(Part<ContentPresenter>(rows[1], "Icon")).Single().Participants!);
            Assert.Equal(["Alex Morgan", "Priya Nair", "Sam Rivera"], rows.Skip(5).Take(3).Select(row => row.Title));
            Assert.Equal(["Mobile", "priya@example.com", "Mobile"], rows.Skip(5).Take(3).Select(row => row.Subtitle));
            Assert.Equal("Missed", rows[7].Detail);
            Assert.All(rows.Take(5), row => Assert.Single(Part<ItemsControl>(row, "Badges").Items));
            Assert.All(rows.Skip(5).Take(3), row => Assert.Empty(Part<ItemsControl>(row, "Badges").Items));
            Assert.Equal("Search Messages", rows[^1].Title);

            // Every row of the kind is as tall as the others and the panel stops at the reference's height.
            Assert.All(rows, row => Assert.Equal(row.Kind == SearchResultKind.Knowledge ? 82 : 62, row.ActualHeight));
            Assert.Equal(409, GridNamed(window, "LauncherLayer").Height);
            RenderFixture(host, "messages-demo-2x.png", 2, new Rect(33, 17, 544, 546));
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
            window.Close();
        }
    }));

    // Times of day are written with a narrow no-break space in newer cultures' data; compare them as plain text.
    private static string Normalize(string text) => text.Replace('\u202f', ' ');

    [Fact]
    public void TheReferencesMessagesRenderAsInIt() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        AvatarParticipant[] group =
        [
            new(Photo: DemoImages.Portrait(0)), new("J"), new(Photo: DemoImages.Portrait(1)), new("AB"), new("SA"),
            new(Photo: DemoImages.Portrait(2)), new(),
        ];
        var results = new SearchResultsViewModel(new FakeResultsSource([
            new SearchResultSectionViewModel(null, [SearchResultFactory.Message(
                "\u0627\u0644\u0625\u062e\u0648\u0629", "(If u need a browser version (only works on brave) lmk I have\u2026", group,
                new RecordingCommand(), new DateTimeOffset(2026, 4, 25, 18, 30, 0, SampleNow.Offset), SampleNow, culture: English)]),
            new SearchResultSectionViewModel(null, [new SearchResultViewModel(
                SearchResultKind.App, "Search Messages", new RecordingCommand(), icon: SearchResultIcon.FromGlyph("Result.Icon.Message"))]),
        ]));
        var assistant = CreateAssistant(frames: frames, results: results);
        var (window, _, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            Named<PromptInputControl>(window, "PromptInput").Text = "sample";
            Pump();
            FinishPanel(frames);
            var host = GridNamed(window, "SurfaceHost");

            // The panel: the chips (19.5 + 24), 17, 62 + 2 + 62 + 2 and 9.75, and the rows where the reference has them.
            Assert.Equal(198.25, GridNamed(window, "LauncherLayer").Height);
            RenderFixture(host, "messages-reference-2x.png", 2, new Rect(33, 17, 544, 339));
        }
        finally { window.Close(); }
    }));
}
