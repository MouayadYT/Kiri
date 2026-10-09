using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Domain;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ShapePath = System.Windows.Shapes.Path;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // A fixed "now" for dates in lists of files: Tuesday 29 September 2026, 10:30 in UTC+2.
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public void RichAnswerCardsCaptionTheirResultAndCopyIt()
    {
        var clipboard = new FakeClipboard();
        var card = new CalculationResult("9 + 10", "19", new CopyTextCommand(clipboard, "19"));
        Assert.Equal(("Calculation", "9 + 10", "9 + 10 =", "19"), (card.Label, card.Expression, card.Caption, card.Result));
        Assert.Null(card.Secondary);
        Assert.IsAssignableFrom<RichAnswerCard>(card);
        Assert.True(card.CopyCommand!.CanExecute(null));
        card.CopyCommand.Execute(null);
        Assert.Equal(["19"], clipboard.Copied);

        // Without an expression the label is the caption; blank extras are left out.
        var date = new RichAnswerCard("Date", "Tuesday, September 29", expression: " ", secondary: "Week 40");
        Assert.Equal(("Date", null, "Week 40"), (date.Caption, date.Expression, date.Secondary));
        Assert.Null(date.CopyCommand);
        Assert.Throws<ArgumentException>(() => new CalculationResult(" ", "1"));
        Assert.Throws<ArgumentException>(() => new RichAnswerCard("", "1"));

        // A command with nothing to copy cannot run.
        var empty = new CopyTextCommand(clipboard, "");
        Assert.False(empty.CanExecute(null));
        empty.Execute(null);
        Assert.Single(clipboard.Copied);
    }

    [Fact]
    public void EachKindOfContentKnowsWhetherItIsWideAndKeepsItsTextOutOfToString()
    {
        var parts = new MessageContent[]
        {
            new TextContent("private"), new CalculationResult("1 + 1", "2"), new CodeContent("private();"),
            new ImageCollection([]), new FileCollection([]),
        };
        Assert.Equal([false, true, true, true, true], parts.Select(part => part.IsWide));
        Assert.Equal(["TextContent", "CalculationResult", "CodeContent", "ImageCollection", "FileCollection"],
            parts.Select(part => part.ToString()));
    }

    [Fact]
    public void CodeKeepsItsLinesAndDrawsTabsAtTabStops()
    {
        var code = new CodeContent("if (x)\r\n{\r\n\tgo();\r\n  \tstop();\r\n}\r\n\r\n", " C# ");
        Assert.Equal("if (x)\n{\n\tgo();\n  \tstop();\n}", code.Code);
        Assert.Equal("if (x)\n{\n    go();\n    stop();\n}", code.DisplayCode);
        Assert.Equal("C#", code.Language);
        Assert.Null(new CodeContent("x", "  ").Language);
        var plain = new CodeContent("plain();");
        Assert.Same(plain.Code, plain.DisplayCode);
    }

    [Fact]
    public void FilesAreDescribedByKindPlaceAndDate()
    {
        var clock = new FixedClock(Now);
        var documents = System.IO.Path.Combine("C:\\", "Users", "someone", "Documents");
        var sheet = new FileItem(SearchResultItemType.File, "Budget.xlsx", System.IO.Path.Combine(documents, "Budget.xlsx"),
            Now.AddHours(-3), "  matching text  ", clock);
        Assert.Equal(("Documents", "XLSX", "matching text"), (sheet.Location, sheet.TypeLabel, sheet.Snippet));
        Assert.StartsWith("Documents · Today, ", sheet.Details);

        var yesterday = new FileItem(SearchResultItemType.File, "notes.markdown", System.IO.Path.Combine(documents, "notes.markdown"),
            Now.AddDays(-1), clock: clock);
        Assert.Null(yesterday.TypeLabel);
        Assert.StartsWith("Documents · Yesterday, ", yesterday.Details);

        var older = new FileItem(SearchResultItemType.Folder, "Finance", System.IO.Path.Combine(documents, "Finance"),
            new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.FromHours(2)), clock: clock);
        Assert.Null(older.TypeLabel);
        Assert.Equal("Documents · " + new DateTime(2026, 3, 4).ToString(
            System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM")), older.Details);

        var lastYear = new FileItem(SearchResultItemType.File, "old.pdf", "D:\\old.pdf",
            new DateTimeOffset(2025, 12, 1, 12, 0, 0, TimeSpan.FromHours(2)), clock: clock);
        Assert.Equal(("D:\\", "PDF"), (lastYear.Location, lastYear.TypeLabel));
        Assert.EndsWith(new DateTime(2025, 12, 1).ToString("d"), lastYear.Details);

        var app = FileItem.From(new SearchResultItem(SearchResultItemType.App, "Calculator", "Microsoft.WindowsCalculator!App"));
        Assert.Equal((null, null, ""), (app.Location, app.TypeLabel, app.Details));

        var results = FileCollection.From([new SearchResultItem(SearchResultItemType.File, "a.txt", "C:\\a.txt") { Snippet = "hit" }]);
        Assert.Equal(("a.txt", "TXT", "hit"), (results.Files[0].Name, results.Files[0].TypeLabel, results.Files[0].Snippet));
    }

    [Fact]
    public void ImagesDecodeInTheBackgroundAtThumbnailSizeAndUpright() => RunSta(() =>
    {
        var directory = Directory.CreateTempSubdirectory("assistant-ui-tests-");
        try
        {
            // A wide image, larger than a thumbnail, with its top-left corner marked red.
            var wide = System.IO.Path.Combine(directory.FullName, "wide.png");
            SaveImage(wide, new PngBitmapEncoder(), 1200, 600);
            var item = new ImageItem("wide.png", wide);
            var changes = 0;
            item.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(ImageItem.Thumbnail) ? 1 : 0;
            Assert.Null(item.Thumbnail);
            WaitUntil(() => item.Thumbnail is not null, "The image was not decoded.");
            var thumbnail = Assert.IsAssignableFrom<BitmapSource>(item.Thumbnail);
            Assert.True(thumbnail.IsFrozen);
            Assert.Equal((640, 320), (thumbnail.PixelWidth, thumbnail.PixelHeight));
            Assert.Equal(1, changes);

            // A camera photo stored sideways (EXIF orientation 6) is turned upright, so its marked corner moves right.
            var sideways = System.IO.Path.Combine(directory.FullName, "sideways.jpg");
            SaveImage(sideways, new JpegBitmapEncoder { QualityLevel = 95 }, 400, 200, orientation: 6);
            var upright = Assert.IsAssignableFrom<BitmapSource>(ImageThumbnail.Load(sideways, 320));
            Assert.Equal((200, 400), (upright.PixelWidth, upright.PixelHeight));
            Assert.True(IsRed(upright, upright.PixelWidth - 5, 5), "The corner that was top left should be top right.");
            Assert.False(IsRed(upright, 5, 5));

            // Files that are not images, or are gone, leave the tile empty.
            var text = System.IO.Path.Combine(directory.FullName, "not-an-image.png");
            File.WriteAllText(text, "not an image");
            Assert.Null(ImageThumbnail.Load(text, 320));
            Assert.Null(ImageThumbnail.Load(System.IO.Path.Combine(directory.FullName, "missing.png"), 320));

            // An image in memory needs no decoding and is frozen.
            var drawn = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Pbgra32, null);
            var inMemory = new ImageItem("drawn", drawn);
            Assert.True(inMemory.Thumbnail!.IsFrozen);
            Assert.Null(inMemory.Path);
        }
        finally { directory.Delete(recursive: true); }
    });

    [Fact]
    public void AskingShowsTheAnswerAfterTheQuestion()
    {
        var answers = new FakeAnswers { Reply = question => new MessageViewModel(MessageRole.Assistant, "Reply to " + question) };
        var model = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), answers);
        model.StartNew("first");
        Assert.Equal([(MessageRole.User, "first"), (MessageRole.Assistant, "Reply to first")],
            model.Messages.Select(message => (message.Role, message.Text)));

        answers.Reply = _ => null;
        model.StartNew("second");
        Assert.Equal((MessageRole.User, "second"), (Assert.Single(model.Messages).Role, model.Messages[0].Text));
    }

    [Fact]
    public void SampleAnswersUseTheRendererForWhatTheyShow() => RunSta(() =>
    {
        var clipboard = new FakeClipboard();
        var demo = new DemoAnswerProvider(clipboard, new FixedClock(Now));
        Type[] Kinds(string question) => demo.Answer(question)!.Content.Select(part => part.GetType()).ToArray();

        // The calculator's reference question, however it is typed, gets prose and a calculation card.
        foreach (var question in new[] { "What is 9+10", "what's 9 + 10?", "  WHAT IS 9 +10. ", "9+10", "demo calculator" })
        {
            Assert.Equal([typeof(TextContent), typeof(CalculationResult)], Kinds(question));
        }

        var calculation = demo.Answer("What is 9+10")!;
        Assert.Equal("9 + 10 is 19.", calculation.Text);
        var card = Assert.IsType<CalculationResult>(calculation.Content[1]);
        Assert.Equal(("9 + 10 =", "19"), (card.Caption, card.Result));
        card.CopyCommand!.Execute(null);
        Assert.Equal(["19"], clipboard.Copied);

        // Photos and screenshots are galleries, never cards.
        var photos = demo.Answer("Find the image I took yesterday")!;
        Assert.Equal("I found 4 photos from yesterday.", photos.Text);
        Assert.Equal(4, Assert.IsType<ImageCollection>(photos.Content[1]).Images.Count);
        Assert.All(Assert.IsType<ImageCollection>(photos.Content[1]).Images, image => Assert.NotNull(image.Thumbnail));
        Assert.Equal(5, Assert.IsType<ImageCollection>(demo.Answer("Show me the last 5 screenshots I took")!.Content[1]).Images.Count);
        Assert.DoesNotContain(photos.Content, part => part is MessageCard);

        // Code is a code block between prose; files are a list; plain text is only prose.
        Assert.Equal([typeof(TextContent), typeof(CodeContent), typeof(TextContent)], Kinds("demo code"));
        var code = Assert.IsType<CodeContent>(demo.Answer("demo code")!.Content[1]);
        code.CopyCommand!.Execute(null);
        Assert.Equal(code.Code, clipboard.Copied[^1]);
        Assert.Equal([typeof(TextContent), typeof(FileCollection)], Kinds("demo files"));
        Assert.Equal(4, Assert.IsType<FileCollection>(demo.Answer("demo files")!.Content[1]).Files.Count);
        Assert.Equal([typeof(TextContent)], Kinds("demo text"));
        Assert.Contains("demo code", demo.Answer("demo")!.Text);

        // Anything else gets no answer, as before.
        Assert.Null(demo.Answer("What is 2+2"));
        Assert.Null(demo.Answer("Plan my weekend"));
    });

    [Fact]
    public void TheAppAnswersWithSampleContentUntilRealAnswersExist()
    {
        // Building the host validates every registration, the conversation's included.
        using var host = AppHost.Create();
        Assert.IsType<DemoAnswerProvider>(host.Services.GetRequiredService<IAnswerProvider>());
        Assert.IsType<WpfTextClipboard>(host.Services.GetRequiredService<ITextClipboard>());
    }

    [Fact]
    public void AskingFromTheBarShowsTheAnswersContentInThePanel() => RunSta(() => WithTheme(() =>
    {
        var assistant = CreateAssistant(answers: new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now)));
        var window = assistant.Window;
        try
        {
            window.ShowAndFocus();
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "What is 9+10";
            Assert.True(input.TrySubmit());
            Pump();
            Assert.True(window.IsVisible);
            var area = Named<Grid>(window, "ConversationLayer");
            Assert.Equal("9 + 10 is 19.", TextNamed(area, "9 + 10 is 19.").Text);
            Assert.Single(Descendants<ContentControl>(area), control => control.Content is CalculationResult);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void CalculationCardMatchesTheResultCardReference() => RunSta(() => WithTheme(() =>
    {
        var clipboard = new FakeClipboard();
        var (panel, model, _) = CreatePanel(answers: new DemoAnswerProvider(clipboard, new FixedClock(Now)));
        model.StartNew("What is 9+10");
        try
        {
            panel.ShowConversation();
            var area = Named<Grid>(panel, "ConversationLayer");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Rect Bounds(FrameworkElement element) => BoundsIn(area, element);

            // Prose, then the card: black, with a card's corners, 16 in from the panel's edges and 19.5 below the text.
            var prose = TextNamed(area, "9 + 10 is 19.");
            var frame = Descendants<ContentControl>(area).Single(control => control.Content is CalculationResult);
            var card = Bounds(frame);
            Assert.Equal((16, 386), (card.Left, card.Width));
            Assert.Equal(Bounds(prose).Bottom + 19.5, card.Top, 3);
            var surface = Assert.Single(Descendants<PanelShape>(frame));
            Assert.Equal((Colors.Black, 40.5), (Assert.IsType<SolidColorBrush>(surface.Fill).Color, surface.CornerSize));
            Assert.False(IsInside(prose, area, element => element is ContentControl { Content: MessageCard } or PanelShape));

            // A dim caption over a large, bright result, 18 in from the card's left.
            var caption = TextNamed(frame, "9 + 10 =");
            var result = TextNamed(frame, "19");
            Assert.Equal((13.5, Color.FromRgb(0x4D, 0x4D, 0x4D)), (caption.FontSize, Assert.IsType<SolidColorBrush>(caption.Foreground).Color));
            Assert.Equal((26, Color.FromRgb(0xF2, 0xF2, 0xF2)), (result.FontSize, Assert.IsType<SolidColorBrush>(result.Foreground).Color));
            Assert.Equal((34, card.Top + 26.5), (Bounds(caption).Left, Bounds(caption).Top));
            Assert.Equal(34, Bounds(result).Left);
            Assert.Equal(106.5, card.Height, 1);

            // The copy button: a 24 disc 18 in from the card's right, centered on the card, that copies the result.
            var copy = Descendants<Button>(frame).Single();
            var button = Bounds(copy);
            Assert.Equal((24, 24), (button.Width, button.Height));
            Assert.Equal(card.Right - 18, button.Right, 3);
            Assert.Equal(card.Top + card.Height / 2, button.Top + 12, 0.3);
            Assert.Equal("Copy result", System.Windows.Automation.AutomationProperties.GetName(copy));
            Assert.Equal(Color.FromRgb(0x23, 0x23, 0x23), Assert.IsType<SolidColorBrush>(copy.Background).Color);
            Assert.False(SurfaceDrag.CanDragFrom(copy, panel.IsMessage));
            Assert.Equal((1, 0), (Part<ShapePath>(copy, "CopyGlyph").Opacity, Part<ShapePath>(copy, "CheckGlyph").Opacity));
            copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            copy.Command.Execute(null);
            Assert.Equal(["19"], clipboard.Copied);
            WaitUntil(() => Part<ShapePath>(copy, "CheckGlyph").Opacity == 1, "Copying did not show the check mark.");
            Assert.Equal(0, Part<ShapePath>(copy, "CopyGlyph").Opacity);
            WaitUntil(() => Part<ShapePath>(copy, "CopyGlyph").Opacity == 1, "The copy glyph did not come back.");

            RenderGlass(panel, "result-calculation.png", 2);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void GalleryShowsImagesThreeAcrossWithoutACard() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel(answers: new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now)));
        model.StartNew("Find the image I took yesterday");
        try
        {
            panel.ShowConversation();
            var area = Named<Grid>(panel, "ConversationLayer");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            RenderGlass(panel, "result-photos.png", 2);

            // As wide as a card and as far below the text, on dark gray with a card's corners, but in no card frame.
            var prose = TextNamed(area, "I found 4 photos from yesterday.");
            var gallery = Descendants<GalleryPanel>(area).Single();
            var images = Descendants<ItemsControl>(area).Single(control => control.ItemsSource is IReadOnlyList<ImageItem> && control.Name is not ("Attachments" or "ComposerAttachments"));
            var host = Assert.IsType<Grid>(VisualTreeHelper.GetParent(images));
            var bounds = BoundsIn(area, gallery);
            Assert.Equal((16, 386), (bounds.Left, bounds.Width));
            Assert.Equal(BoundsIn(area, prose).Bottom + 19.5, bounds.Top, 3);
            Assert.Equal(Color.FromRgb(0x1E, 0x1E, 0x1E), Assert.IsType<SolidColorBrush>(host.Background).Color);
            Assert.Equal(40.5, CornerClip.GetCornerSize(host));
            Assert.False(host.Clip.FillContains(new Point(1, 1)));
            Assert.DoesNotContain(Descendants<ContentControl>(area), control => control.Content is MessageCard);

            // Square tiles three across, 1 apart, continuing on a second row.
            var tiles = Descendants<Image>(gallery).Select(image => BoundsIn(area, (FrameworkElement)VisualTreeHelper.GetParent(image))).ToArray();
            Assert.Equal(4, tiles.Length);
            Assert.All(tiles, tile => Assert.Equal((128, 128), (tile.Width, tile.Height)));
            Assert.Equal([16, 145, 274, 16], tiles.Select(tile => Math.Round(tile.Left, 3)));
            Assert.Equal([0, 0, 0, 129], tiles.Select(tile => Math.Round(tile.Top - bounds.Top, 3)));
            Assert.Equal(257, bounds.Height, 3);

            // Each image fills its tile, cropped around its center, and is named for assistive technology.
            var portrait = Descendants<Image>(gallery).ElementAt(1);
            var image = BoundsIn(area, portrait);
            Assert.Equal(128, image.Width, 3);
            Assert.Equal(128 * 480 / 360.0, image.Height, 3);
            Assert.Equal(tiles[1].Top + tiles[1].Height / 2, image.Top + image.Height / 2, 3);
            Assert.Equal("Sample photo of a mountain lake", System.Windows.Automation.AutomationProperties.GetName(portrait));
            var tile = (FrameworkElement)VisualTreeHelper.GetParent(portrait);
            Assert.Equal("Sample photo of a mountain lake",
                System.Windows.Automation.AutomationProperties.GetName(Assert.IsType<ContentPresenter>(VisualTreeHelper.GetParent(tile))));
            Assert.False(SurfaceDrag.CanDragFrom(portrait, panel.IsMessage));

            // Five screenshots fill a row and part of the next.
            model.StartNew("Show me the last 5 screenshots I took");
            Pump();
            var screenshots = Descendants<GalleryPanel>(area).Single();
            Assert.Equal(5, Descendants<Image>(screenshots).Count());
            Assert.Equal(257, screenshots.ActualHeight, 3);
            RenderGlass(panel, "result-screenshots.png", 2);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void CodeAndFilesHaveTheirOwnPresentations() => RunSta(() => WithTheme(() =>
    {
        var clipboard = new FakeClipboard();
        var (panel, model, _) = CreatePanel(answers: new DemoAnswerProvider(clipboard, new FixedClock(Now)));
        model.StartNew("demo code");
        try
        {
            panel.ShowConversation();
            var area = Named<Grid>(panel, "ConversationLayer");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            RenderGlass(panel, "result-code.png", 2);

            // Code is monospaced on its own dark gray surface, lined up with the prose, between two paragraphs.
            var code = Descendants<TextBlock>(area).Single(text => text.Text.StartsWith("static int Add", StringComparison.Ordinal));
            Assert.Contains("Cascadia Mono", code.FontFamily.Source);
            Assert.Equal(TextWrapping.Wrap, code.TextWrapping);
            Assert.Equal(30, BoundsIn(area, code).Left, 3);
            var block = Descendants<PanelShape>(area).Single(shape => IsInside(code, area, element => element == VisualTreeHelper.GetParent(shape)));
            var blockBounds = BoundsIn(area, block);
            Assert.Equal((16, 386, 22.0), (blockBounds.Left, blockBounds.Width, block.CornerSize));
            Assert.Equal(Color.FromRgb(0x1E, 0x1E, 0x1E), Assert.IsType<SolidColorBrush>(block.Fill).Color);
            Assert.DoesNotContain(Descendants<ContentControl>(area), control => control.Content is MessageCard);
            var before = TextNamed(area, "Here's a C# method that adds two numbers, and a line that calls it:");
            var after = TextNamed(area, "Calling Add(9, 10) prints 19.");
            Assert.Equal(BoundsIn(area, before).Bottom + 19.5, blockBounds.Top, 3);
            Assert.Equal(blockBounds.Bottom + 19.5, BoundsIn(area, after).Top, 3);
            Assert.Equal("C#", TextNamed(area, "C#").Text);
            var copy = Descendants<Button>(area).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Copy code");
            copy.Command.Execute(null);
            Assert.StartsWith("static int Add(int first, int second)", Assert.Single(clipboard.Copied));

            // Files are rows with an icon, a name, where and when, and any matching text, on a faint layer.
            model.StartNew("demo files");
            Pump();
            RenderGlass(panel, "result-files.png", 2);
            var names = new[] { "Budget 2026.xlsx", "Budget notes.docx", "Trip budget.pdf", "Finance" };
            var rows = names.Select(name => BoundsIn(area, TextNamed(area, name))).ToArray();
            Assert.All(rows, row => Assert.Equal(74, row.Left, 3));
            Assert.Equal(names, Descendants<ContentPresenter>(area).Where(presenter => presenter.Content is FileItem)
                .Select(presenter => System.Windows.Automation.AutomationProperties.GetName(presenter)));
            Assert.True(rows.Zip(rows.Skip(1)).All(pair => pair.Second.Top > pair.First.Bottom));
            Assert.Equal(["XLSX", "DOCX", "PDF"], Descendants<TextBlock>(area).Where(text => text.Text is "XLSX" or "DOCX" or "PDF").Select(text => text.Text));
            Assert.Equal(Visibility.Visible, TextNamed(area, "Documents · Today, " + Now.AddHours(-2).ToString("t")).Visibility);
            var list = Descendants<PanelShape>(area).Single(shape => shape.Fill is SolidColorBrush { Color.A: 0x12 });
            Assert.Equal((16, 386), (BoundsIn(area, list).Left, BoundsIn(area, list).Width));
            var separators = Descendants<Border>(area).Where(border => border.Name == "Separator").ToArray();
            Assert.Equal([Visibility.Collapsed, Visibility.Visible, Visibility.Visible, Visibility.Visible],
                separators.Select(separator => separator.Visibility));
            var snippets = Descendants<TextBlock>(area).Where(text => text.Name == "Snippet").Select(text => text.Visibility);
            Assert.Equal([Visibility.Visible, Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed], snippets);
            Assert.Equal(Visibility.Visible, Descendants<ShapePath>(area).Last(path => path.Name == "Glyph").Visibility);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void ProseStaysUnboxedAmongEveryKindOfContent() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew("Synthetic question");
        var answer = new MessageViewModel(MessageRole.Assistant, "Synthetic prose.");
        answer.Content.Add(new CalculationResult("1 + 1", "2"));
        answer.Content.Add(new CodeContent("synthetic();"));
        answer.Content.Add(new ImageCollection([new ImageItem("Synthetic image", new WriteableBitmap(8, 8, 96, 96, PixelFormats.Pbgra32, null))]));
        answer.Content.Add(new TextContent("More synthetic prose."));
        answer.Content.Add(new FileCollection([new FileItem(SearchResultItemType.File, "synthetic.txt", "C:\\synthetic.txt")]));
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            Pump();
            var area = Named<Grid>(panel, "ConversationLayer");
            var transcript = Named<FadingScrollViewer>(panel, "Transcript");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            RenderGlass(panel, "result-kinds.png", 2);

            // Only the calculation is in a card frame, and prose is never inside any surface.
            Assert.Single(Descendants<ContentControl>(transcript), control => control.Content is MessageCard);
            foreach (var prose in new[] { "Synthetic prose.", "More synthetic prose." })
            {
                Assert.Equal(30, BoundsIn(area, TextNamed(area, prose)).Left, 3);
                Assert.False(IsInside(TextNamed(area, prose), transcript, element => element is ContentControl { Content: MessageCard } or PanelShape));
            }

            // Wide parts sit 19.5 from prose and 12 from each other, all 16 in from the panel's edges.
            var presenters = Descendants<ContentPresenter>(transcript)
                .Where(presenter => VisualTreeHelper.GetParent(presenter) is MessageBlockPanel && presenter.Content is MessageContent)
                .ToArray();
            Assert.Equal(6, presenters.Length);
            var parts = presenters.Select(presenter => BoundsIn(area, presenter)).ToArray();
            Assert.Equal(parts[0].Bottom + 19.5, parts[1].Top, 3);
            Assert.Equal(parts[1].Bottom + 12, parts[2].Top, 3);
            Assert.Equal(parts[2].Bottom + 12, parts[3].Top, 3);
            Assert.Equal(parts[3].Bottom + 19.5, parts[4].Top, 3);
            Assert.Equal(parts[4].Bottom + 19.5, parts[5].Top, 3);
            foreach (var index in new[] { 1, 2, 3, 5 })
            {
                var child = (FrameworkElement)VisualTreeHelper.GetChild(presenters[index], 0);
                Assert.Equal((16, 386), (BoundsIn(area, child).Left, BoundsIn(area, child).Width));
            }
        }
        finally { panel.Close(); }
    }));

    private static T Part<T>(Control control, string name) where T : class =>
        Assert.IsType<T>(control.Template.FindName(name, control));

    private static void SaveImage(string path, BitmapEncoder encoder, int width, int height, ushort orientation = 1)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, width, height));
            context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 40, 40));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        BitmapMetadata? metadata = null;
        if (orientation != 1)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        }

        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0), null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool IsRed(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[2] > 200 && pixel[1] < 80 && pixel[0] < 80;
    }

    private sealed class FakeAnswers : IAnswerProvider
    {
        public Func<string, MessageViewModel?> Reply { get; set; } = _ => null;
        public MessageViewModel? Answer(string question) => Reply(question);
    }

    private sealed class FakeClipboard : ITextClipboard
    {
        public List<string> Copied { get; } = [];
        public List<string> Files { get; } = [];
        public bool TrySetText(string text)
        {
            Copied.Add(text);
            return true;
        }

        public bool TrySetFiles(IReadOnlyList<string> paths)
        {
            Files.AddRange(paths);
            return true;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("Test", now.Offset, "Test", "Test");
    }
}
