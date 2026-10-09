using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ImageSearch;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Capture;
using Assistant.UI.Controls;
using Assistant.UI.Settings;
using Assistant.UI.ImageSearch;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Image search: the permission flow's words, the sample provider, the results panel (PROJECT_SPEC §4.6, steps 78-79) --------------------

    private sealed class RecordingUrls : IUrlLauncher
    {
        public List<Uri> Opened { get; } = [];

        public bool Open(Uri url)
        {
            Opened.Add(url);
            return true;
        }
    }

    private sealed class RecordingResultsWindow : IImageSearchResultsWindow
    {
        public List<ImageSearchResultsViewModel> Shown { get; } = [];

        public void ShowResults(ImageSearchResultsViewModel results) => Shown.Add(results);
    }

    private static byte[] ThumbnailPng(int width, int height, Color color)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static ImageSearchResults RealResults(int count = 3) => new(
        "Example",
        false,
        [
            .. Enumerable.Range(0, count).Select(index => new ImageSearchResult(
                $"Result {index}", index % 2 == 0 ? "YouTube" : "Chrome Web Store", new Uri($"https://example.com/{index}"),
                ThumbnailPng(index % 2 == 0 ? 320 : 180, index % 2 == 0 ? 240 : 300, Colors.SteelBlue))),
        ]) { ReportUrl = new Uri("https://example.com/report") };

    // ---- What the chip says ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheImageSearchChip_SaysWhyItIsOff_AndWhatItDoesWhenItIsOn()
    {
        var localOnly = ImageSearchChipText.For(new ImageSearchAvailability(ImageSearchBlock.LocalOnly, "Sample", true));
        var permission = ImageSearchChipText.For(new ImageSearchAvailability(ImageSearchBlock.PermissionOff, "Example", false));
        var none = ImageSearchChipText.For(new ImageSearchAvailability(ImageSearchBlock.NoProvider, "", false));
        var sample = ImageSearchChipText.For(new ImageSearchAvailability(ImageSearchBlock.None, "Sample", true));
        var real = ImageSearchChipText.For(new ImageSearchAvailability(ImageSearchBlock.None, "Example", false));

        Assert.Equal(new ChipAvailability(false, ImageSearchChipText.LocalOnly), localOnly);
        Assert.Contains("Local Only", localOnly.Hint, StringComparison.Ordinal);
        Assert.Contains("Privacy", localOnly.Hint, StringComparison.Ordinal);
        Assert.Equal(new ChipAvailability(false, ImageSearchChipText.PermissionOff), permission);
        Assert.Contains("Permissions", permission.Hint, StringComparison.Ordinal);
        Assert.False(none.IsEnabled);
        Assert.True(sample.IsEnabled);
        Assert.Contains("sample", sample.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing is sent", sample.Hint, StringComparison.Ordinal);
        Assert.True(real.IsEnabled);
        Assert.Contains("Example", real.Hint, StringComparison.Ordinal);
        Assert.Contains("confirm", real.Hint, StringComparison.Ordinal);
    }

    // ---- The sample provider ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSampleProvider_MakesUpResults_SendsNothing_AndStillNeedsTheFlowsConsent() => RunSta(() =>
    {
        var provider = new PlaceholderImageSearchService(TimeProvider.System);
        var flowResults = new List<ImageSearchResults>();
        var settings = new InMemorySettingsService();
        settings.SaveAsync(new AppSettings
        {
            Privacy = new PrivacySettings { LocalOnly = false },
            Permissions = new PermissionSettings { ExternalSearch = true },
        }).GetAwaiter().GetResult();
        var flow = new ImageSearchFlow(settings, new SettingsPermissionPolicy(settings), provider, null, TimeProvider.System);

        var outcome = Task.Run(() => flow.SearchAsync(new byte[] { 1, 2, 3 })).GetAwaiter().GetResult();

        Assert.False(((IImageSearchService)provider).SendsImageOffPc);
        Assert.Equal(ImageSearchOutcomeKind.Results, outcome.Kind);
        var results = outcome.Results!;
        flowResults.Add(results);
        Assert.True(results.IsSample);
        Assert.Equal(PlaceholderImageSearchService.Name, results.ProviderName);
        Assert.Equal(6, results.Results.Count);
        Assert.All(results.Results, result =>
        {
            Assert.Null(result.PageUrl);
            Assert.False(string.IsNullOrWhiteSpace(result.Title));
            Assert.NotEmpty(result.Thumbnail.ToArray());
            Assert.NotNull(result.ThumbnailSize);
        });

        // Without the flow's say-so there is nothing to search with.
        Assert.ThrowsAny<Exception>(() => provider.SearchAsync(new ImageSearchRequest(new byte[] { 1 }, null!)).GetAwaiter().GetResult());
    });

    // ---- The view of the results -----------------------------------------------------------------------------------------------------

    [Fact]
    public void RealResults_AreHeadedWithTheProvider_AndASampleSaysSoAndOpensNothing() => RunSta(() =>
    {
        var urls = new RecordingUrls();
        var real = new ImageSearchResultsViewModel(RealResults(), urls);
        var sample = new ImageSearchResultsViewModel(
            new ImageSearchResults("Sample", true, [new ImageSearchResult("A sample", "example.org", null, ReadOnlyMemory<byte>.Empty)]), urls);

        Assert.Equal("Results from Example", real.Heading);
        Assert.False(real.HasNotice);
        Assert.Equal(string.Empty, real.Notice);
        Assert.Equal(3, real.Cards.Count);
        Assert.True(real.Cards.All(card => card.CanOpen && card.Thumbnail is not null));
        Assert.Equal("Sample results", sample.Heading);
        Assert.True(sample.HasNotice);
        Assert.Contains("nothing was sent", sample.Notice, StringComparison.Ordinal);
        Assert.False(sample.Cards[0].CanOpen);
        Assert.Null(sample.Cards[0].Thumbnail);
        Assert.Contains("a sample", sample.Cards[0].AutomationName, StringComparison.Ordinal);

        // A click opens the card's page in the browser, only when it has one.
        real.OpenCommand.Execute(real.Cards[1]);
        sample.OpenCommand.Execute(sample.Cards[0]);
        real.OpenCommand.Execute("not a card");
        Assert.Equal([new Uri("https://example.com/1")], urls.Opened);
    });

    [Fact]
    public void ReportAConcern_OpensTheProvidersPage_WhenThereIsOne_AndIsOffWhenThereIsNone() => RunSta(() =>
    {
        var urls = new RecordingUrls();
        var real = new ImageSearchResultsViewModel(RealResults(), urls);
        var sample = new ImageSearchResultsViewModel(new ImageSearchResults("Sample", true, []), urls);

        Assert.True(real.CanReport);
        Assert.True(real.ReportCommand.CanExecute(null));
        real.ReportCommand.Execute(null);
        Assert.Equal([new Uri("https://example.com/report")], urls.Opened);

        Assert.False(sample.CanReport);
        Assert.False(sample.ReportCommand.CanExecute(null));
        Assert.False(sample.HasResults);
        Assert.Equal("No matches were found.", sample.EmptyText);
        Assert.NotEmpty(sample.ReportHint);
    });

    [Fact]
    public void ACardsShape_IsItsThumbnailsOrTheProvidersWord_WithinLimits_AndFourByThreeWithoutOne() => RunSta(() =>
    {
        var wide = new ImageSearchCardViewModel(new ImageSearchResult("a", "s", null, ThumbnailPng(320, 160, Colors.Red)));
        var tall = new ImageSearchCardViewModel(new ImageSearchResult("a", "s", null, ThumbnailPng(100, 400, Colors.Red)));
        var told = new ImageSearchCardViewModel(new ImageSearchResult("a", "s", null, ReadOnlyMemory<byte>.Empty) { ThumbnailSize = (360, 600) });
        var none = new ImageSearchCardViewModel(new ImageSearchResult("a", "s", null, ReadOnlyMemory<byte>.Empty));
        var broken = new ImageSearchCardViewModel(new ImageSearchResult("a", "s", null, new byte[] { 1, 2, 3, 4 }));

        Assert.Equal(2.0, wide.AspectRatio, 3);
        Assert.Equal(0.5, tall.AspectRatio, 3);
        Assert.Equal(0.6, told.AspectRatio, 3);
        Assert.Equal(4.0 / 3, none.AspectRatio, 6);
        Assert.Equal(4.0 / 3, broken.AspectRatio, 6);
        Assert.Null(broken.Thumbnail);

        // A site's mark is its first letter, in the same color every time.
        Assert.Equal("S", wide.MarkLetter);
        Assert.Equal(((SolidColorBrush)wide.MarkBrush).Color, ((SolidColorBrush)tall.MarkBrush).Color);
        Assert.Equal("?", new ImageSearchCardViewModel(new ImageSearchResult("a", " ", null, ReadOnlyMemory<byte>.Empty)).MarkLetter);
    });

    [Fact]
    public void TheStaggeredPanel_PutsEachCardUnderTheShortestColumn_AsTheReferenceDoes()
    {
        // The reference's heights, in order: the first card is short, the second tall, then cards that fill under the shorter column.
        double[] heights = [355 + 22, 700 + 22, 305 + 22, 270 + 22, 600];

        var places = StaggeredColumnsPanel.Place(heights, 2);

        Assert.Equal([0, 1, 0, 0, 1], places.Select(place => place.Column));
        Assert.Equal([0, 0, 377, 377 + 327, 722], places.Select(place => place.Top));
        Assert.Equal([(0, 0.0), (0, 10.0)], StaggeredColumnsPanel.Place([10, 10], 1).Select(place => (place.Column, place.Top)));
    }

    // ---- The window ------------------------------------------------------------------------------------------------------------------

    private static (ImageSearchResultsWindow Window, RecordingUrls Urls) ShownResults(ImageSearchResults results)
    {
        var urls = new RecordingUrls();
        var window = new ImageSearchResultsWindow(new FakeFrameFactory(), new FakePlacement())
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        window.ShowResults(new ImageSearchResultsViewModel(results, urls));
        window.UpdateLayout();
        Pump();
        return (window, urls);
    }

    [Fact]
    public void TheResultsWindow_ShowsTheHeading_TheCardsInTwoColumns_AndTheLink() => RunSta(() => WithTheme(() =>
    {
        var (window, _) = ShownResults(RealResults(5));
        try
        {
            Assert.Equal("Results from Example", ((TextBlock)window.FindName("Heading")).Text);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("Notice")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("EmptyState")).Visibility);
            var cards = (ItemsControl)window.FindName("Cards");
            var panel = Descendants<StaggeredColumnsPanel>(cards).Single();
            var tiles = Descendants<AspectRatioTile>(cards).ToList();
            Assert.Equal(5, tiles.Count);

            // Two columns, 16 apart, each as wide as the content allows: the panel is 412.5 wide, with 20 and 20.5 around the cards.
            var width = panel.ActualWidth;
            Assert.Equal((width - 16) / 2, tiles[0].ActualWidth, 2);
            Assert.Equal(tiles[0].ActualWidth, tiles[1].ActualWidth, 2);
            var first = tiles[0].TransformToAncestor(window).Transform(default);
            var second = tiles[1].TransformToAncestor(window).Transform(default);
            Assert.Equal(first.Y, second.Y, 1);
            Assert.Equal(tiles[0].ActualWidth + 16, second.X - first.X, 1);

            // Each tile is as tall as its thumbnail's shape says, with the card's corners.
            Assert.Equal(tiles[0].ActualWidth * 240 / 320, tiles[0].ActualHeight, 1);
            Assert.Equal(tiles[1].ActualWidth * 300 / 180, tiles[1].ActualHeight, 1);
            Assert.Equal(14, CornerClip.GetCornerSize(tiles[0]));

            var report = (Button)window.FindName("ReportButton");
            Assert.True(report.IsEnabled);
            Assert.Equal("Report a Concern", System.Windows.Automation.AutomationProperties.GetName(report));
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void ASampleWindow_SaysItIsMadeUp_AndItsLinkIsOff() => RunSta(() => WithTheme(() =>
    {
        var (window, _) = ShownResults(new ImageSearchResults("Sample", true, [new ImageSearchResult("A sample", "example.org", null, ReadOnlyMemory<byte>.Empty)]));
        try
        {
            Assert.Equal("Sample results", ((TextBlock)window.FindName("Heading")).Text);
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("Notice")).Visibility);
            Assert.False(((Button)window.FindName("ReportButton")).IsEnabled);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void NothingFound_IsSaidInTheWindow() => RunSta(() => WithTheme(() =>
    {
        var (window, _) = ShownResults(new ImageSearchResults("Example", false, []));
        try
        {
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("EmptyState")).Visibility);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void EscPutsTheWindowAway_AndTheResultsAreNotKept_AndANewSearchShowsInTheSameWindow() => RunSta(() => WithTheme(() =>
    {
        var (window, urls) = ShownResults(RealResults());
        try
        {
            Assert.NotNull(window.Results);
            var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = window,
            };
            window.RaiseEvent(press);

            Assert.False(window.IsVisible);
            Assert.Null(window.Results);

            window.ShowResults(new ImageSearchResultsViewModel(RealResults(2), urls));
            Assert.True(window.IsVisible);
            Assert.Equal(2, window.Results!.Cards.Count);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void AClickOnACard_OpensItsPage() => RunSta(() => WithTheme(() =>
    {
        var (window, urls) = ShownResults(RealResults());
        try
        {
            var card = Descendants<Button>((ItemsControl)window.FindName("Cards")).First();
            card.Command.Execute(card.CommandParameter);

            Assert.Equal([new Uri("https://example.com/0")], urls.Opened);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    // ---- What the panel looks like --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheResultsPanelIsLaidOutAsInTheReference() => RunSta(() => WithTheme(() =>
    {
        // The reference's results, in its shapes: 4:3, then a tall one, then 16:10, wide, and a tall one again.
        var results = new ImageSearchResults(
            "Google",
            false,
            [
                new ImageSearchResult("YouTube", "YouTube", null, ThumbnailPng(640, 480, Color.FromRgb(210, 60, 60))),
                new ImageSearchResult("White screen - YouTube", "YouTube", null, ThumbnailPng(360, 600, Colors.White)),
                new ImageSearchResult("White Screen Online", "whitescreenonline.hashnode.dev", null, ThumbnailPng(640, 330, Colors.WhiteSmoke)),
                new ImageSearchResult("Blank White Screen - Chrome Web Store", "Chrome Web Store - Google", null, ThumbnailPng(640, 400, Color.FromRgb(80, 110, 250))),
                new ImageSearchResult("Test screen", "Test", null, ThumbnailPng(360, 640, Colors.WhiteSmoke)),
            ]) { ReportUrl = new Uri("https://example.com/report") };
        var (window, _) = ShownResults(results);
        try
        {
            window.Width = 412.5;
            window.Height = 664;
            window.UpdateLayout();
            Pump();
            var root = (FrameworkElement)window.FindName("Root");
            RenderFixture(root, "image-search-results-2x.png", 2, new Rect(0, 0, 412.5, 664));

            var scroller = (FadingScrollViewer)window.FindName("Scroller");
            var cards = (ItemsControl)window.FindName("Cards");
            var tiles = Descendants<AspectRatioTile>(cards).ToList();

            // The cards sit where the reference's do: columns 178 wide from 20 DIPs in, the first 81.5 down.
            var first = tiles[0].TransformToAncestor(root).Transform(default);
            var second = tiles[1].TransformToAncestor(root).Transform(default);
            Assert.Equal(20, first.X, 0.6);
            Assert.Equal(81.5, first.Y, 0.6);
            Assert.Equal(178, tiles[0].ActualWidth, 0.5);
            Assert.Equal(20 + 178 + 16, second.X, 0.6);
            Assert.True(scroller.ExtentHeight > scroller.ViewportHeight);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    // ---- The question before a picture leaves this PC ---------------------------------------------------------------------------------------

    [Fact]
    public void TheConfirmation_ShowsThePictureAndWhoGetsIt_CancelIsTheDefault_AndOnlySearchSaysYes() => RunSta(() => WithTheme(() =>
    {
        var window = new ImageSearchConfirmationWindow(new ImageSearchDisclosure("Example Search", ThumbnailPng(80, 60, Colors.Teal)))
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.NotNull(((Image)window.FindName("Picture")).Source);
            Assert.Contains("Example Search", ((TextBlock)window.FindName("Body")).Text, StringComparison.Ordinal);
            Assert.True(window.CancelChoice.IsDefault);
            Assert.True(window.CancelChoice.IsCancel);
            Assert.False(window.SearchChoice.IsDefault);
            Assert.False(window.Confirmed);
        }
        finally
        {
            window.Close();
        }

        Assert.False(window.Confirmed);
    }));

    [Fact]
    public void PressingSearch_IsTheOnlyWayTheConfirmationSaysYes() => RunSta(() => WithTheme(() =>
    {
        var window = new ImageSearchConfirmationWindow(new ImageSearchDisclosure("Example Search", ThumbnailPng(80, 60, Colors.Teal)))
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        window.Show();
        window.SearchChoice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, window.SearchChoice));

        Assert.True(window.Confirmed);
        Assert.False(window.IsVisible);

        var other = new ImageSearchConfirmationWindow(new ImageSearchDisclosure("Example Search", ThumbnailPng(80, 60, Colors.Teal)))
        {
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        other.Show();
        other.CancelChoice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, other.CancelChoice));
        Assert.False(other.Confirmed);
    }));

    // ---- What happens when the chip is pressed -----------------------------------------------------------------------------------------

    private sealed class LauncherSetup
    {
        public required ImageSearchLauncher Launcher { get; init; }
        public required RecordingResultsWindow Window { get; init; }
        public required ConversationViewModel Conversation { get; init; }
        public required FakeShell Shell { get; init; }
        public required InMemorySettingsService Settings { get; init; }
        public required SelectableProvider Provider { get; init; }
        public required SelectableConfirmation Confirmation { get; init; }
    }

    private sealed class SelectableProvider(bool sample) : IImageSearchService
    {
        public string ProviderName => "Example";
        public bool IsSample => sample;
        public List<ImageSearchRequest> Requests { get; } = [];
        public Exception? Failure { get; set; }

        public Task<ImageSearchResults> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            request.Consent.Consume(request.Image, DateTimeOffset.UtcNow, ((IImageSearchService)this).SendsImageOffPc);
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new ImageSearchResults("Example", sample, [new ImageSearchResult("A", "B", null, ReadOnlyMemory<byte>.Empty)]));
        }
    }

    private sealed class SelectableConfirmation(bool answer) : IImageSearchConfirmation
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmAsync(ImageSearchDisclosure disclosure, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(answer);
        }
    }

    private static LauncherSetup CreateLauncher(bool localOnly, bool permission, bool sample = false, bool confirms = true)
    {
        var settings = new InMemorySettingsService();
        settings.SaveAsync(new AppSettings
        {
            Privacy = new PrivacySettings { LocalOnly = localOnly },
            Permissions = new PermissionSettings { ExternalSearch = permission },
        }).GetAwaiter().GetResult();
        var provider = new SelectableProvider(sample);
        var confirmation = new SelectableConfirmation(confirms);
        var flow = new ImageSearchFlow(settings, new SettingsPermissionPolicy(settings), provider, confirmation, TimeProvider.System);
        var shell = new FakeShell();
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), new RecordingAnswers());
        var windows = new AssistantWindowStateController(shell, CreateBarModel(), conversation);
        var window = new RecordingResultsWindow();
        return new LauncherSetup
        {
            Launcher = new ImageSearchLauncher(flow, window, new RecordingUrls(), conversation, windows),
            Window = window, Conversation = conversation, Shell = shell, Settings = settings, Provider = provider, Confirmation = confirmation,
        };
    }

    private static void Search(LauncherSetup setup)
    {
        using var region = CodedSnapshot(60, 40);
        var task = setup.Launcher.SearchAsync(region);
        WaitUntil(() => task.IsCompleted, "The search did not end.");
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void WithLocalOnlyOn_PressingTheChipSendsNothing_AndTheConversationSaysWhy() => RunSta(() =>
    {
        var setup = CreateLauncher(localOnly: true, permission: true);

        Search(setup);

        Assert.Empty(setup.Provider.Requests);
        Assert.Equal(0, setup.Confirmation.Asked);
        Assert.Empty(setup.Window.Shown);
        Assert.Contains("Local Only", Assert.Single(setup.Conversation.Messages).Text, StringComparison.Ordinal);
        Assert.Single(setup.Shell.ConversationsShown);
    });

    [Fact]
    public void WhenSearchingIsAllowedAndConfirmed_TheResultsAreShownInTheirWindow() => RunSta(() =>
    {
        var setup = CreateLauncher(localOnly: false, permission: true);

        Search(setup);

        Assert.Equal(1, setup.Confirmation.Asked);
        Assert.Single(setup.Provider.Requests);
        Assert.Equal("Results from Example", Assert.Single(setup.Window.Shown).Heading);
        Assert.Empty(setup.Conversation.Messages);
    });

    [Fact]
    public void WhenTheUserSaysNo_NothingIsSent_AndNothingIsSaid() => RunSta(() =>
    {
        var setup = CreateLauncher(localOnly: false, permission: true, confirms: false);

        Search(setup);

        Assert.Equal(1, setup.Confirmation.Asked);
        Assert.Empty(setup.Provider.Requests);
        Assert.Empty(setup.Window.Shown);
        Assert.Empty(setup.Conversation.Messages);
    });

    [Fact]
    public void AProviderThatFails_IsSaidInTheConversation_AndASampleIsNotAskedAbout() => RunSta(() =>
    {
        var failing = CreateLauncher(localOnly: false, permission: true);
        failing.Provider.Failure = new ImageSearchException();
        Search(failing);
        Assert.Equal(ImageSearchChipText.Failed, Assert.Single(failing.Conversation.Messages).Text);
        Assert.Empty(failing.Window.Shown);

        var sample = CreateLauncher(localOnly: false, permission: true, sample: true, confirms: false);
        Search(sample);
        Assert.Equal(0, sample.Confirmation.Asked);
        Assert.True(Assert.Single(sample.Window.Shown).IsSample);
    });

    [Fact]
    public void TheChipFollowsTheSettingsAsTheyAreSavedNow() => RunSta(() =>
    {
        var setup = CreateLauncher(localOnly: true, permission: true);

        var off = setup.Launcher.GetChipAvailabilityAsync(CancellationToken.None).GetAwaiter().GetResult();
        setup.Settings.SaveAsync(new AppSettings
        {
            Privacy = new PrivacySettings { LocalOnly = false },
            Permissions = new PermissionSettings { ExternalSearch = true },
        }).GetAwaiter().GetResult();
        var on = setup.Launcher.GetChipAvailabilityAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(off.IsEnabled);
        Assert.True(on.IsEnabled);
        Assert.Contains("Example", on.Hint, StringComparison.Ordinal);
    });

    // ---- The sample commands and a conversation that has a screenshot ----------------------------------------------------------------------

    private sealed class RecordingVisualDemo : IVisualIntelligenceDemo
    {
        public int Captures { get; private set; }
        public int Results { get; private set; }

        public void StartCapture() => Captures++;

        public void ShowSampleResults() => Results++;
    }

    [Fact]
    public void TheCaptureAndResultsSampleCommands_StartTheirWindows_AndTheDemoListNamesThem() => RunSta(() =>
    {
        var visual = new RecordingVisualDemo();
        var demo = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now), visual: visual);
        var bare = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now));

        var capture = demo.Answer("Demo capture")!;
        var results = demo.Answer("demo results")!;

        Assert.Equal((1, 1), (visual.Captures, visual.Results));
        Assert.Contains("Visual Intelligence", capture.Text, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", results.Text, StringComparison.Ordinal);
        Assert.Contains("not available", bare.Answer("demo capture")!.Text, StringComparison.Ordinal);
        Assert.Contains("not available", bare.Answer("demo results")!.Text, StringComparison.Ordinal);
        Assert.Contains("demo capture", demo.Answer("demo")!.Text, StringComparison.Ordinal);
        Assert.Contains("demo results", demo.Answer("demo")!.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void WhenAScreenshotIsInTheConversation_WhatTheUserSaysGoesToTheModel_NotToTheFileRules() => RunSta(() =>
    {
        var chat = CreateScreenConversation();
        var rules = new ScriptedFileRequests
        {
            IsRequest = _ => true,
            Likely = _ => true,
            Result = Found(new FileSearchQuery { Filename = "error" }, FoundFile(@"C:\Docs\error.docx")),
        };
        var demo = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now), localModel: chat.Provider, files: CreateAnswers(rules));
        var shown = new List<MessageViewModel>();

        var asked = demo.StreamAnswerAsync(chat.Conversation.Id, "find the error", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Empty(rules.Found);
        Assert.Equal("find the error", Assert.Single(chat.Model.Requests).Messages[^1].Text);
        Assert.Single(Assert.Single(chat.Model.Requests).Images);
    });

    [Fact]
    public void TheLocalOnlySwitch_IsOnByDefault_AndSavesWhenTheUserSwitchesItOff() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            var localOnly = Assert.IsType<CheckBox>(SettingsControl(window, kit, SettingsSection.Privacy, "Local Only"));

            Assert.True(localOnly.IsChecked);
            Assert.True(kit.Model.Privacy.LocalOnly);
            ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(localOnly)).Toggle();
            kit.Settle();

            Assert.False(kit.Saved.Privacy.LocalOnly);
        }
        finally
        {
            window.CloseForGood();
        }
    })));
}
