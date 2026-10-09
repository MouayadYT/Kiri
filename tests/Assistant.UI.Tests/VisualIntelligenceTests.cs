using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.Orchestration;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Capture;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Capture;
using Assistant.Windows.Imaging;
using Assistant.Windows.Placement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Visual Intelligence: the shortcut, the overlay's outcome, and the conversation it opens (PROJECT_SPEC §4.6) ----------------

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];

    private sealed class VisualSetup
    {
        public required VisualIntelligenceController Controller { get; init; }
        public required ConversationViewModel Conversation { get; init; }
        public required FakeCapture Capture { get; init; }
        public required FakeOverlay Overlay { get; init; }
        public required FakeShell Window { get; init; }
        public required FakePermissions Permissions { get; init; }
        public required FakeImageClipboard Clipboard { get; init; }
        public required IContextService Contexts { get; init; }
        public required ScreenAttachments Screens { get; init; }
        public required RecordingAnswers Answers { get; init; }
        public required CollectingLogger<VisualIntelligenceController> Log { get; init; }
        public required List<string> Order { get; init; }
    }

    private static VisualSetup CreateVisual(
        bool allowed = true, ChipAvailability? imageSearch = null, Func<CapturedImage, Task>? searchImage = null,
        Func<CancellationToken, Task<ChipAvailability>>? imageSearchOf = null, IPermissionGate? gate = null)
    {
        var order = new List<string>();
        var permissions = new FakePermissions(allowed) { Order = order };
        var capture = new FakeCapture { Order = order };
        var overlay = new FakeOverlay { Order = order };
        var window = new FakeShell { Order = order };
        var answers = new RecordingAnswers();
        var contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()));
        var screens = new ScreenAttachments(contexts, answers);
        var conversation = new ConversationViewModel(
            new VoiceInputViewModel(new FakeMicrophone()), answers, screens: screens);
        var bar = CreateBarModel();
        var windows = new AssistantWindowStateController(window, bar, conversation);
        var clipboard = new FakeImageClipboard();
        var log = new CollectingLogger<VisualIntelligenceController>();
        var controller = new VisualIntelligenceController(
            capture, permissions, overlay, window, windows, conversation, clipboard,
            imageSearchOf ?? (_ => Task.FromResult(imageSearch ?? ChipAvailability.ImageSearchUnavailable)), log, searchImage, gate);
        return new VisualSetup
        {
            Controller = controller, Conversation = conversation, Capture = capture, Overlay = overlay, Window = window,
            Permissions = permissions, Clipboard = clipboard, Contexts = contexts, Screens = screens, Answers = answers, Log = log,
            Order = order,
        };
    }

    // A region of a snapshot, as the overlay hands it over.
    private static CaptureOutcome Outcome(CaptureAction action, string question = "", int width = 300, int height = 200)
    {
        var snapshot = CodedSnapshot(1000, 600);
        var region = snapshot.Crop(new ScreenRect(100, 100, 100 + width, 100 + height));
        snapshot.Dispose();
        return new CaptureOutcome(action, region, question);
    }

    private static Task Run(VisualSetup setup, CaptureOutcome? outcome)
    {
        setup.Overlay.Outcome = outcome;
        return setup.Controller.InvokeAsync();
    }

    [Fact]
    public void TheShortcut_AsksThePermission_HidesTheWindow_CapturesEveryMonitor_AndThenShowsTheOverlay() => RunSta(() =>
    {
        var setup = CreateVisual(imageSearch: new ChipAvailability(true, "Sends the picture to a search provider."));

        var running = Run(setup, null);
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.Equal(["permission", "hide", "capture", "overlay"], setup.Order);
        Assert.Equal(2, setup.Overlay.Snapshots.Count);
        Assert.Equal(new ChipAvailability(true, "Sends the picture to a search provider."), setup.Overlay.ImageSearch);
        Assert.Equal(PermissionCapability.ScreenCapture, setup.Permissions.Asked.Single());
        Assert.False(setup.Controller.IsActive);

        // Given up: the conversation is untouched and the window is not brought back.
        Assert.Empty(setup.Conversation.Messages);
        Assert.Empty(setup.Conversation.Attachments);
        Assert.Empty(setup.Window.ConversationsShown);
    });

    [Fact]
    public void WithScreenCaptureTurnedOff_NothingIsCaptured_AndTheConversationSaysSo() => RunSta(() =>
    {
        var setup = CreateVisual(allowed: false);

        var running = Run(setup, null);
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.Equal(["permission"], setup.Order);
        Assert.Equal(0, setup.Window.HiddenAtOnce);
        var message = Assert.Single(setup.Conversation.Messages);
        Assert.Equal(MessageRole.Assistant, message.Role);
        Assert.Equal(VisualIntelligenceController.TurnedOffText, message.Text);
        Assert.Single(setup.Window.ConversationsShown);
    });

    [Fact]
    public void WhenWindowsWillNotAllowTheCapture_TheConversationSaysSo_AndNothingIsLeftRunning() => RunSta(() =>
    {
        var setup = CreateVisual();
        setup.Capture.Failure = new ScreenCaptureException(ScreenCaptureFailure.Refused, 5);

        var running = Run(setup, null);
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.Equal(VisualIntelligenceController.CaptureFailedText, Assert.Single(setup.Conversation.Messages).Text);
        Assert.Empty(setup.Overlay.Snapshots);
        Assert.False(setup.Controller.IsActive);
        Assert.Contains(setup.Log.Messages, line => line.Contains("Refused", StringComparison.Ordinal) && line.Contains('5'));
    });

    [Fact]
    public void AskAssistant_OpensTheConversationWithThePartAttached_TheComposerWaiting_AndThePictureInTheContextService() => RunSta(() =>
    {
        var setup = CreateVisual();

        var running = Run(setup, Outcome(CaptureAction.Ask));
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        // The floating conversation is the one shown, with no message yet and the part of the screen as a chip, ready for a question.
        var conversation = setup.Conversation;
        Assert.Single(setup.Window.ConversationsShown);
        Assert.Empty(conversation.Messages);
        var image = Assert.Single(conversation.Attachments);
        Assert.True(image.IsCapture);
        Assert.Equal(VisualIntelligenceController.CaptureName, image.Name);
        Assert.Equal((300, 200), (image.PixelWidth, image.PixelHeight));
        Assert.Null(image.Path);
        Assert.NotNull(image.Thumbnail);
        Assert.Equal(AttachmentKind.Image, Assert.Single(conversation.Chips).Kind);
        Assert.True(conversation.CanCompose);
        Assert.Equal("Ask about this picture", conversation.ComposerPlaceholder);

        // The picture went to the context service as a screenshot the user picked, kept for every question of the conversation.
        var pending = Assert.Single(setup.Contexts.PendingItems(conversation.Id));
        Assert.Equal(ContextItemType.Screenshot, pending.Type);
        Assert.True(pending.Retained);
        Assert.Equal(ContextSource.UserSelected, pending.Source);
        Assert.Equal(image.ContextId, pending.Id);
        Assert.Equal(PngSignature, pending.ImageData.Span[..4].ToArray());
        Assert.Equal(image.Data.ToArray(), pending.ImageData.ToArray());
        Assert.Equal(ScreenAttachments.Origin, setup.Contexts.GetContext(conversation.Id).Pending[0].Provenance[0].Origin);
    });

    [Fact]
    public void ThePictureTheModelGetsIsAPngOfTheSelectedPixels_AndNothingIsSavedToDisk() => RunSta(() =>
    {
        var setup = CreateVisual();
        var region = Outcome(CaptureAction.Ask, width: 64, height: 48);
        var expected = region.Region.Pixels.ToArray();

        var running = Run(setup, region);
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        var png = Assert.Single(setup.Conversation.Attachments).Data.ToArray();
        var decoded = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((64, 48), (decoded.PixelWidth, decoded.PixelHeight));
        var pixels = new byte[64 * 48 * 4];
        new FormatConvertedBitmap(decoded, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 64 * 4, 0);
        Assert.Equal(expected, pixels);

        // The capture's own copy was wiped once the picture was taken from it.
        Assert.Throws<ObjectDisposedException>(() => region.Region.Pixels);
    });

    [Fact]
    public void AQuestionTypedInTheChip_IsAskedAtOnce_AboutThePartOfTheScreen() => RunSta(() =>
    {
        var setup = CreateVisual();

        var running = Run(setup, Outcome(CaptureAction.Ask, "explain this error"));
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");
        WaitUntil(() => setup.Answers.Asked.Count == 1, "The question was not asked.");

        var asked = setup.Answers.Asked.Single();
        Assert.Equal("explain this error", asked.Text);
        Assert.True(Assert.Single(asked.Attachments).IsCapture);
        Assert.Single(setup.Conversation.Messages, message => message.Role == MessageRole.User);
        Assert.Single(setup.Window.ConversationsShown);

        // It stays attached for the follow-ups, but the next message does not show it again.
        var capture = Assert.Single(setup.Conversation.Attachments);
        Assert.True(capture.WasAsked);
        Assert.Single(setup.Contexts.PendingItems(setup.Conversation.Id));
    });

    [Fact]
    public void Copy_PutsThePictureOnTheClipboard_AndLeavesTheConversationAlone() => RunSta(() =>
    {
        var setup = CreateVisual();

        var running = Run(setup, Outcome(CaptureAction.Copy, width: 120, height: 90));
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        var copied = Assert.Single(setup.Clipboard.Images);
        Assert.Equal((120, 90), (copied.PixelWidth, copied.PixelHeight));
        Assert.Empty(setup.Conversation.Messages);
        Assert.Empty(setup.Window.ConversationsShown);
        Assert.Empty(setup.Contexts.PendingItems(setup.Conversation.Id));
    });

    [Fact]
    public void WhenTheClipboardIsBusy_TheUserIsToldSo() => RunSta(() =>
    {
        var setup = CreateVisual();
        setup.Clipboard.Succeeds = false;

        var running = Run(setup, Outcome(CaptureAction.Copy));
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.Equal(VisualIntelligenceController.ClipboardBusyText, Assert.Single(setup.Conversation.Messages).Text);
    });

    [Fact]
    public void ImageSearch_GoesToTheSearchOnlyWhenOneIsGiven_AndIsLeftAloneOtherwise() => RunSta(() =>
    {
        var searched = new List<CapturedImage>();
        var setup = CreateVisual(
            imageSearch: new ChipAvailability(true, "Sends the picture to a search provider."),
            searchImage: image =>
            {
                searched.Add(image.Crop(image.Bounds));
                return Task.CompletedTask;
            });

        var running = Run(setup, Outcome(CaptureAction.ImageSearch, width: 80, height: 60));
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.Equal((80, 60), (Assert.Single(searched).Width, searched[0].Height));
        Assert.Empty(setup.Conversation.Messages);

        var without = CreateVisual();
        var other = Run(without, Outcome(CaptureAction.ImageSearch));
        WaitUntil(() => other.IsCompleted, "Visual Intelligence did not end.");
        Assert.Empty(without.Conversation.Messages);
    });

    [Fact]
    public void PressingTheShortcutAgainWhileTheOverlayIsUp_GivesUp() => RunSta(() =>
    {
        var setup = CreateVisual();
        setup.Overlay.Hold = true;

        var first = setup.Controller.InvokeAsync();
        WaitUntil(() => setup.Overlay.Snapshots.Count == 2, "The overlay did not show.");
        Assert.True(setup.Controller.IsActive);

        var second = setup.Controller.InvokeAsync();
        WaitUntil(() => first.IsCompleted, "The first invocation did not end.");

        Assert.True(second.IsCompleted);
        Assert.True(setup.Overlay.CancelledByCaller);
        Assert.False(setup.Controller.IsActive);
        Assert.Equal(1, setup.Capture.Captures);
    });

    [Fact]
    public void AFailureInsideIsLoggedByTypeAlone_AndDoesNotEscape() => RunSta(() =>
    {
        var setup = CreateVisual();
        setup.Overlay.Throws = new InvalidOperationException("the screen showed secret words");

        var running = setup.Controller.InvokeAsync();
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.True(running.IsCompletedSuccessfully);
        Assert.False(setup.Controller.IsActive);
        Assert.Contains(setup.Log.Messages, line => line.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(setup.Log.Messages, line => line.Contains("secret", StringComparison.Ordinal));
    });

    [Fact]
    public void NothingTheUserSawOrAsked_ReachesALog() => RunSta(() =>
    {
        var setup = CreateVisual();

        var running = Run(setup, Outcome(CaptureAction.Ask, "what is my secret password question"));
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.NotEmpty(setup.Log.Messages);
        Assert.DoesNotContain(setup.Log.Messages, line => line.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(setup.Log.Messages, line => line.Contains("Screenshot", StringComparison.Ordinal));
        Assert.Contains(setup.Log.Messages, line => line.Contains("Ask", StringComparison.Ordinal) && line.Contains("300 x 200", StringComparison.Ordinal));
    });

    // ---- The part of the screen stays with the conversation, and goes when the user takes it off (§4.6, step 76) ------------------------

    private sealed class ConversationSetup
    {
        public required ConversationViewModel Conversation { get; init; }
        public required ModelAnswerProvider Provider { get; init; }
        public required ChattyModel Model { get; init; }
        public required IContextService Contexts { get; init; }
        public required ScreenAttachments Screens { get; init; }
        public required ImageItem Capture { get; init; }
    }

    // A conversation over the real pipeline (context service, prompt builder, orchestrator) with a scripted vision model, with a part of
    // the screen attached.
    private static ConversationSetup CreateScreenConversation(
        bool vision = true, int width = 40, int height = 30, TimeProvider? time = null)
    {
        var model = new ChattyModel { Active = new ModelInfo("vision-model", 8192) { SupportsVision = vision } };
        var contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()));
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(contexts), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, contexts);
        var provider = new ModelAnswerProvider(orchestrator, new FixedClock(Now), null, null, contexts);
        var screens = new ScreenAttachments(contexts, provider, time: time);
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), provider, screens: screens);
        var region = CodedSnapshot(width, height);
        var png = Encode(region);
        var capture = ImageItem.FromCapture("Screenshot", png, SnapshotImage.Thumbnail(region, 16), width, height);
        conversation.StartWithCapture(capture);
        return new ConversationSetup
        {
            Conversation = conversation, Provider = provider, Model = model, Contexts = contexts, Screens = screens, Capture = capture,
        };
    }

    // A PNG of a capture, made off the UI thread as the app does.
    private static byte[] Encode(CapturedImage image) =>
        Task.Run(() => CapturedImageEncoder.EncodePngAsync(image)).GetAwaiter().GetResult();

    [Fact]
    public void AFollowUp_IsAskedAboutTheSameScreenshot_WithoutCapturingAgain() => RunSta(() =>
    {
        var chat = CreateScreenConversation();

        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The first answer did not end.");
        chat.Conversation.Ask("what does the second row say?");
        WaitUntil(() => chat.Model.Requests.Count == 2 && !chat.Conversation.IsAnswering, "The follow-up was not answered.");

        // Both questions went to the model with the picture, as an image beside the question.
        Assert.All(chat.Model.Requests, request =>
        {
            var image = Assert.Single(request.Images);
            Assert.Equal(ImageFormat.Png, ImageFormats.Detect(image.Span));
        });
        Assert.Equal("what does the second row say?", chat.Model.Requests[1].Messages[^1].Text);
        Assert.Equal("explain this error", chat.Model.Requests[1].Messages[0].Text);

        // The first message shows the picture; the follow-up does not show it again. Its chip left the composer with the first question (it
        // read as a picture still waiting to be sent), and the picture stays with the conversation for the next one.
        var users = chat.Conversation.Messages.Where(message => message.Role == MessageRole.User).ToList();
        Assert.Single(users[0].Attachments);
        Assert.Empty(users[1].Attachments);
        Assert.Empty(chat.Conversation.Chips);
        Assert.Same(chat.Capture, Assert.Single(chat.Conversation.Captures));
        Assert.True(chat.Provider.HasScreenContext(chat.Conversation.Id));
    });

    [Fact]
    public void TakingTheChipOff_ReleasesThePictureEverywhere_AndTheNextQuestionIsAskedWithoutIt() => RunSta(() =>
    {
        var chat = CreateScreenConversation();
        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The first answer did not end.");
        Assert.NotEmpty(chat.Capture.Data.ToArray());
        Assert.Empty(chat.Conversation.Chips);

        // Its chip went with the question; the picture itself is still taken off the same way.
        chat.Conversation.RemoveAttachmentCommand.Execute(chat.Capture);

        Assert.Empty(chat.Conversation.Chips);
        Assert.Empty(chat.Conversation.Attachments);
        Assert.True(chat.Capture.Data.IsEmpty);
        Assert.Empty(chat.Contexts.PendingItems(chat.Conversation.Id));
        Assert.False(chat.Provider.HasScreenContext(chat.Conversation.Id));

        chat.Conversation.Ask("and the third?");
        WaitUntil(() => chat.Model.Requests.Count == 2 && !chat.Conversation.IsAnswering, "The next answer did not end.");
        Assert.Empty(chat.Model.Requests[1].Images);
    });

    [Fact]
    public void TheSessionsMemoryOfTheMessages_LosesThePixels_WhenThePictureIsReleased() => RunSta(() =>
    {
        var chat = CreateScreenConversation();
        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The first answer did not end.");
        var session = chat.Provider.SessionOf(chat.Conversation.Id)!;
        Assert.Contains(session.Conversation.Messages.SelectMany(message => message.ContextItems), item => !item.ImageData.IsEmpty);

        chat.Screens.Release(chat.Conversation.Id, chat.Capture);

        var kept = session.Conversation.Messages.SelectMany(message => message.ContextItems).ToList();
        Assert.Contains(kept, item => item.Type == ContextItemType.Screenshot);
        Assert.All(kept, item => Assert.True(item.ImageData.IsEmpty));
    });

    [Fact]
    public void AnUnaskedCapture_IsReleasedByEsc_WhileAnAskedOneStaysUntilItsChipIsPressed() => RunSta(() =>
    {
        var chat = CreateScreenConversation();

        // Nothing has been asked: Esc takes the picture off, which frees it.
        Assert.False(chat.Conversation.HandleEscape());
        Assert.Empty(chat.Conversation.Chips);
        Assert.True(chat.Capture.Data.IsEmpty);
        Assert.Empty(chat.Contexts.PendingItems(chat.Conversation.Id));

        var asked = CreateScreenConversation();
        asked.Conversation.Ask("explain this error");
        WaitUntil(() => asked.Model.Requests.Count == 1 && !asked.Conversation.IsAnswering, "The answer did not end.");

        // Asked about: its chip is gone, Esc closes the panel and leaves the picture for the next question.
        Assert.Empty(asked.Conversation.Chips);
        Assert.True(asked.Conversation.HandleEscape());
        Assert.Single(asked.Conversation.Captures);
        Assert.False(asked.Capture.Data.IsEmpty);
    });

    [Fact]
    public void AnotherConversation_LetsGoOfTheOnesPicture() => RunSta(() =>
    {
        var chat = CreateScreenConversation();
        var first = chat.Conversation.Id;
        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The answer did not end.");

        chat.Conversation.StartNew("something else entirely");
        WaitUntil(() => chat.Model.Requests.Count == 2 && !chat.Conversation.IsAnswering, "The new answer did not end.");

        Assert.True(chat.Capture.Data.IsEmpty);
        Assert.Empty(chat.Contexts.PendingItems(first));
        Assert.Empty(chat.Model.Requests[1].Images);
        Assert.NotEqual(first, chat.Conversation.Id);
    });

    [Fact]
    public void WithAModelThatCannotSeeImages_TheScreenshotIsLeftOutAndTheUserIsToldSo() => RunSta(() =>
    {
        var chat = CreateScreenConversation(vision: false);

        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The answer did not end.");

        Assert.Empty(chat.Model.Requests[0].Images);
        Assert.Contains(
            chat.Conversation.Messages.Last().Content.OfType<TextContent>(),
            part => part.Text == PromptBuilder.ScreenshotDroppedNotice);
    });

    [Fact]
    public void TheHistoryWindowTakesTheScreenshotAlong_AndFollowUpsThereAreAskedAboutItToo() => RunSta(() =>
    {
        var chat = CreateScreenConversation();
        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The first answer did not end.");
        var history = new HistoryViewModel(new FixedClock(Now), answers: chat.Provider, screens: chat.Screens);

        var opened = history.Open(chat.Conversation.Id, chat.Conversation.Messages, chat.Conversation.UpdatedAt, chat.Conversation.Captures);
        WaitUntil(() => opened.IsLoaded, "The conversation did not load.");

        // It was asked about in the bar, so it is no chip in History either: it is what the conversation is about.
        Assert.Empty(history.Chips);
        Assert.True(history.Send("what about the third row?"));
        WaitUntil(() => chat.Model.Requests.Count == 2 && !history.IsAnswering, "The follow-up did not end.");
        Assert.Single(chat.Model.Requests[1].Images);

        // Starting another conversation in the floating panel does not let go of the one that went on in History.
        chat.Conversation.StartNew("something else entirely");
        WaitUntil(() => chat.Model.Requests.Count == 3 && !chat.Conversation.IsAnswering, "The new answer did not end.");
        Assert.False(chat.Capture.Data.IsEmpty);
        Assert.Empty(history.Chips);

        // Taking it off in History frees it for good.
        history.RemoveAttachmentCommand.Execute(chat.Capture);
        Assert.True(chat.Capture.Data.IsEmpty);
        Assert.Empty(chat.Contexts.PendingItems(chat.Conversation.Id));
    });

    [Fact]
    public void WhatTheMessagesSaveOfAScreenshot_IsNothingButThatItWasOne() => RunSta(() =>
    {
        var chat = CreateScreenConversation();
        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The first answer did not end.");
        var mapper = new Assistant.UI.History.MessageMapper(new FakeClipboard());

        var saved = mapper.ToDomain(chat.Conversation.Messages.First(message => message.Role == MessageRole.User));

        var descriptor = Assert.Single(saved.ContextItems);
        Assert.Equal(ContextItemType.Screenshot, descriptor.Type);
        Assert.Equal("Screenshot", descriptor.DisplayName);
        Assert.Null(descriptor.FilePath);
        Assert.True(descriptor.ImageData.IsEmpty);
        Assert.Null(descriptor.Text);
    });

    // ---- Doubles ------------------------------------------------------------------------------------------------------------------

    private sealed class FakeShell : IAssistantWindow
    {
        public List<string>? Order { get; init; }
        public AssistantWindowState State { get; set; }
        public ScreenPoint? SurfaceTop => null;
        public int HiddenAtOnce { get; private set; }
        public List<ScreenPoint?> ConversationsShown { get; } = [];

        public event EventHandler? Moved { add { } remove { } }
        public event EventHandler? Expanded { add { } remove { } }
        public event EventHandler? Hidden { add { } remove { } }
        public event EventHandler? AssistantStateChanged { add { } remove { } }

        public void ShowAndFocus(ScreenPoint? surfaceTop)
        {
        }

        public void ShowConversation(ScreenPoint? surfaceTop) => ConversationsShown.Add(surfaceTop);

        public List<NearWindowTarget> ShownNear { get; } = [];

        public void ShowConversationNear(NearWindowTarget target) => ShownNear.Add(target);

        public int ForegroundTaken { get; private set; }

        public bool TakeForeground()
        {
            Order?.Add("foreground");
            ForegroundTaken++;
            return true;
        }

        public void ExpandToConversation()
        {
        }

        public void Dismiss()
        {
        }

        public bool HideNow()
        {
            Order?.Add("hide");
            HiddenAtOnce++;
            return false;
        }
    }

    private sealed class FakePermissions(bool allowed) : IPermissionPolicy
    {
        public List<string>? Order { get; init; }
        public List<PermissionCapability> Asked { get; } = [];

        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default)
        {
            Order?.Add("permission");
            Asked.Add(capability);
            return Task.FromResult(new PermissionDecision(
                capability, allowed ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
        }
    }

    private sealed class FakeCapture : IScreenCapture
    {
        public List<string>? Order { get; init; }
        public ScreenCaptureException? Failure { get; set; }
        public int Captures { get; private set; }
        public bool ApprovedAtCapture { get; private set; }
        public List<CapturedImage> Taken { get; } = [];

        public IReadOnlyList<CaptureMonitor> GetMonitors() => [];

        public Task<CapturedImage> CaptureMonitorAsync(CaptureMonitor monitor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapturedImage> CaptureWindowAsync(nint window, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapturedImage> CaptureRegionAsync(ScreenRect region, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CapturedImage>> CaptureAllMonitorsAsync(CancellationToken cancellationToken = default)
        {
            Order?.Add("capture");
            Captures++;
            ApprovedAtCapture = Assistant.Core.Permissions.PermissionApprovals.IsApproved(PermissionCapability.ScreenCapture);
            if (Failure is not null)
            {
                throw Failure;
            }

            CapturedImage[] snapshots = [CodedSnapshot(200, 100, index: 0), CodedSnapshot(200, 100, left: 200, index: 1)];
            Taken.AddRange(snapshots);
            return Task.FromResult<IReadOnlyList<CapturedImage>>(snapshots);
        }
    }

    private sealed class FakeOverlay : ICaptureOverlay
    {
        public List<string>? Order { get; init; }
        public CaptureOutcome? Outcome { get; set; }
        public bool Hold { get; set; }
        public Exception? Throws { get; set; }
        public bool CancelledByCaller { get; private set; }
        public IReadOnlyList<CapturedImage> Snapshots { get; private set; } = [];
        public ChipAvailability? ImageSearch { get; private set; }
        public bool ApprovedAtSelect { get; private set; }

        public async Task<CaptureOutcome?> SelectAsync(
            IReadOnlyList<CapturedImage> snapshots, ChipAvailability imageSearch, CancellationToken cancellationToken)
        {
            Order?.Add("overlay");
            ApprovedAtSelect = Assistant.Core.Permissions.PermissionApprovals.IsApproved(PermissionCapability.ScreenCapture);
            if (Throws is not null)
            {
                throw Throws;
            }

            Snapshots = snapshots;
            ImageSearch = imageSearch;
            if (Hold)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    CancelledByCaller = true;
                    throw;
                }
            }

            foreach (var snapshot in snapshots)
            {
                snapshot.Dispose();
            }

            return Outcome;
        }
    }

    private sealed class FakeImageClipboard : IImageClipboard
    {
        public bool Succeeds { get; set; } = true;
        public List<BitmapSource> Images { get; } = [];

        public bool TrySetImage(BitmapSource image)
        {
            if (Succeeds)
            {
                Images.Add(image);
            }

            return Succeeds;
        }
    }

    private sealed class RecordingAnswers : IAnswerProvider
    {
        public List<MessageViewModel> Asked { get; } = [];

        public MessageViewModel? Answer(string question) => null;

        public Task StreamAnswerAsync(
            Guid conversationId, MessageViewModel question, Action<MessageViewModel> show, CancellationToken cancellationToken)
        {
            Asked.Add(question);
            return Task.CompletedTask;
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public List<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>A model that answers every request with a few words, and keeps the requests.</summary>
    private sealed class ChattyModel : IModelService
    {
        public ModelInfo? Active { get; init; } = new("test-model", 8192);

        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active);

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return AssistantResponseChunk.ForTextDelta("It says so.");
        }
    }
}
