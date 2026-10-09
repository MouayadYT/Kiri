using Assistant.Core.Contracts;
using Assistant.Core.Permissions;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.UI.Messages;
using Assistant.UI.Selection;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Selection;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Ask Selection by copy: the explicit, opt-in fallback for apps that do not share their selection (PROJECT_SPEC §4.5, step 89) ----

    private const string CopiedWords = "Quarterly numbers are due on Friday.";

    private sealed class CopySetup
    {
        public required AskSelectionController Controller { get; init; }
        public required ConversationViewModel Conversation { get; init; }
        public required FakeCopyService Copy { get; init; }
        public required FakeSelectionService Selection { get; init; }
        public required PerCapabilityPermissions Permissions { get; init; }
        public required InMemorySettingsService Settings { get; init; }
        public required FakeShell Window { get; init; }
        public required CollectingLogger<AskSelectionController> Log { get; init; }
        public required List<string> Order { get; init; }
    }

    private static CopySetup CreateAskByCopy(bool selectedText = true, bool byCopy = true, bool withSettings = true, IPermissionGate? gate = null)
    {
        var order = new List<string>();
        var permissions = new PerCapabilityPermissions { Order = order };
        permissions.Set(PermissionCapability.SelectedText, selectedText);
        permissions.Set(PermissionCapability.SelectedTextByCopy, byCopy);
        var window = new FakeShell { Order = order };
        var copy = new FakeCopyService { Order = order, Result = CopiedIn(CopiedWords) };
        var selection = new FakeSelectionService { Order = order, Result = UnsupportedIn(), Window = window };
        var settings = new InMemorySettingsService();
        settings.SaveAsync(new AppSettings { Permissions = new PermissionSettings { SelectedTextByCopy = byCopy } }).GetAwaiter().GetResult();
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), new RecordingAnswers());
        var windows = new AssistantWindowStateController(window, CreateBarModel(), conversation);
        var log = new CollectingLogger<AskSelectionController>();
        var controller = new AskSelectionController(
            selection, permissions, windows, conversation, log, AssistantProcess, copy, withSettings ? settings : null, gate);
        return new CopySetup
        {
            Controller = controller, Conversation = conversation, Copy = copy, Selection = selection, Permissions = permissions,
            Settings = settings, Window = window, Log = log, Order = order,
        };
    }

    private static SelectionResult UnsupportedIn() => SelectionResult.Unsupported(new ForegroundApp(1234, "customeditor", @"C:\Apps\customeditor.exe", 0x1000));

    private static CopySelectionResult CopiedIn(string text, ClipboardRestoreOutcome restore = ClipboardRestoreOutcome.Restored, bool truncated = false) =>
        CopySelectionResult.Copied(new ForegroundApp(1234, "customeditor", @"C:\Apps\customeditor.exe", 0x1000), text, truncated, restore);

    private static CopySelectionResult CopyFailed(CopySelectionStatus status, ClipboardRestoreOutcome restore = ClipboardRestoreOutcome.NotNeeded) =>
        CopySelectionResult.Of(status, new ForegroundApp(1234, "customeditor", @"C:\Apps\customeditor.exe", 0x1000), restore);

    private static Task PressCopy(CopySetup setup)
    {
        var running = setup.Controller.InvokeByCopyAsync();
        WaitUntil(() => running.IsCompleted, "Ask Selection by copy did not end.");
        return running;
    }

    [Fact]
    public void TheCopyShortcut_ChecksBothPermissions_PressesCopy_AndOpensThePanelWithTheTextAndANoticeThatSaysCopyWasUsed() => RunSta(() =>
    {
        var setup = CreateAskByCopy();

        PressCopy(setup);

        Assert.Equal(["permission", "permission", "copy", "foreground"], setup.Order);
        Assert.Equal([PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy], setup.Permissions.Asked);
        Assert.Equal(1, setup.Copy.Calls);
        Assert.Equal(0, setup.Selection.Calls);

        var text = Assert.Single(setup.Conversation.Texts);
        Assert.Equal(CopiedWords, text.Text);
        Assert.Null(text.WebPage);
        Assert.Empty(setup.Conversation.Messages);
        Assert.True(setup.Conversation.ShowQuickActions);
        Assert.Equal(AskSelectionController.CopiedRestoredNotice, setup.Conversation.AttachNotice);
        Assert.False(setup.Controller.IsRunning);
    });

    [Theory]
    [InlineData(ClipboardRestoreOutcome.Restored, AskSelectionController.CopiedRestoredNotice)]
    [InlineData(ClipboardRestoreOutcome.ChangedByOther, AskSelectionController.CopiedChangedNotice)]
    [InlineData(ClipboardRestoreOutcome.Failed, AskSelectionController.CopiedNotRestoredNotice)]
    public void TheNoticeSaysWhatBecameOfTheUsersClipboard(ClipboardRestoreOutcome restore, string expected) => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        setup.Copy.Result = CopiedIn(CopiedWords, restore);

        PressCopy(setup);

        Assert.Equal(expected, setup.Conversation.AttachNotice);
    });

    [Fact]
    public void ACopiedSelectionThatWasCut_SaysSoAfterTheCopyNotice() => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        setup.Copy.Result = CopiedIn(CopiedWords, truncated: true);

        PressCopy(setup);

        Assert.Equal(AskSelectionController.CopiedRestoredNotice + " " + AskSelectionController.TruncatedNotice, setup.Conversation.AttachNotice);
    });

    [Fact]
    public void WithSelectedTextByCopyTurnedOff_NothingIsPressed_AndThePanelSaysWhereToTurnItOn() => RunSta(() =>
    {
        var setup = CreateAskByCopy(byCopy: false);

        PressCopy(setup);

        Assert.Equal(0, setup.Copy.Calls);
        Assert.Empty(setup.Conversation.Texts);
        Assert.Equal(AskSelectionController.CopyTurnedOffText, Assert.Single(setup.Conversation.Messages).Text);
        Assert.Contains("Permissions", AskSelectionController.CopyTurnedOffText, StringComparison.Ordinal);
    });

    [Fact]
    public void WithSelectedTextTurnedOff_TheCopyShortcutDoesNothingEither() => RunSta(() =>
    {
        var setup = CreateAskByCopy(selectedText: false);

        PressCopy(setup);

        Assert.Equal(0, setup.Copy.Calls);
        Assert.Equal(AskSelectionController.TurnedOffText, Assert.Single(setup.Conversation.Messages).Text);
        Assert.Equal([PermissionCapability.SelectedText], setup.Permissions.Asked);
    });

    [Theory]
    [InlineData(CopySelectionStatus.NothingCopied, AskSelectionController.NothingCopiedText)]
    [InlineData(CopySelectionStatus.NotText, AskSelectionController.NotTextText)]
    [InlineData(CopySelectionStatus.UnsafeApp, AskSelectionController.UnsafeAppText)]
    [InlineData(CopySelectionStatus.ProtectedControl, AskSelectionController.ProtectedControlText)]
    [InlineData(CopySelectionStatus.ClipboardNotSaved, AskSelectionController.ClipboardNotSavedText)]
    [InlineData(CopySelectionStatus.ClipboardBusy, AskSelectionController.ClipboardBusyText)]
    [InlineData(CopySelectionStatus.KeysHeld, AskSelectionController.KeysHeldText)]
    [InlineData(CopySelectionStatus.ForegroundChanged, AskSelectionController.ForegroundChangedText)]
    [InlineData(CopySelectionStatus.NoForegroundApp, AskSelectionController.NoForegroundText)]
    [InlineData(CopySelectionStatus.Failed, AskSelectionController.FailedText)]
    public void WhenNothingIsCopied_ThePanelSaysWhyInWords_AndAttachesNothing(CopySelectionStatus status, string expected) => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        setup.Copy.Result = CopyFailed(status);

        PressCopy(setup);

        Assert.Equal(expected, Assert.Single(setup.Conversation.Messages).Text);
        Assert.Empty(setup.Conversation.Texts);
        Assert.False(setup.Conversation.ShowQuickActions);
    });

    [Theory]
    [InlineData(ClipboardRestoreOutcome.Failed, AskSelectionController.NotRestoredSentence)]
    [InlineData(ClipboardRestoreOutcome.ChangedByOther, AskSelectionController.ChangedSentence)]
    public void WhenCopyWasPressedButTheClipboardIsNotBack_ThePanelSaysThatToo(ClipboardRestoreOutcome restore, string sentence) => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        setup.Copy.Result = CopyFailed(CopySelectionStatus.NotText, restore);

        PressCopy(setup);

        Assert.Equal(AskSelectionController.NotTextText + sentence, Assert.Single(setup.Conversation.Messages).Text);
    });

    [Fact]
    public void WhileTheAssistantsOwnWindowIsInFront_TheCopyShortcutDoesNothingAndKeepsTheConversation() => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        setup.Conversation.StartNew("an earlier question");
        var before = setup.Conversation.Id;
        setup.Copy.Result = CopySelectionResult.Of(CopySelectionStatus.OwnWindow, new ForegroundApp(AssistantProcess, "assistant.ui", null, 0x9));

        PressCopy(setup);

        Assert.Equal(before, setup.Conversation.Id);
        Assert.Single(setup.Conversation.Messages);
        Assert.Empty(setup.Window.ConversationsShown);
    });

    [Fact]
    public void AnExceptionFromTheCopy_IsSaidInWords_NotRaised() => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        setup.Copy.Throws = new InvalidOperationException("secret");

        PressCopy(setup);

        Assert.Equal(AskSelectionController.FailedText, Assert.Single(setup.Conversation.Messages).Text);
        Assert.False(setup.Controller.IsRunning);
        Assert.DoesNotContain("secret", string.Join('\n', setup.Log.Messages), StringComparison.Ordinal);
    });

    [Fact]
    public void PressedAgainWhileItWorks_TheCopyShortcutDoesNothing() => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        setup.Copy.Hold = new TaskCompletionSource();

        var first = setup.Controller.InvokeByCopyAsync();
        WaitUntil(() => setup.Copy.Calls == 1, "The copy did not start.");
        Assert.True(setup.Controller.IsRunning);
        var second = setup.Controller.InvokeByCopyAsync();
        Assert.True(second.IsCompleted);
        setup.Copy.Hold.SetResult();
        WaitUntil(() => first.IsCompleted, "The copy did not end.");

        Assert.Equal(1, setup.Copy.Calls);
    });

    [Fact]
    public void WithNoCopyServiceBuilt_TheCopyShortcutDoesNothing() => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        var bare = new AskSelectionController(
            setup.Selection, setup.Permissions, new AssistantWindowStateController(setup.Window, CreateBarModel(), setup.Conversation),
            setup.Conversation, setup.Log, AssistantProcess);

        var running = bare.InvokeByCopyAsync();
        WaitUntil(() => running.IsCompleted, "It did not end.");

        Assert.Empty(setup.Permissions.Asked);
        Assert.Empty(setup.Conversation.Messages);
    });

    // ---- Never silent: the ordinary shortcut does not fall back to Copy by itself ---------------------------------------------------

    [Fact]
    public void TheOrdinaryShortcut_NeverPressesCopyByItself_AndTellsTheUserHowToAskForIt() => RunSta(() =>
    {
        var setup = CreateAskByCopy();
        var settings = new AppSettings { Permissions = new PermissionSettings { SelectedTextByCopy = true } };
        setup.Settings.SaveAsync(settings).GetAwaiter().GetResult();

        Press(setup);

        Assert.Equal(0, setup.Copy.Calls);
        Assert.Equal(1, setup.Selection.Calls);
        var said = Assert.Single(setup.Conversation.Messages).Text;
        Assert.StartsWith(AskSelectionController.UnsupportedText, said, StringComparison.Ordinal);
        Assert.Contains("Press Alt+Shift+C to try copying the selection instead", said, StringComparison.Ordinal);
        Assert.Empty(setup.Conversation.Texts);
    });

    [Fact]
    public void WhenCopyIsNotAllowed_TheUnsupportedNoticeSaysWhereToAllowIt_AndNothingIsPressed() => RunSta(() =>
    {
        var setup = CreateAskByCopy(byCopy: false);

        Press(setup);

        Assert.Equal(0, setup.Copy.Calls);
        var said = Assert.Single(setup.Conversation.Messages).Text;
        Assert.StartsWith(AskSelectionController.UnsupportedText, said, StringComparison.Ordinal);
        Assert.Contains("Selected Text by Copy in Settings, under Permissions", said, StringComparison.Ordinal);
    });

    [Fact]
    public void WithoutTheCopyFallbackWiredIn_TheUnsupportedNoticeIsUnchanged() => RunSta(() =>
    {
        var setup = CreateAskByCopy(withSettings: false);

        Press(setup);

        Assert.Equal(AskSelectionController.UnsupportedText, Assert.Single(setup.Conversation.Messages).Text);
    });

    private static Task Press(CopySetup setup)
    {
        var running = setup.Controller.InvokeAsync();
        WaitUntil(() => running.IsCompleted, "Ask Selection did not end.");
        return running;
    }

    [Fact]
    public void TheLogSaysHowTheCopyCameOut_NeverWhatWasCopiedOrWhereItIsFrom() => RunSta(() =>
    {
        var setup = CreateAskByCopy();

        PressCopy(setup);

        var logged = string.Join('\n', setup.Log.Messages);
        Assert.Contains("Ask Selection by copy", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Apps", logged, StringComparison.Ordinal);
    });

    [Fact]
    public void TheCopyShortcutHasAnIdOfItsOwn_AndIsOffUntilTheUserAllowsIt()
    {
        var ids = new[]
        {
            Assistant.Windows.Hotkeys.GlobalHotkeyService.SelectedTextHotkeyId,
            Assistant.Windows.Hotkeys.GlobalHotkeyService.VisualIntelligenceHotkeyId,
            Assistant.Windows.Hotkeys.GlobalHotkeyService.SelectedTextByCopyHotkeyId,
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "C"), new HotkeySettings().SelectedTextByCopy);
        Assert.False(new PermissionSettings().SelectedTextByCopy);
    }

    private sealed class PerCapabilityPermissions : IPermissionPolicy
    {
        private readonly Dictionary<PermissionCapability, bool> _allowed = [];

        public List<string>? Order { get; init; }
        public List<PermissionCapability> Asked { get; } = [];

        public void Set(PermissionCapability capability, bool allowed) => _allowed[capability] = allowed;

        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default)
        {
            Order?.Add("permission");
            Asked.Add(capability);
            return Task.FromResult(new PermissionDecision(
                capability, _allowed.GetValueOrDefault(capability) ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
        }
    }

    private sealed class FakeCopyService : ICopySelectionService
    {
        public List<string>? Order { get; init; }
        public CopySelectionResult Result { get; set; } = CopySelectionResult.Of(CopySelectionStatus.NoForegroundApp, null);
        public Exception? Throws { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public int Calls { get; private set; }
        public bool SelectedTextApproved { get; private set; }
        public bool ByCopyApproved { get; private set; }

        public async Task<CopySelectionResult> CopySelectionAsync(CancellationToken cancellationToken = default)
        {
            Order?.Add("copy");
            Calls++;
            SelectedTextApproved = Assistant.Core.Permissions.PermissionApprovals.IsApproved(PermissionCapability.SelectedText);
            ByCopyApproved = Assistant.Core.Permissions.PermissionApprovals.IsApproved(PermissionCapability.SelectedTextByCopy);
            if (Hold is { } hold)
            {
                await hold.Task.ConfigureAwait(true);
            }

            return Throws is not null ? throw Throws : Result;
        }
    }
}
