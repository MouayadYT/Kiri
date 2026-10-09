using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Ipc;
using Assistant.Core.Orchestration;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Browser;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.Selection;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Imaging;
using Assistant.Windows.Placement;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Text selected in a browser opens the Ask panel (PROJECT_SPEC §4.5) -----------------------------------------------------

    private const string BrowserWords = "Foxes are omnivores and hunt mostly at dusk.";

    private static readonly BrowserSelection FoxSelection = new(
        BrowserWords, IsTruncated: false, "A page about foxes", "https://example.test/foxes?q=1", "Microsoft Edge");

    private sealed class BrowserSetup
    {
        public required AskBrowserSelectionController Controller { get; init; }
        public required ConversationViewModel Conversation { get; init; }
        public required FakePermissions Permissions { get; init; }
        public required FakeShell Window { get; init; }
        public required RecordingAnswers Answers { get; init; }
        public required CollectingLogger<AskBrowserSelectionController> Log { get; init; }
        public required List<string> Order { get; init; }
    }

    private static BrowserSetup CreateAskBrowserSelection(bool allowed = true)
    {
        var order = new List<string>();
        var permissions = new FakePermissions(allowed) { Order = order };
        var window = new FakeShell { Order = order };
        var answers = new RecordingAnswers();
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), answers);
        var windows = new AssistantWindowStateController(window, CreateBarModel(), conversation);
        var log = new CollectingLogger<AskBrowserSelectionController>();
        return new BrowserSetup
        {
            Controller = new AskBrowserSelectionController(permissions, windows, conversation, log),
            Conversation = conversation, Permissions = permissions, Window = window, Answers = answers, Log = log, Order = order,
        };
    }

    private static readonly NearWindowTarget Browser = new(
        0x1234, 4242, new ScreenRect(100, 50, 1700, 1000), new ScreenPoint(300, 400));

    private static void Open(BrowserSetup setup, BrowserSelection selection, NearWindowTarget? browser = null)
    {
        var running = setup.Controller.OpenAsync(selection, browser);
        WaitUntil(() => running.IsCompleted, "The browser selection was not opened.");
    }

    [Fact]
    public void ASelectionFromTheBrowser_OpensThePanelWithTheTextTheTitleTheAddressAndTheBrowserAttached_AskingNothing() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();

        Open(setup, FoxSelection);

        // The permission was checked, then the panel opened and was given the keyboard (the browser is the window in front).
        Assert.Equal(["permission", "foreground"], setup.Order);
        Assert.Equal(PermissionCapability.SelectedText, Assert.Single(setup.Permissions.Asked));
        Assert.Single(setup.Window.ConversationsShown);
        Assert.Equal(1, setup.Window.ForegroundTaken);

        // One text, with where it is from; nothing is asked or typed, so the composer is what waits for the user, with the actions above it.
        var text = Assert.Single(setup.Conversation.Texts);
        Assert.Equal(BrowserWords, text.Text);
        Assert.Equal(new WebPageOrigin("A page about foxes", "https://example.test/foxes?q=1", "Microsoft Edge"), text.WebPage);
        Assert.Equal(AttachmentKind.Text, Assert.Single(setup.Conversation.Chips).Kind);
        Assert.Empty(setup.Conversation.Messages);
        Assert.Empty(setup.Answers.Asked);
        Assert.Equal("", setup.Conversation.Draft);
        Assert.True(setup.Conversation.CanCompose);
        Assert.True(setup.Conversation.ShowQuickActions);
        Assert.Equal(BrowserWords, setup.Conversation.SelectionPreview);
        Assert.Equal($"Selected text from Microsoft Edge · {BrowserWords.Length:N0} characters", setup.Conversation.SelectionCaption);
    });

    [Fact]
    public void ABrowserThatDidNotSayWhoItIs_IsCalledTheBrowser_AndAMissingTitleOrAddressIsLeftOut() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();

        Open(setup, new BrowserSelection(BrowserWords, false, "", ""));

        // Nothing is known about where it is from, so it is text like any other.
        Assert.Null(Assert.Single(setup.Conversation.Texts).WebPage);
        Assert.Equal($"Selected text · {BrowserWords.Length:N0} characters", setup.Conversation.SelectionCaption);

        Open(setup, new BrowserSelection(BrowserWords, false, "  A title  ", " https://example.test/ ", ""));
        var page = Assert.Single(setup.Conversation.Texts).WebPage!;
        Assert.Equal(new WebPageOrigin("A title", "https://example.test/", ""), page);
        Assert.Equal($"Selected text from the browser · {BrowserWords.Length:N0} characters", setup.Conversation.SelectionCaption);
    });

    [Fact]
    public void ASelectionThatWasCutIsSaidSo_AndANewOneReplacesTheConversationBefore() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();
        setup.Conversation.StartNew("an earlier question");
        var before = setup.Conversation.Id;

        Open(setup, FoxSelection with { IsTruncated = true });

        Assert.NotEqual(before, setup.Conversation.Id);
        Assert.Empty(setup.Conversation.Messages);
        Assert.Equal(AskSelectionController.TruncatedNotice, setup.Conversation.AttachNotice);

        Open(setup, FoxSelection with { Text = "Another sentence." });
        Assert.Equal("Another sentence.", Assert.Single(setup.Conversation.Texts).Text);
        Assert.Equal("", setup.Conversation.AttachNotice);
    });

    [Fact]
    public void WithSelectedTextTurnedOff_NothingIsAttached_AndThePanelSaysWhy() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection(allowed: false);

        Open(setup, FoxSelection);

        Assert.Empty(setup.Conversation.Texts);
        Assert.False(setup.Conversation.ShowQuickActions);
        var notice = Assert.Single(setup.Conversation.Messages);
        Assert.Equal(AskBrowserSelectionController.TurnedOffText, notice.Text);
        Assert.Equal(["permission", "foreground"], setup.Order);
        Assert.DoesNotContain(BrowserWords, string.Join("\n", setup.Log.Messages), StringComparison.Ordinal);
    });

    [Fact]
    public void TheLogSaysThatTheSelectionWasOpened_NeverWhatItWasOrWhereItIsFrom() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();

        Open(setup, FoxSelection);

        var logged = string.Join("\n", setup.Log.Messages);
        Assert.Contains("shown in the Ask panel", logged);
        Assert.DoesNotContain("Foxes", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("A page about foxes", logged, StringComparison.Ordinal);
    });

    // ---- Page text around the selection (step 88) --------------------------------------------------------------------------------

    private static readonly BrowserSelection FoxWithNearby = FoxSelection with
    {
        NearbyBefore = "Foxes are canids found on every continent but Antarctica.",
        NearbyAfter = "Their pups are born in spring.",
    };

    [Fact]
    public void WithNearbyContext_ThePanelSaysExtraPageTextIsIncluded_AndHowMuch() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();

        // Selection Only: the card says nothing about page text.
        Open(setup, FoxSelection);
        Assert.False(setup.Conversation.HasSelectionNearbyContext);
        Assert.Equal("", setup.Conversation.SelectionNearbyNotice);
        Assert.Equal("Text, " + BrowserWords.Length.ToString("N0") + " characters", Assert.Single(setup.Conversation.Chips).Detail);

        // Selection + Nearby Context: it does, in the card and on the chip, and the selection's own caption and preview are unchanged.
        Open(setup, FoxWithNearby);
        var length = FoxWithNearby.NearbyBefore.Length + FoxWithNearby.NearbyAfter.Length;
        Assert.True(setup.Conversation.HasSelectionNearbyContext);
        Assert.Equal($"Plus {length:N0} characters of nearby page text", setup.Conversation.SelectionNearbyNotice);
        Assert.Equal($"Selected text from Microsoft Edge · {BrowserWords.Length:N0} characters", setup.Conversation.SelectionCaption);
        Assert.Equal(BrowserWords, setup.Conversation.SelectionPreview);
        Assert.EndsWith("Plus " + length.ToString("N0") + " characters of nearby page text.", Assert.Single(setup.Conversation.Chips).Detail);
        Assert.True(Assert.Single(setup.Conversation.Texts).HasNearbyContext);
    });

    [Fact]
    public void ThePanelsSelectionCardShowsTheNearbyNoteOnlyWhenPageTextCameWithTheSelection() => RunSta(() => WithTheme(() =>
    {
        var (panel, _, model) = CreateAssistant();
        try
        {
            model.StartWithSelection(new TextAttachment(BrowserWords, webPage: new WebPageOrigin("A page", "https://example.test/", "Microsoft Edge")));
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            Assert.Equal(Visibility.Collapsed, Named<TextBlock>(panel, "SelectionNearbyText").Visibility);

            model.StartWithSelection(new TextAttachment(
                BrowserWords, webPage: new WebPageOrigin("A page", "https://example.test/", "Microsoft Edge", "Before it.", "After it.")));
            Pump();
            var note = Named<TextBlock>(panel, "SelectionNearbyText");
            Assert.Equal(Visibility.Visible, note.Visibility);
            Assert.Equal($"Plus {"Before it.".Length + "After it.".Length:N0} characters of nearby page text", note.Text);
            Assert.Equal(model.SelectionCaption, Named<TextBlock>(panel, "SelectionCaptionText").Text);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void ThePageTextIsCleanedAgainBeforeItIsKept_AndNothingReadableLeftMeansNoNearbyContext() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();

        Open(setup, FoxSelection with { NearbyBefore = "Be\u200Bfore\u202E  it.\u0007", NearbyAfter = "  \n\u200B" });

        var page = Assert.Single(setup.Conversation.Texts).WebPage!;
        Assert.Equal("Before it.", page.NearbyBefore);
        Assert.Equal("", page.NearbyAfter);
        Assert.True(page.HasNearbyContext);

        Open(setup, FoxSelection with { NearbyBefore = "\u200B\u200C", NearbyAfter = "\n\t " });
        Assert.False(Assert.Single(setup.Conversation.Texts).HasNearbyContext);
        Assert.False(setup.Conversation.HasSelectionNearbyContext);
    });

    [Fact]
    public void TheNearbyTextIsInMemoryOnly_AndTheLogOnlySaysWhetherThereWasSome() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();

        Open(setup, FoxWithNearby);

        var logged = string.Join("\n", setup.Log.Messages);
        Assert.Contains("shown in the Ask panel (page text around it: True)", logged);
        Assert.DoesNotContain("canids", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("pups", logged, StringComparison.Ordinal);
    });

    [Fact]
    public void AskingWithNearbyContext_GivesTheModelAPageBlockNextToTheSelection_AndSavesNeitherTheTextNorThePage() => RunSta(() =>
    {
        var model = new ChattyModel();
        var contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()));
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(contexts), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, contexts);
        var provider = new ModelAnswerProvider(orchestrator, new FixedClock(Now), null, null, contexts);
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), provider);
        var windows = new AssistantWindowStateController(new FakeShell(), CreateBarModel(), conversation);
        var controller = new AskBrowserSelectionController(
            new FakePermissions(true), windows, conversation, NullLogger<AskBrowserSelectionController>.Instance);
        var running = controller.OpenAsync(FoxWithNearby);
        WaitUntil(() => running.IsCompleted, "The browser selection was not opened.");

        conversation.Draft = "When do they hunt?";
        conversation.AskCommand.Execute(null);
        WaitUntil(() => model.Requests.Count == 1 && !conversation.IsAnswering, "The question was not answered.");

        var request = model.Requests.Single();
        var sent = request.Messages[^1].Text;
        Assert.Contains("kind=\"selection\"", sent, StringComparison.Ordinal);
        Assert.Contains(BrowserWords, sent, StringComparison.Ordinal);
        Assert.Contains("kind=\"page\" name=\"Text around the selection\"", sent, StringComparison.Ordinal);
        Assert.Contains("Just before the selection:\nFoxes are canids found on every continent but Antarctica.", sent, StringComparison.Ordinal);
        Assert.Contains("Just after the selection:\nTheir pups are born in spring.", sent, StringComparison.Ordinal);
        Assert.True(
            sent.IndexOf("kind=\"selection\"", StringComparison.Ordinal) < sent.IndexOf("kind=\"page\"", StringComparison.Ordinal),
            "the selection comes before the text around it");
        Assert.EndsWith("When do they hunt?", sent, StringComparison.Ordinal);
        Assert.EndsWith(AssistantInstructions.WebNearbyContextGuidance, request.Instructions, StringComparison.Ordinal);

        // The question's message says the page text went with it, and nothing of it is saved.
        var asked = conversation.Messages.First(message => message.Role == MessageRole.User);
        Assert.True(Assert.Single(asked.TextAttachments).HasNearbyContext);
        var saved = new MessageMapper(new FakeClipboard()).ToDomain(asked);
        Assert.Empty(saved.ContextItems);
        Assert.DoesNotContain("canids", saved.ToString(), StringComparison.Ordinal);
    });

    [Fact]
    public void WithoutNearbyContext_TheModelGetsNoPageBlock() => RunSta(() =>
    {
        var model = new ChattyModel();
        var contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()));
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(contexts), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, contexts);
        var provider = new ModelAnswerProvider(orchestrator, new FixedClock(Now), null, null, contexts);
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), provider);
        var windows = new AssistantWindowStateController(new FakeShell(), CreateBarModel(), conversation);
        var controller = new AskBrowserSelectionController(
            new FakePermissions(true), windows, conversation, NullLogger<AskBrowserSelectionController>.Instance);
        var running = controller.OpenAsync(FoxSelection);
        WaitUntil(() => running.IsCompleted, "The browser selection was not opened.");

        conversation.Draft = "When do they hunt?";
        conversation.AskCommand.Execute(null);
        WaitUntil(() => model.Requests.Count == 1 && !conversation.IsAnswering, "The question was not answered.");

        var request = model.Requests.Single();
        Assert.DoesNotContain("kind=\"page\"", request.Messages[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantInstructions.WebNearbyContextGuidance, request.Instructions, StringComparison.Ordinal);
    });

    [Fact]
    public void AskingAboutIt_GivesTheContextServiceASelectionItemThatNamesThePage_AndTheModelIsToldWhereItIsFrom() => RunSta(() =>
    {
        var model = new ChattyModel();
        var contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()));
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(contexts), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, contexts);
        var provider = new ModelAnswerProvider(orchestrator, new FixedClock(Now), null, null, contexts);
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), provider);
        var windows = new AssistantWindowStateController(new FakeShell(), CreateBarModel(), conversation);
        var controller = new AskBrowserSelectionController(
            new FakePermissions(true), windows, conversation, NullLogger<AskBrowserSelectionController>.Instance);
        var running = controller.OpenAsync(FoxSelection);
        WaitUntil(() => running.IsCompleted, "The browser selection was not opened.");
        Assert.Empty(model.Requests);

        conversation.Draft = "When do they hunt?";
        conversation.AskCommand.Execute(null);
        WaitUntil(() => model.Requests.Count == 1 && !conversation.IsAnswering, "The question was not answered.");

        // The model has the selected words, and where they are from: the page's title and address and the browser, in the block's tag.
        var sent = model.Requests.Single().Messages[^1].Text;
        Assert.Contains(BrowserWords, sent);
        Assert.Contains("browser=\"Microsoft Edge\"", sent);
        Assert.Contains("page_title=\"A page about foxes\"", sent);
        Assert.Contains("page_url=\"https://example.test/foxes?q=1\"", sent);
        Assert.EndsWith("When do they hunt?", sent);
        Assert.EndsWith(AssistantInstructions.WebSelectionGuidance, model.Requests.Single().Instructions, StringComparison.Ordinal);

        // It is only the words: the page itself was never fetched, and the question's message holds the chip and nothing saved.
        var asked = conversation.Messages.First(message => message.Role == MessageRole.User);
        Assert.Equal(BrowserWords, Assert.Single(asked.TextAttachments).Text);
        var saved = new MessageMapper(new FakeClipboard()).ToDomain(asked);
        Assert.Empty(saved.ContextItems);
        Assert.DoesNotContain("example.test", saved.ToString());
    });

    // ---- Getting it from the pipe to the window -------------------------------------------------------------------------------------

    [Fact]
    public void TheWindowGetsTheSelectionOnItsOwnThread_AndOneThatCameBeforeTheWindowWaitsForIt()
    {
        var requests = new BrowserSelectionRequests();
        var posted = new List<Action>();
        var opened = new List<BrowserSelection>();

        // Before the window exists, as when the browser's click started the app: only the newest waits.
        var browsers = new List<NearWindowTarget?>();
        void Take(BrowserSelection selection, NearWindowTarget? browser)
        {
            opened.Add(selection);
            browsers.Add(browser);
        }

        requests.Post(FoxSelection with { Text = "older" });
        requests.Post(FoxSelection, Browser);
        Assert.Empty(opened);

        requests.Connect(Take, posted.Add);
        Assert.Empty(opened);
        posted.Single()();
        Assert.Equal([FoxSelection], opened);

        // The browser's window comes with the selection it was noted for, also when the selection had to wait.
        Assert.Same(Browser, Assert.Single(browsers));

        // Once connected, each goes through the window's own thread, one at a time, and nothing is kept.
        posted.Clear();
        requests.Post(FoxSelection with { Text = "next" });
        Assert.Single(opened);
        posted.Single()();
        Assert.Equal("next", opened[^1].Text);
        Assert.Null(browsers[^1]);

        // Connecting with nothing waiting posts nothing.
        var idle = new List<Action>();
        new BrowserSelectionRequests().Connect(Take, idle.Add);
        Assert.Empty(idle);
    }

    [Fact]
    public void TheBridgeHandsATakenSelectionToTheWindow_AndLogsOnlyCounts()
    {
        var requests = new BrowserSelectionRequests();
        var handed = new List<BrowserSelection>();
        var windows = new List<NearWindowTarget?>();
        requests.Connect((selection, browser) =>
        {
            handed.Add(selection);
            windows.Add(browser);
        }, action => action());
        var logger = new MessageLogger<BrowserBridgeIntegration>();
        var placement = new FakePlacement { Foreground = Browser };
        using var bridge = new BrowserBridgeIntegration(
            new FakeBrowserBridge(), new InMemorySettingsService(), requests, logger, placement);

        var reply = ((IBrowserSelectionSink)bridge).Receive(FoxSelection);

        Assert.True(reply.IsAccepted);
        Assert.Equal([FoxSelection], handed);

        // The window in front, which is the browser the click was made in, is noted for the panel to open beside; the app's own windows
        // are not the one (its process id is passed to be left out).
        Assert.Same(Browser, Assert.Single(windows));
        Assert.Equal([Environment.ProcessId], placement.DescribeCalls);

        var logged = string.Join("\n", logger.Messages);
        Assert.Contains("noted: True", logged);
        Assert.DoesNotContain("Foxes", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Edge", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("4242", logged, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoWindowInFrontOrOneThatCouldNotBeNoted_TheSelectionIsStillTaken_AndOpensWhereTheAssistantUsuallyDoes()
    {
        var requests = new BrowserSelectionRequests();
        var windows = new List<NearWindowTarget?>();
        requests.Connect((_, browser) => windows.Add(browser), action => action());
        var logger = new MessageLogger<BrowserBridgeIntegration>();

        // Nothing in front (a locked PC, the desktop, only the Assistant's own window).
        using (var bridge = new BrowserBridgeIntegration(
            new FakeBrowserBridge(), new InMemorySettingsService(), requests, logger, new FakePlacement()))
        {
            Assert.True(((IBrowserSelectionSink)bridge).Receive(FoxSelection).IsAccepted);
        }

        // Windows failing to say is no reason to lose the selection.
        using (var bridge = new BrowserBridgeIntegration(
            new FakeBrowserBridge(), new InMemorySettingsService(), requests, logger, new FakePlacement { Throws = true }))
        {
            Assert.True(((IBrowserSelectionSink)bridge).Receive(FoxSelection).IsAccepted);
        }

        // And a bridge made without placement (as the older tests do) notes nothing.
        using (var bridge = new BrowserBridgeIntegration(new FakeBrowserBridge(), new InMemorySettingsService(), requests, logger))
        {
            Assert.True(((IBrowserSelectionSink)bridge).Receive(FoxSelection).IsAccepted);
        }

        Assert.Equal(3, windows.Count);
        Assert.All(windows, Assert.Null);
        var logged = string.Join("\n", logger.Messages);
        Assert.Contains("noted: False", logged);
        Assert.Contains("could not be noted: InvalidOperationException", logged);
    }

    // ---- Opening beside the browser's window ---------------------------------------------------------------------------------------

    [Fact]
    public void WithTheBrowsersWindow_ThePanelOpensBesideIt_AndWithoutOneAtItsUsualPlace() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();

        Open(setup, FoxSelection, Browser);

        // Beside the browser, and nowhere else; the order is the same as without a window.
        Assert.Same(Browser, Assert.Single(setup.Window.ShownNear));
        Assert.Empty(setup.Window.ConversationsShown);
        Assert.Equal(["permission", "foreground"], setup.Order);
        Assert.Equal(BrowserWords, Assert.Single(setup.Conversation.Texts).Text);

        // The browser may be gone from the front by now (a start that took long): then it is the usual place.
        Open(setup, FoxSelection);
        Assert.Single(setup.Window.ShownNear);
        Assert.Single(setup.Window.ConversationsShown);
    });

    [Fact]
    public void TheNoticesOpenBesideTheBrowsersWindowToo() => RunSta(() =>
    {
        var off = CreateAskBrowserSelection(allowed: false);

        Open(off, FoxSelection, Browser);

        Assert.Same(Browser, Assert.Single(off.Window.ShownNear));
        Assert.Equal(AskBrowserSelectionController.TurnedOffText, Assert.Single(off.Conversation.Messages).Text);
    });

    [Fact]
    public void TheWindowOpensAtThePointPlacementGivesBesideTheBrowser_AndAShowingPanelMovesThere() => RunSta(() => WithTheme(() =>
    {
        var placement = new FakePlacement { Near = new ScreenPoint(1450, 146) };
        var assistant = CreateAssistant(placement, animations: false);
        var (window, controller) = (assistant.Window, assistant.Controller);
        try
        {
            // Hidden: it opens as the panel straight at the point, never at the default place.
            controller.ShowConversationNear(Browser);

            Assert.True(window.IsVisible);
            Assert.Equal(AssistantWindowState.FloatingConversation, controller.State);
            var asked = Assert.Single(placement.NearCalls);
            Assert.Same(Browser, asked.Target);
            Assert.Equal(598.0, asked.Layout.AnchorHeight);
            Assert.Equal(418.0, asked.Layout.AnchorWidth);
            Assert.Equal((598.0, new ScreenPoint(1450, 146)), (placement.PointCalls[^1].Layout.AnchorHeight, placement.PointCalls[^1].Point));
            Assert.Empty(placement.Calls);

            // Showing: another selection, in a browser that is somewhere else now, brings the panel there.
            placement.Near = new ScreenPoint(-900, 220);
            controller.ShowConversationNear(Browser);
            Assert.Equal(new ScreenPoint(-900, 220), placement.PointCalls[^1].Point);
            Assert.Equal(2, placement.PointCalls.Count);

            // Without a place for it (the window is on no monitor), it is shown, and left, as it is.
            placement.Near = null;
            controller.ShowConversationNear(Browser);
            Assert.Equal(2, placement.PointCalls.Count);
            Assert.True(window.IsVisible);

            // After it is dismissed, the same way in opens it beside the browser again.
            window.Dismiss();
            WaitUntil(() => !window.IsVisible, "The panel was not dismissed.");
            placement.Near = new ScreenPoint(1450, 146);
            controller.ShowConversationNear(Browser);
            Assert.Equal(new ScreenPoint(1450, 146), placement.PointCalls[^1].Point);
            Assert.Equal(3, placement.PointCalls.Count);

            // No window at all: the usual place for a hidden panel, which a showing one keeps.
            window.Dismiss();
            WaitUntil(() => !window.IsVisible, "The panel was not dismissed.");
            controller.ShowConversationNear(null);
            Assert.Single(placement.Calls);
            Assert.Equal(3, placement.PointCalls.Count);
        }
        finally
        {
            window.Close();
        }
    }));

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of synthetic text only: the answer panel as the browser answer reference shows it, with the
    // plus, the follow-up field and the microphone in one row under the answer.
    [Fact]
    public void TheBrowserAnswerPanelRenders() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew("find inverse of white screen");
        model.Messages.Add(new MessageViewModel(MessageRole.Assistant,
            "The inverse of a white screen is a black screen. When colors are inverted, white (#FFFFFF) becomes black (#000000).\n\n" +
            "## How Color Inversion Works\n\n" +
            "Color inversion creates the mathematical negative of a color by subtracting its RGB (Red, Green, Blue) values from 255.\n\n" +
            "This technique is commonly used in accessibility settings and in dark modes, and many screens offer it as a switch."));
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<System.Windows.Controls.Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            System.Threading.Thread.Sleep(100);
            Pump();
            Assert.True(Named<System.Windows.Controls.Grid>(panel, "Composer").IsVisible);
            Assert.True(Named<System.Windows.Controls.Button>(panel, "AddButton").IsVisible);
            Assert.False(Named<System.Windows.Controls.Button>(panel, "SpeakerButton").IsVisible);
            RenderGlass(panel, "browser-answer-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    // ---- A conversation that began in the browser carries on and can move into the full app ---------------------------------------

    [Fact]
    public void AConversationThatBeganInTheBrowser_TakesFollowUps_AndOpensInTheHistoryWindow() => RunSta(() =>
    {
        var setup = CreateAskBrowserSelection();
        Open(setup, FoxSelection, Browser);

        // Nothing is asked yet, so there is nothing to move into the History window.
        Assert.False(setup.Conversation.OpenInHistoryCommand.CanExecute(null));

        setup.Conversation.Draft = "When do they hunt?";
        setup.Conversation.AskCommand.Execute(null);
        Assert.Equal("When do they hunt?", Assert.Single(setup.Answers.Asked).Text);
        Assert.True(setup.Conversation.OpenInHistoryCommand.CanExecute(null));

        // The panel is the History window's to take over, with the question in it, as for any conversation.
        var history = new HistoryViewModel(new FixedClock(ReferenceNow));
        var assistant = new FakeAssistantWindow();
        var historyWindow = new FakeHistoryWindow();
        _ = new HistoryWindowController(setup.Conversation, history, assistant, () => historyWindow);

        setup.Conversation.OpenInHistoryCommand.Execute(null);

        Assert.Equal(setup.Conversation.Id, Assert.Single(history.Conversations).Id);
        Assert.Equal(1, historyWindow.Shown);
        Assert.Equal(1, assistant.Dismissals);
    });

}
