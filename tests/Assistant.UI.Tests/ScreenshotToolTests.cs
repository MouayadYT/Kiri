using System.Windows.Threading;
using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.UI.Capture;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Capture;
using Assistant.Windows.Placement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// What the <c>take_screenshot</c> tool does in the app (PROJECT_SPEC §4.6, §4.8): the Assistant's window goes and comes back as it was,
/// the monitor the user is on is captured into memory, and the picture is attached to the conversation as a chip the user can take off.
/// </summary>
public sealed partial class PromptInputControlTests
{
    // The Assistant's window, which writes into the order the test follows.
    private sealed class MonitorShell(List<string> order) : IAssistantWindow
    {
        public bool Showing { get; set; } = true;
        public ScreenPoint? Top { get; set; }
        public int ConversationsShown { get; private set; }

        public AssistantWindowState State { get; set; }
        public ScreenPoint? SurfaceTop => Top;

        public event EventHandler? Moved { add { } remove { } }
        public event EventHandler? Expanded { add { } remove { } }
        public event EventHandler? Hidden { add { } remove { } }
        public event EventHandler? AssistantStateChanged { add { } remove { } }

        public void ShowAndFocus(ScreenPoint? surfaceTop)
        {
        }

        public void ShowConversation(ScreenPoint? surfaceTop)
        {
            order.Add("show");
            ConversationsShown++;
            Showing = true;
        }

        public void ShowConversationNear(NearWindowTarget target)
        {
        }

        public bool TakeForeground() => true;

        public void ExpandToConversation()
        {
        }

        public void Dismiss()
        {
        }

        public bool HideNow()
        {
            order.Add("hide");
            var was = Showing;
            Showing = false;
            return was;
        }
    }

    // Monitors of the virtual screen, and the picture each gives when it is captured.
    private sealed class MonitorCapture(List<string> order) : IScreenCapture
    {
        public List<CaptureMonitor> Monitors { get; } =
        [
            new(0, new ScreenRect(-1000, 0, 0, 600), 96, false),
            new(1, new ScreenRect(0, 0, 1000, 600), 120, true),
        ];

        public ScreenCaptureException? Failure { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public List<int> Captured { get; } = [];
        public List<CapturedImage> Taken { get; } = [];

        public IReadOnlyList<CaptureMonitor> GetMonitors() => Monitors;

        public async Task<CapturedImage> CaptureMonitorAsync(CaptureMonitor monitor, CancellationToken cancellationToken = default)
        {
            order.Add("capture");
            Captured.Add(monitor.Index);
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            var image = CodedSnapshot(monitor.Bounds.Width, monitor.Bounds.Height, monitor.Bounds.Left, monitor.Bounds.Top, monitor.Dpi, monitor.Index);
            Taken.Add(image);
            return image;
        }

        public Task<CapturedImage> CaptureWindowAsync(nint window, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CapturedImage> CaptureRegionAsync(ScreenRect region, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CapturedImage>> CaptureAllMonitorsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ScreenshotSetup
    {
        public required ConversationScreenshotTaker Taker { get; init; }
        public required ConversationViewModel Conversation { get; init; }
        public required MonitorShell Shell { get; init; }
        public required MonitorCapture Capture { get; init; }
        public required ScreenAttachments Screens { get; init; }
        public required IContextService Contexts { get; init; }
        public required List<string> Order { get; init; }
    }

    private static ScreenshotSetup CreateScreenshotTaker()
    {
        // One list says in what order the window, the capture and the attachment happened.
        var order = new List<string>();
        var capture = new MonitorCapture(order);
        var shell = new MonitorShell(order);
        var answers = new RecordingAnswers();
        var contexts = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()));
        var screens = new ScreenAttachments(contexts, answers);
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), answers, screens: screens);
        var services = new ServiceCollection()
            .AddSingleton<IAssistantWindow>(shell)
            .AddSingleton(new AssistantWindowStateController(shell, CreateBarModel(), conversation))
            .AddSingleton(screens)
            .BuildServiceProvider();
        var taker = new ConversationScreenshotTaker(capture, services, NullLogger<ConversationScreenshotTaker>.Instance, Dispatcher.CurrentDispatcher);
        return new ScreenshotSetup
        {
            Taker = taker, Conversation = conversation, Shell = shell, Capture = capture, Screens = screens, Contexts = contexts, Order = order,
        };
    }

    private static ScreenshotOutcome Take(ScreenshotSetup setup, Guid? conversation = null)
    {
        var running = setup.Taker.TakeAsync(conversation ?? setup.Conversation.Id);
        WaitUntil(() => running.IsCompleted, "The screenshot did not end.");
        return running.GetAwaiter().GetResult();
    }

    [Fact]
    public void AScreenshotIsTakenWithTheWindowOutOfTheWay_AndTheWindowComesBack() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        setup.Shell.Top = new ScreenPoint(500, 20);

        var outcome = Take(setup);

        Assert.Equal(new ScreenshotOutcome(ScreenshotStatus.Taken, 1000, 600), outcome);
        Assert.Equal(["hide", "capture", "show"], setup.Order);
        Assert.True(setup.Shell.Showing);
        Assert.Equal(1, setup.Shell.ConversationsShown);
    });

    [Fact]
    public void ThePictureIsAChipOfTheConversation_AndInTheContextServiceForItsNextQuestions() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        setup.Shell.Top = new ScreenPoint(500, 20);

        Take(setup);

        var image = Assert.Single(setup.Conversation.Attachments);
        Assert.True(image.IsCapture);
        Assert.Equal(VisualIntelligenceController.CaptureName, image.Name);
        Assert.Equal((1000, 600), (image.PixelWidth, image.PixelHeight));
        Assert.Null(image.Path);
        Assert.NotNull(image.Thumbnail);
        Assert.Equal(AttachmentKind.Image, Assert.Single(setup.Conversation.Chips).Kind);

        var pending = Assert.Single(setup.Contexts.PendingItems(setup.Conversation.Id));
        Assert.Equal(ContextItemType.Screenshot, pending.Type);
        Assert.True(pending.Retained);
        Assert.Equal(image.ContextId, pending.Id);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], pending.ImageData.Span[..4].ToArray());

        // The snapshot itself is wiped once the attached copy was made, and nothing was written anywhere.
        Assert.Throws<ObjectDisposedException>(() => setup.Capture.Taken.Single().Pixels);
    });

    [Fact]
    public void TheUserCanTakeItOff_WhichLetsGoOfItEverywhere() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        Take(setup);
        var chip = Assert.Single(setup.Conversation.Chips);
        var image = Assert.Single(setup.Conversation.Attachments);

        setup.Conversation.RemoveAttachmentCommand.Execute(chip);

        Assert.Empty(setup.Conversation.Attachments);
        Assert.Empty(setup.Contexts.PendingItems(setup.Conversation.Id));
        Assert.True(image.Data.IsEmpty);
    });

    [Fact]
    public void TheMonitorTheWindowIsOnIsTheOneThatIsCaptured() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();

        setup.Shell.Top = new ScreenPoint(-500, 10);
        Assert.Equal((1000, 600), (Take(setup).Width, Take(setup).Height));
        Assert.Equal([0, 0], setup.Capture.Captured);

        setup.Shell.Top = new ScreenPoint(999, 599);
        Take(setup);
        setup.Shell.Top = new ScreenPoint(0, 0);
        Take(setup);
        Assert.Equal([0, 0, 1, 1], setup.Capture.Captured);
    });

    [Fact]
    public void WithNoPlaceKnownForTheWindow_OrOneOffEveryMonitor_ThePrimaryMonitorIsCaptured() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();

        setup.Shell.Top = null;
        Take(setup);
        setup.Shell.Top = new ScreenPoint(50_000, 50_000);
        Take(setup);

        Assert.Equal([1, 1], setup.Capture.Captured);
    });

    [Fact]
    public void WhenTheWindowWasNotShowing_ItIsNotShownAfterwards() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        setup.Shell.Showing = false;

        var outcome = Take(setup);

        Assert.Equal(ScreenshotStatus.Taken, outcome.Status);
        Assert.Equal(["hide", "capture"], setup.Order);
        Assert.Equal(0, setup.Shell.ConversationsShown);
    });

    [Fact]
    public void WhenWindowsWillNotAllowTheCapture_NothingIsAttached_AndTheWindowIsBack() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        setup.Capture.Failure = new ScreenCaptureException(ScreenCaptureFailure.Refused, 5);

        var outcome = Take(setup);

        Assert.Equal(new ScreenshotOutcome(ScreenshotStatus.Failed), outcome);
        Assert.Equal(["hide", "capture", "show"], setup.Order);
        Assert.Empty(setup.Conversation.Attachments);
        Assert.Empty(setup.Contexts.PendingItems(setup.Conversation.Id));
    });

    [Fact]
    public void WithNoMonitorAtAll_ItFailsAndTheWindowIsBack() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        setup.Capture.Monitors.Clear();

        var outcome = Take(setup);

        Assert.Equal(ScreenshotStatus.Failed, outcome.Status);
        Assert.Equal(["hide", "show"], setup.Order);
        Assert.Empty(setup.Capture.Captured);
    });

    [Fact]
    public void StoppingWhileTheScreenIsBeingCaptured_BringsTheWindowBack_AndAttachesNothing() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        setup.Capture.Hold = new TaskCompletionSource();
        using var stop = new CancellationTokenSource();

        var running = setup.Taker.TakeAsync(setup.Conversation.Id, stop.Token);
        WaitUntil(() => setup.Capture.Captured.Count == 1, "The capture did not start.");
        stop.Cancel();
        WaitUntil(() => running.IsCompleted, "The screenshot did not end.");

        Assert.True(running.IsCanceled);
        Assert.Equal(["hide", "capture", "show"], setup.Order);
        Assert.Empty(setup.Conversation.Attachments);
        Assert.Empty(setup.Contexts.PendingItems(setup.Conversation.Id));
    });

    [Fact]
    public void AConversationThatIsOpenInTheHistoryWindowShowsTheChipThere() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        var history = new HistoryViewModel(new FixedClock(Now), screens: setup.Screens);
        var other = Guid.NewGuid();
        history.Open(other, [], Now);

        Take(setup, other);

        Assert.Equal(AttachmentKind.Image, Assert.Single(history.Chips).Kind);
        Assert.True(Assert.IsType<ImageItem>(history.Chips.Single().Source).IsCapture);

        // The floating conversation is another one: it shows nothing of it.
        Assert.Empty(setup.Conversation.Attachments);
        Assert.Single(setup.Contexts.PendingItems(other));
        Assert.Empty(setup.Contexts.PendingItems(setup.Conversation.Id));

        // Taking it off there frees it for good.
        history.RemoveAttachmentCommand.Execute(history.Chips.Single());
        Assert.Empty(history.Chips);
        Assert.Empty(setup.Contexts.PendingItems(other));
    });

    [Fact]
    public void AScreenshotOfAConversationNoViewHolds_IsKeptForItsNextQuestionAndShownNowhere() => RunSta(() =>
    {
        var setup = CreateScreenshotTaker();
        var history = new HistoryViewModel(new FixedClock(Now), screens: setup.Screens);
        var unknown = Guid.NewGuid();

        Take(setup, unknown);

        Assert.Empty(history.Chips);
        Assert.Empty(setup.Conversation.Attachments);
        Assert.Single(setup.Contexts.PendingItems(unknown));
    });
}
