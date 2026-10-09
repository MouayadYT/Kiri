using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.Selection;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Hotkeys;
using Assistant.Windows.Imaging;
using Assistant.Windows.Selection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Ask Selection: the selected-text shortcut, the Ask panel it opens and its quick actions (PROJECT_SPEC §4.5) ---------------

    private const int AssistantProcess = 4242;
    private const string SampleSelection = "The committee will meet on Thursday to review the budget proposal and the revised timeline.";

    private static readonly string[] QuickActionTitles = ["Summarize", "Explain", "Rewrite", "Solve", "Define", "Ask Anything"];

    private sealed class SelectionSetup
    {
        public required AskSelectionController Controller { get; init; }
        public required ConversationViewModel Conversation { get; init; }
        public required FakeSelectionService Service { get; init; }
        public required FakePermissions Permissions { get; init; }
        public required FakeShell Window { get; init; }
        public required RecordingAnswers Answers { get; init; }
        public required CollectingLogger<AskSelectionController> Log { get; init; }
        public required List<string> Order { get; init; }
    }

    private static SelectionSetup CreateAskSelection(bool allowed = true, SelectionResult? result = null, IPermissionGate? gate = null)
    {
        var order = new List<string>();
        var permissions = new FakePermissions(allowed) { Order = order };
        var window = new FakeShell { Order = order };
        var service = new FakeSelectionService { Order = order, Result = result ?? SelectedIn(SampleSelection), Window = window };
        var answers = new RecordingAnswers();
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), answers);
        var windows = new AssistantWindowStateController(window, CreateBarModel(), conversation);
        var log = new CollectingLogger<AskSelectionController>();
        var controller = new AskSelectionController(service, permissions, windows, conversation, log, AssistantProcess, gate: gate);
        return new SelectionSetup
        {
            Controller = controller, Conversation = conversation, Service = service, Permissions = permissions, Window = window,
            Answers = answers, Log = log, Order = order,
        };
    }

    private static ForegroundApp Notepad(int processId = 1234) => new(processId, "notepad", @"C:\Windows\notepad.exe", 0x1000);

    private static SelectionResult SelectedIn(string text, bool truncated = false, int processId = 1234) =>
        SelectionResult.Selected(Notepad(processId), text, truncated);

    private static Task Press(SelectionSetup setup)
    {
        var running = setup.Controller.InvokeAsync();
        WaitUntil(() => running.IsCompleted, "Ask Selection did not end.");
        return running;
    }

    // ---- What the shortcut does ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheShortcut_ChecksThePermission_ReadsTheSelection_AndOpensThePanelWithTheTextAttached_AskingNothing() => RunSta(() =>
    {
        var setup = CreateAskSelection();

        Press(setup);

        Assert.Equal(["permission", "read", "foreground"], setup.Order);
        Assert.Equal(PermissionCapability.SelectedText, Assert.Single(setup.Permissions.Asked));
        // The text was read while the Assistant had not yet taken the keyboard: the panel was shown after.
        Assert.Equal(0, setup.Service.PanelsShownAtRead);
        Assert.Single(setup.Window.ConversationsShown);
        // ...and it was then given the keyboard, which Windows may have taken back while the other application was being read.
        Assert.Equal(1, setup.Window.ForegroundTaken);

        // Attached as one text, with its chip; nothing is asked and nothing is typed.
        var text = Assert.Single(setup.Conversation.Texts);
        Assert.Equal(SampleSelection, text.Text);
        Assert.Equal(AttachmentKind.Text, Assert.Single(setup.Conversation.Chips).Kind);
        Assert.Empty(setup.Conversation.Messages);
        Assert.Empty(setup.Answers.Asked);
        Assert.Equal("", setup.Conversation.Draft);
        Assert.True(setup.Conversation.CanCompose);
        Assert.Equal("Ask about this text", setup.Conversation.ComposerPlaceholder);
        Assert.False(setup.Controller.IsRunning);

        // The quick actions are listed with the text they are about.
        Assert.True(setup.Conversation.ShowQuickActions);
        Assert.Equal(QuickActionTitles, setup.Conversation.QuickActions.Select(action => action.Title));
        Assert.Equal(SampleSelection, setup.Conversation.SelectionPreview);
        Assert.Equal($"Selected text · {SampleSelection.Length:N0} characters", setup.Conversation.SelectionCaption);
    });

    [Fact]
    public void ANewSelection_StartsAConversationOfItsOwn_InPlaceOfTheOneBefore() => RunSta(() =>
    {
        var setup = CreateAskSelection();
        setup.Conversation.StartNew("an earlier question");
        var before = setup.Conversation.Id;
        Assert.Single(setup.Conversation.Messages);

        Press(setup);

        Assert.NotEqual(before, setup.Conversation.Id);
        Assert.Empty(setup.Conversation.Messages);
        Assert.Single(setup.Conversation.Texts);
    });

    [Fact]
    public void NoSelection_AnAppThatDoesNotShareIt_AndALockedPc_AreSaidInWords_AndNothingIsAttached() => RunSta(() =>
    {
        var cases = new (SelectionResult Result, string Text)[]
        {
            (SelectionResult.NoSelection(Notepad()), AskSelectionController.NoSelectionText),
            (SelectionResult.Unsupported(Notepad()), AskSelectionController.UnsupportedText),
            (SelectionResult.NoForegroundApp(), AskSelectionController.NoForegroundText),
            // Blanks are not a selection either.
            (SelectedIn("   \r\n  "), AskSelectionController.NoSelectionText),
        };

        foreach (var (result, text) in cases)
        {
            var setup = CreateAskSelection(result: result);

            Press(setup);

            var message = Assert.Single(setup.Conversation.Messages);
            Assert.Equal(MessageRole.Assistant, message.Role);
            Assert.Equal(text, message.Text);
            Assert.Empty(setup.Conversation.Texts);
            Assert.False(setup.Conversation.ShowQuickActions);
            Assert.Single(setup.Window.ConversationsShown);
            Assert.Equal(1, setup.Window.ForegroundTaken);
            Assert.Empty(setup.Answers.Asked);
        }

        // Each says what to do next, and none of them guesses at a selection.
        Assert.All(
            [AskSelectionController.NoSelectionText, AskSelectionController.UnsupportedText, AskSelectionController.NoForegroundText],
            text => Assert.False(string.IsNullOrWhiteSpace(text)));
    });

    [Fact]
    public void WithSelectedTextTurnedOff_NothingIsRead_AndThePanelSaysWhereToTurnItOn() => RunSta(() =>
    {
        var setup = CreateAskSelection(allowed: false);

        Press(setup);

        Assert.Equal(0, setup.Service.Calls);
        Assert.Equal(["permission", "foreground"], setup.Order);
        Assert.Equal(AskSelectionController.TurnedOffText, Assert.Single(setup.Conversation.Messages).Text);
        Assert.Contains("Settings", AskSelectionController.TurnedOffText);
        Assert.Contains("Permissions", AskSelectionController.TurnedOffText);
        Assert.Empty(setup.Conversation.Texts);
        Assert.Single(setup.Window.ConversationsShown);
    });

    [Fact]
    public void WhileTheAssistantsOwnWindowIsInFront_TheShortcutDoesNothing_AndTheConversationIsKept() => RunSta(() =>
    {
        var setup = CreateAskSelection(result: SelectedIn("text typed in the assistant's own composer", processId: AssistantProcess));
        setup.Conversation.StartNew("an earlier question");
        var before = setup.Conversation.Id;

        Press(setup);

        Assert.Equal(before, setup.Conversation.Id);
        Assert.Single(setup.Conversation.Messages);
        Assert.Empty(setup.Conversation.Texts);
        Assert.Empty(setup.Window.ConversationsShown);
        Assert.Equal(0, setup.Window.ForegroundTaken);
        Assert.Contains(setup.Log.Messages, line => line.Contains("own window"));
    });

    [Fact]
    public void ASelectionTooLongToTakeInFull_IsAttached_WithANoticeAboveTheComposer() => RunSta(() =>
    {
        var setup = CreateAskSelection(result: SelectedIn(new string('x', 5000), truncated: true));

        Press(setup);

        Assert.Single(setup.Conversation.Texts);
        Assert.True(setup.Conversation.HasAttachNotice);
        Assert.Equal(AskSelectionController.TruncatedNotice, setup.Conversation.AttachNotice);
        Assert.Contains("200,000", AskSelectionController.TruncatedNotice);
    });

    [Fact]
    public void AShortcutPressedWhileTheSelectionIsBeingRead_IsIgnored() => RunSta(() =>
    {
        var setup = CreateAskSelection();
        setup.Service.Hold = new TaskCompletionSource();

        var first = setup.Controller.InvokeAsync();
        WaitUntil(() => setup.Service.Calls == 1, "The selection was not asked for.");
        Assert.True(setup.Controller.IsRunning);
        var second = setup.Controller.InvokeAsync();
        Assert.True(second.IsCompleted);
        Assert.Equal(1, setup.Service.Calls);

        setup.Service.Hold.SetResult();
        WaitUntil(() => first.IsCompleted, "Ask Selection did not end.");
        Assert.False(setup.Controller.IsRunning);
        Assert.Single(setup.Window.ConversationsShown);
        Assert.Single(setup.Conversation.Texts);

        // And it can be used again afterwards.
        setup.Service.Hold = null;
        Press(setup);
        Assert.Equal(2, setup.Service.Calls);
    });

    [Fact]
    public void AFailureReadingTheSelection_IsLoggedByTypeAlone_AndTheUserIsToldToTryAgain() => RunSta(() =>
    {
        var setup = CreateAskSelection();
        setup.Service.Throws = new InvalidOperationException("the secret words that were selected");

        Press(setup);

        Assert.Equal(AskSelectionController.FailedText, Assert.Single(setup.Conversation.Messages).Text);
        Assert.False(setup.Controller.IsRunning);
        Assert.Contains(setup.Log.Messages, line => line.Contains("InvalidOperationException"));
        Assert.DoesNotContain(setup.Log.Messages, line => line.Contains("secret words"));
    });

    [Fact]
    public void NeitherTheSelectedTextNorAWindowTitleIsEverLogged() => RunSta(() =>
    {
        var setup = CreateAskSelection(result: SelectedIn("my private diary entry about the merger"));

        Press(setup);

        var log = string.Join("\n", setup.Log.Messages);
        Assert.DoesNotContain("diary", log);
        Assert.DoesNotContain("merger", log);
        Assert.Contains("notepad", log);
        Assert.Contains("Selected", log);
        Assert.Contains("39 characters", log);
    });

    // ---- The quick actions ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnActionWritesItsRequestInTheComposer_AndNeverSendsIt() => RunSta(() =>
    {
        var prompts = new Dictionary<string, string>
        {
            ["Summarize"] = "Summarize the selected text.",
            ["Explain"] = "Explain the selected text.",
            ["Rewrite"] = "Rewrite the selected text so it reads more clearly.",
            ["Solve"] = "Solve the selected problem and show the steps.",
            ["Define"] = "Define the selected text.",
        };
        var setup = CreateAskSelection();
        Press(setup);
        var focusRequests = 0;
        setup.Conversation.ComposerFocusRequested += (_, _) => focusRequests++;

        foreach (var (title, prompt) in prompts)
        {
            var action = setup.Conversation.QuickActions.Single(item => item.Title == title);

            action.Command.Execute(null);

            Assert.Equal(prompt, setup.Conversation.Draft);
        }

        // Five actions, five times the keyboard was handed to the composer, and nothing was ever asked.
        Assert.Equal(5, focusRequests);
        Assert.Empty(setup.Conversation.Messages);
        Assert.Empty(setup.Answers.Asked);
        Assert.Single(setup.Conversation.Texts);
        Assert.True(setup.Conversation.ShowQuickActions);
        Assert.Equal(QuickActionTitles.Length, setup.Conversation.QuickActions.Count);
    });

    [Fact]
    public void AskAnything_PreparesNothing_ButLeavesTheKeyboardInTheComposerWithWhatWasTyped() => RunSta(() =>
    {
        var setup = CreateAskSelection();
        Press(setup);
        var focusRequests = 0;
        setup.Conversation.ComposerFocusRequested += (_, _) => focusRequests++;
        var askAnything = setup.Conversation.QuickActions.Single(item => item.Title == "Ask Anything");

        askAnything.Command.Execute(null);
        Assert.Equal("", setup.Conversation.Draft);

        setup.Conversation.Draft = "what is the deadline?";
        askAnything.Command.Execute(null);
        Assert.Equal("what is the deadline?", setup.Conversation.Draft);
        Assert.Equal(2, focusRequests);
        Assert.Empty(setup.Answers.Asked);
        Assert.Empty(setup.Conversation.Messages);
    });

    [Fact]
    public void SendingAPreparedAction_AsksTheModelAboutTheSelection_AndThenTheActionsGoAway() => RunSta(() =>
    {
        var model = new ChattyModel();
        var contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()));
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(contexts), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, contexts);
        var provider = new ModelAnswerProvider(orchestrator, new FixedClock(Now), null, null, contexts);
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), provider);
        var window = new FakeShell();
        var windows = new AssistantWindowStateController(window, CreateBarModel(), conversation);
        var controller = new AskSelectionController(
            new FakeSelectionService { Result = SelectedIn(SampleSelection), Window = window }, new FakePermissions(true), windows,
            conversation, NullLogger<AskSelectionController>.Instance, AssistantProcess);
        var running = controller.InvokeAsync();
        WaitUntil(() => running.IsCompleted, "Ask Selection did not end.");
        Assert.Empty(model.Requests);

        conversation.QuickActions.Single(item => item.Title == "Summarize").Command.Execute(null);
        Assert.Empty(model.Requests);
        conversation.AskCommand.Execute(null);
        WaitUntil(() => model.Requests.Count == 1 && !conversation.IsAnswering, "The question was not answered.");

        // The model was given the selection as context, and the request the user sent.
        var sent = model.Requests.Single().Messages[^1].Text;
        Assert.Contains(SampleSelection, sent);
        Assert.EndsWith("Summarize the selected text.", sent);
        Assert.Contains("selection", sent);

        // The question is the first message, with the text above it; the quick actions and the chip are gone.
        var asked = conversation.Messages.First(message => message.Role == MessageRole.User);
        Assert.Equal("Summarize the selected text.", asked.Text);
        Assert.Single(asked.TextAttachments);
        Assert.False(conversation.ShowQuickActions);
        Assert.Empty(conversation.Texts);
        Assert.Equal("", conversation.Draft);
    });

    [Fact]
    public void TakingTheTextsChipOff_PutsTheQuickActionsAway_AndEscDoesItStepByStep() => RunSta(() =>
    {
        var setup = CreateAskSelection();
        Press(setup);
        Assert.True(setup.Conversation.ShowQuickActions);

        setup.Conversation.RemoveAttachmentCommand.Execute(setup.Conversation.Chips.Single());
        Assert.False(setup.Conversation.ShowQuickActions);
        Assert.Empty(setup.Conversation.Texts);

        // Esc: first the request an action wrote goes, then the text, and only then does the panel close.
        Press(setup);
        setup.Conversation.QuickActions[0].Command.Execute(null);
        Assert.False(setup.Conversation.HandleEscape());
        Assert.Equal("", setup.Conversation.Draft);
        Assert.True(setup.Conversation.ShowQuickActions);
        Assert.False(setup.Conversation.HandleEscape());
        Assert.False(setup.Conversation.ShowQuickActions);
        Assert.True(setup.Conversation.HandleEscape());
    });

    [Fact]
    public void TheSelectionIsNeverSaved_ItIsOnTheChipAndTheMessageAlone() => RunSta(() =>
    {
        var setup = CreateAskSelection(result: SelectedIn("only in memory"));

        Press(setup);
        setup.Conversation.Draft = "what is this?";
        setup.Conversation.AskCommand.Execute(null);

        var asked = Assert.Single(setup.Conversation.Messages);
        Assert.Equal("only in memory", Assert.Single(asked.TextAttachments).Text);
        var saved = new MessageMapper(new FakeClipboard()).ToDomain(asked);
        Assert.Empty(saved.ContextItems);
        Assert.Equal("what is this?", saved.Text);
        Assert.DoesNotContain("only in memory", saved.ToString());
    });

    // ---- The panel ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePanelListsTheSelectionAndSixActionsAboveTheComposer_AndAnActionFillsTheComposer() => RunSta(() => WithTheme(() =>
    {
        var (panel, _, model) = CreateAssistant();
        try
        {
            model.StartWithSelection(new TextAttachment(SampleSelection));
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var area = Named<Grid>(panel, "ConversationLayer");
            var list = Named<StackPanel>(panel, "QuickActionsPanel");
            Assert.Equal(Visibility.Visible, list.Visibility);

            // What was selected, and six rows, in order.
            Assert.Equal(SampleSelection, Named<TextBlock>(panel, "SelectionPreviewText").Text);
            Assert.Equal(model.SelectionCaption, Named<TextBlock>(panel, "SelectionCaptionText").Text);
            var rows = Descendants<Button>(Named<ItemsControl>(panel, "QuickActionList")).ToArray();
            Assert.Equal(QuickActionTitles, rows.Select(AutomationName));
            Assert.All(rows, row => Assert.True(row.IsEnabled));
            // Real buttons: Tab reaches them, and they run the action through their own command.
            Assert.All(rows, row => Assert.True(row.Focusable && row.IsTabStop));

            // Each row is 40 tall and they sit in one column inside the panel, 30 in from its sides, under its top buttons...
            var panelBounds = new Rect(0, 0, 418, 598);
            var bounds = rows.Select(row => BoundsIn(area, row)).ToArray();
            Assert.All(bounds, row => Assert.Equal(40, row.Height, 1));
            Assert.All(bounds, row => Assert.Equal(358, row.Width, 1));
            Assert.All(bounds, row => Assert.Equal(30, row.Left, 1));
            Assert.True(BoundsIn(area, Named<Border>(panel, "SelectionCard")).Top >= 84 - 0.5);
            for (var i = 1; i < bounds.Length; i++)
            {
                Assert.True(bounds[i].Top >= bounds[i - 1].Bottom - 0.5, "Rows overlap.");
            }

            // ...and end above the composer, which has grown to hold the chip.
            var composer = BoundsIn(area, Named<Grid>(panel, "Composer"));
            Assert.True(bounds[^1].Bottom <= composer.Top, $"The list ends at {bounds[^1].Bottom} and the composer starts at {composer.Top}.");
            Assert.True(panelBounds.Contains(composer));

            // Pressing Summarize, as Enter or Space or a click does, writes its request in the composer and puts the keyboard there. Nothing is sent.
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(rows[0])).Invoke();
            Pump();
            var input = Named<PromptInputControl>(panel, "ComposerInput");
            Assert.Equal("Summarize the selected text.", input.Text);
            Assert.Equal("Summarize the selected text.", model.Draft);
            Assert.IsAssignableFrom<System.Windows.Controls.TextBox>(FocusManager.GetFocusedElement(panel));
            Assert.Empty(model.Messages);
            Assert.Equal(Visibility.Visible, list.Visibility);

            // Once it is sent, the list is put away.
            model.AskCommand.Execute(null);
            Pump();
            Assert.Equal(Visibility.Collapsed, list.Visibility);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void ThePanelFitsLongAndMultilineSelectionsInThreeLines_AndKeepsTheListAboveTheComposerEvenWithANotice() => RunSta(() => WithTheme(() =>
    {
        var (panel, _, model) = CreateAssistant();
        try
        {
            var long_ = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i} of a very long document that the user selected."));
            model.StartWithSelection(new TextAttachment(long_), AskSelectionController.TruncatedNotice);
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var area = Named<Grid>(panel, "ConversationLayer");
            var preview = Named<TextBlock>(panel, "SelectionPreviewText");
            Assert.True(preview.Text.EndsWith((char)0x2026), "The long selection's preview is not cut with an ellipsis.");
            Assert.DoesNotContain('\n', preview.Text);
            Assert.True(preview.ActualHeight <= 54.5, $"The preview is {preview.ActualHeight} tall.");

            var rows = Descendants<Button>(Named<ItemsControl>(panel, "QuickActionList")).ToArray();
            var composer = BoundsIn(area, Named<Grid>(panel, "Composer"));
            Assert.True(BoundsIn(area, rows[^1]).Bottom <= composer.Top, "The list runs under the composer.");
            Assert.True(composer.Top >= 0);
        }
        finally { panel.Close(); }
    }));

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of synthetic text only: the panel as the shortcut opens it, with an action picked, and
    // with a very long selection and its notice.
    [Fact]
    public void TheAskSelectionPanelRenders() => RunSta(() => WithTheme(() =>
    {
        var (panel, _, model) = CreateAssistant();
        try
        {
            model.StartWithSelection(new TextAttachment(
                "The committee will meet on Thursday to review the budget proposal and the revised timeline, and the vote on the new office lease is expected before the end of the quarter."));
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            System.Threading.Thread.Sleep(100);
            Pump();
            RenderGlass(panel, "ask-selection-open-2x.png", 2);

            // The row the pointer is on (the plate the template lights, drawn by hand since a test has no pointer).
            var hovered = Descendants<Button>(Named<ItemsControl>(panel, "QuickActionList")).ElementAt(2);
            var plate = Assert.IsType<Border>(hovered.Template.FindName("Highlight", hovered));
            plate.Background = (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Surface.CommandItemSelected");
            Pump();
            RenderGlass(panel, "ask-selection-hover-2x.png", 2);
            plate.Background = System.Windows.Media.Brushes.Transparent;

            model.QuickActions[0].Command.Execute(null);
            Pump();
            System.Threading.Thread.Sleep(100);
            Pump();
            RenderGlass(panel, "ask-selection-action-2x.png", 2);

            model.StartWithSelection(
                new TextAttachment(string.Join(" ", Enumerable.Range(1, 80).Select(i => $"sentence{i}"))), AskSelectionController.TruncatedNotice);
            Pump();
            System.Threading.Thread.Sleep(100);
            Pump();
            RenderGlass(panel, "ask-selection-long-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    // ---- The shortcut itself -------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheThirdShortcutsMessage_ReadsTheSelection_AndOpensThePanel_WithoutTheOtherTwoShortcutsDoingAnything() => RunSta(() => WithTheme(() =>
    {
        // The registration is real, on a chord nothing else uses; the message Windows sends when it is pressed is posted by hand, so no
        // input access is needed. The id is the selected-text shortcut's own, which tells it from the other two. The panel that opens is
        // a fake one, so that this never takes the keyboard from whoever is using the machine.
        var assistant = CreateAssistant();
        var window = assistant.Window;
        window.ShowActivated = false;
        var shell = new FakeShell();
        var windows = new AssistantWindowStateController(shell, CreateBarModel(), assistant.Conversation);
        var service = new FakeSelectionService { Result = SelectedIn(SampleSelection) };
        var controller = new AskSelectionController(
            service, new FakePermissions(true), windows, assistant.Conversation, NullLogger<AskSelectionController>.Instance,
            AssistantProcess);
        var shortcut = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F21");
        using var hotkeys = new GlobalHotkeyService(NullLogger<GlobalHotkeyService>.Instance, GlobalHotkeyService.SelectedTextHotkeyId);
        using var binding = new OverlayHotkeyBinding(window, hotkeys, shortcut, () => _ = controller.InvokeAsync());
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Assert.True(hotkeys.IsRegistered);

            // The other shortcuts' ids are not this one's: nothing is read.
            PostMessage(handle, 0x0312 /* WM_HOTKEY */, GlobalHotkeyService.VisualIntelligenceHotkeyId, (0x84 << 16) | 0x7);
            PostMessage(handle, 0x0312, 0x5341, (0x84 << 16) | 0x7);
            Pump();
            Assert.Equal(0, service.Calls);

            PostMessage(handle, 0x0312, GlobalHotkeyService.SelectedTextHotkeyId, (0x84 << 16) | 0x7);
            WaitUntil(() => service.Calls == 1 && shell.ForegroundTaken == 1, "The panel did not open.");

            Assert.Equal(SampleSelection, Assert.Single(assistant.Conversation.Texts).Text);
            Assert.Empty(assistant.Conversation.Messages);
            Assert.Single(shell.ConversationsShown);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheAppRegistersTheSelectionServiceAndAShortcutOfItsOwn() => RunSta(() =>
    {
        using var host = AppHost.Create();

        // The service the app hands out reads only with the Selected Text permission (step 119): it wraps the one that reads.
        Assert.IsType<PermissionCheckedSelectionService>(host.Services.GetRequiredService<ISelectionService>());
        Assert.IsType<SelectionService>(host.Services.GetRequiredService<SelectionService>());
        var selected = host.Services.GetRequiredKeyedService<GlobalHotkeyService>(ServiceCollectionExtensions.SelectedTextHotkey);
        var visual = host.Services.GetRequiredKeyedService<GlobalHotkeyService>(ServiceCollectionExtensions.VisualIntelligenceHotkey);
        var search = host.Services.GetRequiredService<GlobalHotkeyService>();
        Assert.NotSame(selected, visual);
        Assert.NotSame(selected, search);
        Assert.Same(selected, host.Services.GetRequiredKeyedService<GlobalHotkeyService>(ServiceCollectionExtensions.SelectedTextHotkey));
        Assert.Equal(0x5343, GlobalHotkeyService.SelectedTextHotkeyId);
        Assert.NotEqual(GlobalHotkeyService.VisualIntelligenceHotkeyId, GlobalHotkeyService.SelectedTextHotkeyId);
        // The default shortcut is the one the spec gives: Alt+Shift+W.
        Assert.Equal(new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "W"), new HotkeySettings().SelectedTextActions);
    });

    // ---- Doubles ------------------------------------------------------------------------------------------------------------------

    private sealed class FakeSelectionService : ISelectionService
    {
        public List<string>? Order { get; init; }
        public FakeShell? Window { get; init; }
        public SelectionResult Result { get; set; } = SelectionResult.NoForegroundApp();
        public Exception? Throws { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public int Calls { get; private set; }

        /// <summary>Whether the user's yes for this use (a permission set to ask every time) was in force when the selection was asked for (step 119).</summary>
        public bool ApprovedAtRead { get; private set; }

        /// <summary>How many times the panel had been shown when the selection was asked for.</summary>
        public int PanelsShownAtRead { get; private set; }

        public async Task<SelectionResult> GetSelectionAsync(CancellationToken cancellationToken = default)
        {
            Order?.Add("read");
            Calls++;
            ApprovedAtRead = Assistant.Core.Permissions.PermissionApprovals.IsApproved(PermissionCapability.SelectedText);
            PanelsShownAtRead = Window?.ConversationsShown.Count ?? 0;
            if (Hold is { } hold)
            {
                await hold.Task.ConfigureAwait(true);
            }

            return Throws is not null ? throw Throws : Result;
        }
    }
}
