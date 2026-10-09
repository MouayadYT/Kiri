using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.UI.Animation;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Voice;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Foreground;
using Assistant.Windows.Placement;

namespace Assistant.UI.Views;

/// <summary>
/// The Assistant's one window (PROJECT_SPEC §4.1, §4.2): a borderless, topmost surface of dark glass over a blurred
/// backdrop that is the compact Search or Ask pill and, grown, the floating conversation. Asking from the pill folds the
/// glass into the Working pill while the answer is on its way, and the pill springs open into the panel when it comes, as in
/// the reference; a way in that has its contents ready grows the pill into the panel where it is, with the pill's contents
/// fading out and the conversation's fading in on it, so there is never a second window or a second surface. The answer to a
/// sum shows in the bar as it is typed, which grows to hold it. While voice input is on, a glow along the bottom edge follows
/// the microphone.
/// </summary>
public partial class AssistantWindow : Window, IAssistantWindow
{
    // The glass's top edge sits this far down the work area, like a system search bar; the panel's top edge is the
    // pill's, as if the bar grew into it.
    private const double VerticalPosition = 0.22;

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;

    // How far apart, in physical pixels, two points may be and still count as the same one.
    private const int SamePointTolerance = 1;

    // Pressed, as a question is asked, the bar narrows to this share of its width and loses this much of its height before it folds into the
    // Working pill, as the reference's does (measured from its recording).
    private const double PressedWidth = 0.878;
    private const double PressedHeightLoss = 3;

    // The Working pill's words come in out of a blur this long after the bar starts to fold (or after they change), in this time, and then breathe
    // between full and this much once every PulsePeriod, as in the reference.
    private static readonly TimeSpan LabelDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LabelChangeDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan LabelFade = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan PulsePeriod = TimeSpan.FromMilliseconds(2450);
    private const double PulseLow = 0.42;

    private readonly SearchOrAskViewModel _bar;
    private readonly ConversationViewModel _conversation;
    private readonly ISpokenAnswers? _speech;
    private readonly IWindowPlacementService _placement;
    private readonly Func<bool> _animationsEnabled;
    private readonly SurfaceTransition _transition;
    private readonly SurfaceMorph _morph;
    private readonly SurfaceMorphMotion _morphMotion;
    private readonly BackdropHost _backdrop;
    private readonly BackdropHost _launcherBackdrop;

    // The two forms of the glass, the window around them and where the two layers of contents rest, in DIPs. The bar is the pill at rest, grown by
    // the answer to a sum while one shows (BarForm).
    private readonly SurfaceForm _compactForm;
    private readonly SurfaceForm _conversationForm;

    // The pill that shows a result alone (a device of the home that was just switched): as wide as the panel, as tall as the result's row and its margin.
    private readonly SurfaceForm _resultForm;
    private readonly Thickness _gutter;
    private readonly double _width;
    private readonly double _expandedHeight;
    private readonly double _compactLeft;
    private readonly double _conversationLeft;

    // The answer to a sum in the bar: how much the bar grows to hold it, and how far it has grown (0 to 1), easing in and out.
    private readonly double _calculationExtra;
    private readonly FrameTween _calculation;
    private readonly TimeSpan _calculationIn;
    private readonly TimeSpan _calculationOut;

    // The list of results comes in once the typing pauses: how far it has faded in.
    private readonly FrameTween _resultsReveal;
    private bool _panelWasShown;
    private bool _panelHeldResults;

    // The panel that is up eases to a new height when what it lists changes, rather than jumping: where it eases from and to, and how far it has come.
    private readonly FrameTween _panelGrow;
    private readonly TimeSpan _panelRevealDuration;
    private readonly double _panelRevealScale;
    private readonly double _panelRevealHeight;
    private readonly TimeSpan _panelResizeDuration;
    private readonly TimeSpan _panelRevealGrowDuration;
    private double _panelFrom;
    private double _panelTo;
    private bool _panelStarting;

    // How the height is covered over the time: settling gently when a list changes, and more evenly when the panel arrives.
    private Func<double, double> _panelEase = PresenceTween.EaseOut;

    // The blur under the panel is a window of its own, and Windows moves a window sooner than it shows what this window has just drawn. While the
    // panel arrives, widening quickly, the blur would run ahead of the glass and show as a second outline around it. So while it arrives the blur
    // is held back: each frame it is given the glass as a frame some frames before left it (Motion.PanelBlurLag), and the glass as it is once it
    // is there. The line holds one glass for each frame, or nothing for a frame that changed nothing.
    private readonly IFrameSource _blurFrames;
    private readonly int _panelBlurLag;
    private readonly Queue<BackdropGlass?> _heldBlur = new();
    private BackdropGlass? _blurOfFrame;
    private bool _holdsBlur;

    // The bar opens as the bar alone; its categories come when the pointer has moved this far, in physical pixels, from where it was when the bar
    // opened (a hand resting on the mouse does not count), or when the arrow keys ask. The pointer is looked at a few times a second while they wait.
    private const int PointerMoveThreshold = 6;
    private static readonly TimeSpan PointerWatchInterval = TimeSpan.FromMilliseconds(40);
    private readonly Func<ScreenPoint?> _pointer;
    private readonly DispatcherTimer _pointerTimer;
    private ScreenPoint? _pointerAtOpen;

    // The Working pill: its height and where its words lie, how the bar folds into it and how it opens into the panel.
    private readonly double _workingHeight;
    private readonly double _workingLabelLeft;
    private readonly double _workingLabelRight;
    private readonly SpringMotion _foldSpring;
    private readonly SpringMotion _openSpring;
    private readonly TimeSpan _pressTime;
    private readonly TimeSpan _resizeTime;

    // The panel under the pill, which lists the launcher's categories while the field is empty and the search results
    // while it has text: its width, its corners, the gap above it and its height, in DIPs. Its height is the launcher's
    // fixed one, or, for results, what its sections need up to a most.
    private readonly double _launcherWidth;
    private readonly double _launcherHeight;
    private readonly double _launcherGap;
    private readonly double _launcherCorner;
    private readonly double _resultsMaxHeight;
    private double _panelHeight;

    // The Searching chip on the bar hangs the same gap below whatever is under the pill; while it shows, the window
    // is that much taller. Its height, in DIPs.
    private readonly double _chipHeight;

    private SurfaceForm _form;
    private AssistantWindowState _state;

    // How the glass is moving, and, for every motion but the eased growth, the forms it moves between.
    private GlassMotion _motion = GlassMotion.Grow;

    // The result the glass shows alone, while it does, and whether the panel is opening from it (and not from the Working pill).
    private HomeDeviceContent? _result;
    private MessageViewModel? _resultAnswer;
    private bool _fromResult;
    private SurfaceForm _from;
    private SurfaceForm _to;

    // While the bar grows into a panel that would not fit below it, where the glass's top center starts and ends, in
    // physical pixels. The window slides between them as the glass grows.
    private (ScreenPoint From, ScreenPoint To)? _slide;
    private ScreenPoint _slidePlacedAt;

    public AssistantWindow(
        SearchOrAskViewModel bar, ConversationViewModel conversation, IWindowBackdropFactory backdropFactory,
        IWindowPlacementService placement, IFileLauncher? fileLauncher = null, ISpokenAnswers? speech = null)
        : this(bar, conversation, backdropFactory, placement, () => new RenderingFrameSource(),
            () => SystemParameters.ClientAreaAnimation, fileLauncher, speech)
    {
    }

    internal AssistantWindow(
        SearchOrAskViewModel bar, ConversationViewModel conversation, IWindowBackdropFactory backdropFactory,
        IWindowPlacementService placement, Func<IFrameSource> frames, Func<bool> animationsEnabled,
        IFileLauncher? fileLauncher = null, ISpokenAnswers? speech = null, Func<ScreenPoint?>? pointer = null)
    {
        InitializeComponent();
        _pointer = pointer ?? Assistant.Windows.Capture.ScreenOverlayWindows.CursorPosition;
        _pointerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PointerWatchInterval };
        _pointerTimer.Tick += (_, _) => CheckPointer();
        _bar = bar;
        _conversation = conversation;
        _speech = speech;
        _placement = placement;
        _animationsEnabled = animationsEnabled;

        // A file in an answer opens, shows in File Explorer, or (an image, or a document the Assistant reads) is attached to this
        // conversation for the next question.
        Conversation.FileLauncher = fileLauncher;
        Conversation.CanAttach = true;
        Conversation.CanAttachDocuments = true;
        Conversation.AttachRequested += (_, image) => conversation.Attach(image);
        Conversation.DocumentAttachRequested += (_, document) => conversation.Attach(document);

        // The composer's plus offers Photos and Files, as in the reference; what is chosen is attached for the next question.
        PlusMenu = AttachMenu.Attach(
            AddButton, backdropFactory,
            pictures => AttachAndCompose(() => { foreach (var picture in pictures) conversation.Attach(picture); }),
            documents => AttachAndCompose(() => { foreach (var document in documents) conversation.Attach(document); }));

        // Pictures on the clipboard are pasted like text: into the bar they start a conversation that has them attached, and into the composer they are
        // attached to the next question.
        PromptInput.PicturesPasted += (_, pictures) => PicturesPasted?.Invoke(this, pictures);
        ComposerInput.PicturesPasted += (_, pictures) => AttachAndCompose(() => { foreach (var picture in pictures) conversation.Attach(picture); });

        // Each state's contents, and its voice glow, have their own view model.
        CompactLayer.DataContext = LauncherLayer.DataContext = BarGlow.DataContext = ActivityLayer.DataContext = bar;
        ConversationLayer.DataContext = PanelGlow.DataContext = conversation;

        // A quick action writes its request in the composer: the keyboard goes there, after it, to read, change or send.
        conversation.ComposerFocusRequested += (_, _) => ComposerInput.FocusInputAtEnd();

        // When the answer is done, or the microphone goes off, the next message can be typed at once. While the bar waits as the Working pill,
        // the answer ending opens the pill into the panel, or, with nothing to show, puts it away.
        conversation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationViewModel.IsAnswering) && IsWaitingForAnswer && !conversation.IsAnswering)
            {
                if (HasAnswer)
                {
                    OpenAnswer();
                }
                else
                {
                    Dismiss();
                }
            }

            if (e.PropertyName == nameof(ConversationViewModel.CanCompose) && conversation.CanCompose
                && _state == AssistantWindowState.FloatingConversation && IsActive)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (conversation.CanCompose && IsActive)
                    {
                        ComposerInput.FocusInput();
                    }
                });
            }
        };

        var compact = new Size((double)FindResource("Compact.Width"), (double)FindResource("Compact.Height"));
        var panel = new Size((double)FindResource("Conversation.PanelWidth"), (double)FindResource("Conversation.PanelHeight"));
        _gutter = (Thickness)FindResource("Assistant.ShadowGutter");
        _compactForm = SurfaceForm.Pill(compact.Width, compact.Height);
        _conversationForm = SurfaceForm.Panel(panel.Width, panel.Height, (double)FindResource("Radius.Panel"));
        _resultForm = SurfaceForm.Pill(panel.Width, (double)FindResource("Result.Height"));
        _form = _from = _to = _compactForm;
        _width = Math.Max(compact.Width, panel.Width) + _gutter.Left + _gutter.Right;
        _expandedHeight = _gutter.Top + panel.Height + _gutter.Bottom;
        _compactLeft = (_width - compact.Width) / 2;
        _conversationLeft = (_width - panel.Width) / 2;
        Root.Width = _width;
        Conversation.Clip = _conversationForm.CreateGeometry(new Rect(0, 0, panel.Width, panel.Height));

        var launcher = new Size((double)FindResource("Launcher.Width"), (double)FindResource("Launcher.Height"));
        _launcherWidth = launcher.Width;
        _launcherHeight = _panelHeight = _panelFrom = _panelTo = launcher.Height;
        _launcherGap = (double)FindResource("Launcher.Gap");
        _launcherCorner = (double)FindResource("Radius.Launcher");
        _resultsMaxHeight = (double)FindResource("SearchResults.MaxHeight");
        _chipHeight = (double)FindResource("Size.ActivityChip");
        LauncherShadowLayer.Clip = CreateLauncherShadowClip(compact, launcher);

        _calculationExtra = (double)FindResource("Compact.CalculationExtra");
        _calculationIn = (TimeSpan)FindResource("Motion.CalculationIn");
        _calculationOut = (TimeSpan)FindResource("Motion.CalculationOut");
        _panelResizeDuration = (TimeSpan)FindResource("Motion.PanelResize");
        _panelRevealDuration = (TimeSpan)FindResource("Motion.PanelReveal");
        _panelRevealScale = (double)FindResource("Motion.PanelRevealScale");
        _panelRevealHeight = (double)FindResource("Motion.PanelRevealHeight");
        _panelRevealGrowDuration = (TimeSpan)FindResource("Motion.PanelRevealGrow");
        _panelBlurLag = (int)FindResource("Motion.PanelBlurLag");

        _workingHeight = (double)FindResource("Working.Height");
        _workingLabelLeft = (double)FindResource("Working.LabelLeft");
        _workingLabelRight = (double)FindResource("Working.LabelRight");
        _foldSpring = (SpringMotion)FindResource("Motion.PillFold");
        _openSpring = (SpringMotion)FindResource("Motion.PillOpen");
        _pressTime = (TimeSpan)FindResource("Motion.PillPress");
        _resizeTime = (TimeSpan)FindResource("Motion.PillResize");

        _backdrop = BackdropHost.Attach(this, Surface, size => _form.GetBackdropCornerRadii(size), backdropFactory);

        // The launcher's panel is glass of its own, so it has a blur of its own; the pill's owns the window's stacking.
        _launcherBackdrop = BackdropHost.Attach(
            this, LauncherSurface, size => PanelShape.GetBackdropCornerRadii(size, _launcherCorner), backdropFactory,
            ownsWindow: false);

        // The whole surface, shadow and glow included, arrives and leaves together, and the blur beneath it follows.
        _transition = new SurfaceTransition(
            this, SurfaceHost, (SurfaceMotion)FindResource("Motion.FloatingSurface"), frames(), animationsEnabled);
        _transition.FrameApplied += (_, _) => MatchBackdrops();

        _morphMotion = (SurfaceMorphMotion)FindResource("Motion.SurfaceMorph");
        _morph = new SurfaceMorph(_morphMotion, frames(), animationsEnabled);
        _morph.Changed += (_, _) => ApplyMorph();
        _morph.Arrived += OnMorphArrived;

        // The answer to a sum grows the bar to hold it; the panel under the bar grows down out of it when it is revealed, a little narrower at
        // first and fading in (its height is the panel's own, eased below).
        _calculation = new FrameTween(frames(), animationsEnabled);
        _calculation.Changed += (_, _) => ApplyCalculation();
        _resultsReveal = new FrameTween(frames(), animationsEnabled, 1, PresenceTween.Evenly);
        _resultsReveal.Changed += (_, _) =>
        {
            // It fades in evenly, as the reference's does, and widens quickly at first.
            var time = _resultsReveal.Value;
            LauncherLayer.Opacity = time;
            LauncherScale.ScaleX = LauncherScale.ScaleY = _panelRevealScale + ((1 - _panelRevealScale) * PresenceTween.EaseOutGently(time));
            var glass = new Size(_form.Width, _form.Height);
            UpdateShadow(glass, _form.CreateGeometry(new Rect(glass)));
            MatchBackdrops();
            ReleaseBlurOnceArrived();
        };
        _panelGrow = new FrameTween(frames(), animationsEnabled, 1, PresenceTween.Evenly);
        _panelGrow.Changed += (_, _) => ApplyPanelGrow();
        _blurFrames = frames();
        _blurFrames.Frame += (_, _) => GiveHeldBlur();
        SetRest(AssistantWindowState.Compact);

        // The category panel is there while the field is empty, and the results while it has text, and only on the bar.
        bar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SearchOrAskViewModel.IsLauncherVisible) or nameof(SearchOrAskViewModel.IsResultsVisible))
            {
                UpdateLauncher();
            }
            else if (e.PropertyName == nameof(SearchOrAskViewModel.HasCalculation))
            {
                UpdateCalculation();
            }
        };

        // New sections change how tall the panel is, and start again from their top; so do the words the list says when it is empty or
        // when what was chosen could not be done, and the list of a result's actions that takes the chips away.
        bar.Results.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SearchResultsViewModel.Sections):
                    ResultsScroller.ScrollToTop();
                    UpdateLauncher();
                    break;
                case nameof(SearchResultsViewModel.EmptyMessage):
                case nameof(SearchResultsViewModel.Notice):
                case nameof(SearchResultsViewModel.IsShowingAlternates):
                    UpdateLauncher();
                    break;
            }
        };

        // The Searching chip shows on whichever surface is up while something runs, and makes room for itself on the bar; the Working pill
        // says the same words.
        if ((bar.Activity ?? conversation.Activity) is { } activity)
        {
            activity.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ActivityViewModel.IsVisible))
                {
                    UpdateChips();
                }

                if (e.PropertyName is nameof(ActivityViewModel.IsVisible) or nameof(ActivityViewModel.StatusText))
                {
                    UpdateWorkingLabel();
                }
            };
        }

        BarChip.IsVisibleChanged += (_, _) => UpdateLauncher();
        UpdateChips();

        Loaded += (_, _) => FocusContent();

        // The glass moves the window; the editor, the microphone, buttons and messages, which will hold links, code,
        // cards and galleries, never do.
        SurfaceDrag.Attach(this, element => IsMessage(element) || element is SearchResultRow or ActivityChip).Dragged += (_, _) => OnDragged();

        // The newest message is the one to read; the first words of an answer open the Working pill into the panel.
        conversation.Messages.CollectionChanged += OnMessagesChanged;

        // A hidden window is not listening, and its next appearance is the bar again.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) OnHidden();
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnMessage);
        Closed += (_, _) =>
        {
            _morph.Dispose();
            _calculation.Dispose();
            _resultsReveal.Dispose();
            _panelGrow.Dispose();
            _blurFrames.Stop();
            _pointerTimer.Stop();
        };
    }

    // How the glass moves: growing from the bar into the panel (eased, and back), folding from the bar into the Working pill, resting as the pill
    // while the answer is on its way, widening or narrowing as the pill's words change, and opening from the pill into the panel.
    private enum GlassMotion
    {
        Grow,
        Fold,
        Wait,
        Resize,
        Open,

        // Opening from the Working pill into the pill that shows a result alone, and resting as it.
        Result,
        Rest,
    }

    /// <inheritdoc/>
    public AssistantWindowState State => _state;

    /// <inheritdoc/>
    public event EventHandler? AssistantStateChanged;

    /// <inheritdoc/>
    public event EventHandler? Moved;

    /// <inheritdoc/>
    public event EventHandler? Expanded;

    /// <inheritdoc/>
    public event EventHandler? Hidden;

    /// <inheritdoc/>
    public event EventHandler<IReadOnlyList<ImageItem>>? PicturesPasted;

    /// <summary>Whether the glass is on its way from the pill to the panel.</summary>
    internal bool IsExpanding => _morph.IsRunning && _motion is GlassMotion.Grow or GlassMotion.Open;

    /// <summary>How far the glass has come from the pill (0) to the panel (1), after easing.</summary>
    internal double ExpansionProgress => _morph.Progress;

    /// <summary>
    /// Whether the bar has folded, or is folding, into the Working pill and waits there for the answer: it says what the Assistant is doing and
    /// opens into the panel when the answer comes.
    /// </summary>
    internal bool IsWaitingForAnswer => IsVisible && _motion is GlassMotion.Fold or GlassMotion.Wait or GlassMotion.Resize;

    /// <summary>
    /// Whether the glass shows a result alone, as a pill (a device of the home that was just switched), or is opening into it: pressing it opens the
    /// conversation around it.
    /// </summary>
    internal bool IsShowingResult => IsVisible && _motion is GlassMotion.Result or GlassMotion.Rest;

    /// <summary>What the Working pill says.</summary>
    internal string WorkingText => WorkingLabel.Text;

    /// <summary>How far the bar has grown to hold the answer to a sum, from 0 to 1.</summary>
    internal double CalculationProgress => _calculation.Value;

    /// <summary>The glass's form as it is drawn now.</summary>
    internal SurfaceForm Form => _form;

    /// <inheritdoc/>
    public ScreenPoint? SurfaceTop => _placement.GetAnchorTop(Handle, Layout(_state));

    /// <inheritdoc/>
    public bool IsShowing => IsVisible && !_transition.IsHiding;

    /// <summary>
    /// Brings the window forward and focuses its contents: the draft in the bar's editor, or the conversation. A hidden
    /// window first moves to <paramref name="surfaceTop"/>, the point for the glass's top center, such as where the
    /// user dragged it, or else to the monitor the user is working on, then animates in as the bar; one that is on its
    /// way out turns back. A visible window stays where it is, in whichever state it is in. A bar that opens again on what
    /// was typed before has it selected.
    /// </summary>
    public void ShowAndFocus(ScreenPoint? surfaceTop = null)
    {
        var opening = !IsShowing;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (opening) WatchPointer();
        if (!IsVisible) Place(surfaceTop);
        _transition.Show();
        Activate();
        FocusContent(opening);
    }

    public void ShowAtPointer()
    {
        var opening = !IsShowing;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (opening) WatchPointer();
        _placement.PlaceOnCursorMonitor(Handle, Layout(_state));
        _transition.Show();
        Activate();
        FocusContent(opening);
    }

    /// <summary>
    /// Shows the window as the floating conversation, for a way in that does not start from the bar (PROJECT_SPEC §4.2).
    /// Placement is as for <see cref="ShowAndFocus"/>. A visible bar grows into the conversation where it is, and the Working pill opens into it.
    /// </summary>
    public void ShowConversation(ScreenPoint? surfaceTop = null)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible)
        {
            SetRest(AssistantWindowState.FloatingConversation);
            Place(surfaceTop);
        }
        else if (_state == AssistantWindowState.Compact)
        {
            ExpandToConversation();
        }
        else if (IsWaitingForAnswer)
        {
            OpenFromPill();
        }
        else if (IsShowingResult)
        {
            OpenFromResult();
        }

        _transition.Show();
        Activate();
        FocusContent();
    }

    /// <inheritdoc/>
    public void ShowConversationNear(NearWindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var point = _placement.FindAnchorTopNear(target, Layout(AssistantWindowState.FloatingConversation));
        var movesShown = point is not null && IsVisible && _state == AssistantWindowState.FloatingConversation && !IsExpanding && !IsWaitingForAnswer;
        ShowConversation(point);

        // A panel that shows is not moved by showing it again, but this way in says where the user is now.
        if (movesShown)
        {
            _placement.PlaceAnchorTopAt(Handle, Layout(_state), point!.Value);
        }
    }

    /// <inheritdoc/>
    public bool TakeForeground()
    {
        if (!IsVisible)
        {
            return false;
        }

        // An ordinary request first; when Windows refuses it, one made as the thread in front would make it. IsActive is not the test: a
        // window can be the active one of its own thread while another application still has the foreground.
        Activate();
        var inFront = ForegroundWindow.IsForeground(Handle) || ForegroundWindow.TryActivate(Handle);
        FocusContent();
        return inFront;
    }

    /// <inheritdoc/>
    public void ExpandToConversation()
    {
        if (_state != AssistantWindowState.Compact || !IsVisible)
        {
            return;
        }

        // The panel is taller than the bar, and may not fit below it: then the window slides up as the glass grows,
        // so what the user sees is the glass reaching up as well as down.
        var from = SurfaceTop;
        _motion = GlassMotion.Grow;
        SetState(AssistantWindowState.FloatingConversation);
        UpdateLauncher();
        Root.Height = _expandedHeight;
        _slide = PlanSlide(from);

        // The conversation's layer is shown, still clear, and takes the focus now: the text the user typed stays where
        // it is, fading, but keys go to the conversation, and the window never loses the keyboard on the way.
        ApplyMorph();
        Conversation.ScrollToEnd();
        FocusContent();
        _morph.GrowTo(1);
    }

    /// <inheritdoc/>
    public void AwaitAnswer()
    {
        if (_state != AssistantWindowState.Compact || !IsVisible)
        {
            return;
        }

        // An answer that is there already (a sum, a sample) has nothing to wait for: the bar grows straight into it.
        if (HasAnswer || !_conversation.IsAnswering)
        {
            ExpandToConversation();
            return;
        }

        // As in the reference: the bar is pressed a little narrower, then springs into the Working pill, which hangs from the same line. The
        // conversation's layer is there, still clear, and has the keyboard, as when the bar grows.
        var bar = _form;
        SetState(AssistantWindowState.FloatingConversation);
        UpdateLauncher();
        Root.Height = _expandedHeight;
        _slide = null;
        var label = CurrentWorkingText();
        WorkingLabel.Text = label;
        _motion = GlassMotion.Fold;
        _from = new SurfaceForm(
            bar.Width * PressedWidth, Math.Max(_workingHeight, bar.Height - PressedHeightLoss), bar.CornerWidth, bar.CornerHeight, bar.ControlRatio);
        _to = WorkingForm(label);
        UpdateChips();
        StartWorkingLabel(LabelDelay);
        ApplyMorph();
        FocusContent();
        _morph.SpringFromStart(_foldSpring, _pressTime);
    }

    /// <summary>
    /// Turns voice input off, animates the window out and hides it. It is only hidden, never closed, so the draft and
    /// the conversation are still there the next time it opens.
    /// </summary>
    public void Dismiss()
    {
        _bar.Voice.Stop();
        _conversation.Voice.Stop();

        // The Assistant's voice goes quiet at once, not when the window has finished leaving.
        _speech?.Stop();
        _transition.Hide();
    }

    /// <inheritdoc/>
    public bool HideNow()
    {
        var wasShowing = IsVisible;
        _bar.Voice.Stop();
        _conversation.Voice.Stop();
        _speech?.Stop();
        _transition.HideNow();
        return wasShowing;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Handy hands its transcript over by pasting it: while a spoken request waits for it, the paste is taken here, wherever the keyboard is (the
        // floating conversation has no text field up while the microphone is on).
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && HandySpeechToTextService.TryAcceptClipboard())
        {
            e.Handled = true;
            base.OnPreviewKeyDown(e);
            return;
        }

        // Esc on the Working pill stops the answer on its way and puts the pill away, as the reference's does.
        if (e.Key == Key.Escape && (IsWaitingForAnswer || IsShowingResult))
        {
            e.Handled = true;
            _conversation.Activity?.Cancel();
            _conversation.Stop();
            Dismiss();
            base.OnPreviewKeyDown(e);
            return;
        }

        // Categories that wait for the pointer come for the arrow keys too, and their own shortcuts work before they are seen.
        if (_state == AssistantWindowState.Compact && _bar.IsLauncherWaiting
            && ((Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Down or Key.Up) || Keyboard.Modifiers == ModifierKeys.Control))
        {
            if (e.Key is Key.Down or Key.Up)
            {
                _pointerTimer.Stop();
                _bar.RevealLauncher();
            }

            if (_bar.Launcher.HandleKey(e.Key, Keyboard.Modifiers))
            {
                e.Handled = true;
                base.OnPreviewKeyDown(e);
                return;
            }
        }

        // While the category panel shows, the arrow keys and Enter, and the categories' own shortcuts, are its: the
        // editor never sees them. Everything else goes to the editor, and typing takes the panel away.
        // The same goes for the arrow keys and Enter on the results, when one is highlighted.
        if ((IsLauncherShown && _bar.Launcher.HandleKey(e.Key, Keyboard.Modifiers)) ||
            (IsResultsListening && _bar.Results.HandleKey(e.Key, Keyboard.Modifiers)))
        {
            e.Handled = true;
            base.OnPreviewKeyDown(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            var dismiss = _state == AssistantWindowState.Compact ? _bar.HandleEscape() : _conversation.HandleEscape();
            if (dismiss)
            {
                Dismiss();
            }
        }

        base.OnPreviewKeyDown(e);
    }

    // Typing while a result shows alone is the start of the next question: the panel opens, and what was typed is in its composer.
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (IsShowingResult && e.Text.Length > 0 && !char.IsControl(e.Text[0]) && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            e.Handled = true;
            _conversation.Draft += e.Text;
            OpenFromResult();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ComposerInput.FocusInputAtEnd());
        }

        base.OnPreviewTextInput(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Focus that comes back while the window is leaving, such as a click on it, brings it back.
        if (_transition.IsHiding) _transition.Show();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (_state == AssistantWindowState.FloatingConversation)
        {
            // The panel stays up for reading while the user works elsewhere, with its conversation, but it stops
            // listening.
            _conversation.Voice.Stop();
            return;
        }

        // Losing focus dismisses the bar (PROJECT_SPEC §4.1) without discarding anything. The check waits for the
        // dispatcher, so focus that returns at once, or a hide that caused the deactivation, leaves the bar alone.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsVisible && !IsActive && _state == AssistantWindowState.Compact) Dismiss();
        });
    }

    private void OnCloseExecuted(object sender, ExecutedRoutedEventArgs e) => Dismiss();

    // Alt+F4 and the system menu's Close dismiss the panel like its close button, rather than destroying it. On the bar
    // they close the window, which ends the application. The app still closes the window when it shuts down.
    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WM_SYSCOMMAND && (wParam & 0xFFF0) == SC_CLOSE && _state == AssistantWindowState.FloatingConversation)
        {
            handled = true;
            Dismiss();
        }

        return 0;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Conversation.ScrollToEnd());
        }

        // The answer has come, or begun to: the Working pill opens into the panel with it.
        if (IsWaitingForAnswer && HasAnswer)
        {
            OpenAnswer();
        }
    }

    // However it was hidden, the window is not listening, and it opens as the bar the next time.
    private void OnHidden()
    {
        _bar.Voice.Stop();
        _conversation.Voice.Stop();
        _speech?.Stop();
        _pointerTimer.Stop();
        _bar.ResetLauncher();
        _bar.Launcher.ClearSelection();
        _bar.Results.CloseAlternates();
        _bar.Results.ShowNotice("");
        _bar.Results.ClearSelection();
        SetRest(AssistantWindowState.Compact);
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    // Puts the surface at rest in a state: the glass in that form, with that state's contents alone showing.
    private void SetRest(AssistantWindowState state)
    {
        _slide = null;
        _motion = GlassMotion.Grow;
        ForgetResult();
        StopWorkingLabel();
        SetState(state);
        _calculation.JumpTo(_bar.HasCalculation ? 1 : 0);
        UpdateLauncher();
        _morph.JumpTo(state == AssistantWindowState.Compact ? 0 : 1);
    }

    /// <summary>Whether the launcher's category panel is showing under the pill.</summary>
    internal bool IsLauncherShown => _state == AssistantWindowState.Compact && _bar.IsLauncherVisible;

    /// <summary>Whether the search results are showing under the pill, in the same panel.</summary>
    internal bool IsResultsShown => _state == AssistantWindowState.Compact && _bar.IsResultsVisible;

    // The keys of the results are theirs while they show, and while there are results for what is typed that are not listed yet (its best match
    // is beside the text, and Enter opens it; the arrow keys show the list) or are on their way: a Tab pressed then waits for them (see
    // SearchResultsViewModel.HandleKey) instead of moving the focus to the microphone.
    private bool IsResultsListening =>
        IsResultsShown || (_state == AssistantWindowState.Compact && _bar.Query.Length > 0 && (_bar.Results.IsSearching || _bar.Results.HasResults));

    private bool IsPanelShown => IsLauncherShown || IsResultsShown;

    /// <summary>Whether the Searching chip is up under the bar, appearing, shown or leaving.</summary>
    internal bool IsChipShown => _state == AssistantWindowState.Compact && BarChip.Visibility == Visibility.Visible;

    // The bar as it is drawn at rest: the pill, grown by as much of the answer to a sum as shows.
    private SurfaceForm BarForm => _calculation.Value <= 0
        ? _compactForm
        : new SurfaceForm(
            _compactForm.Width, _compactForm.Height + (_calculationExtra * _calculation.Value), _compactForm.CornerWidth, _compactForm.CornerHeight,
            _compactForm.ControlRatio);

    // Whether the conversation holds an answer to show.
    private bool HasAnswer => _conversation.Messages.Any(message => message.Role == MessageRole.Assistant);

    // How much taller the window is while the bar's chip shows: the gap above it and its height.
    private double ChipExtra => _launcherGap + _chipHeight;

    // How far below the bar the panel reaches as it is drawn now: the gap above it and its height.
    private double PanelExtra => _launcherGap + _panelHeight;

    // The room the window keeps for the panel: for the taller of where it eases from and to while it eases, so that the window is resized
    // once for a change of height and not with every frame of it.
    private double PanelRoom => _launcherGap + (_panelGrow.IsRunning ? Math.Max(_panelFrom, _panelTo) : _panelHeight);

    // The window is as tall as the bar, plus the panel while it shows, or the floating conversation, and its shadow.
    // Growing the window downward never moves the glass, so the pill stays where it was.
    private double RestHeight(AssistantWindowState state) => state == AssistantWindowState.Compact
        ? _gutter.Top + BarForm.Height + _gutter.Bottom + (_bar.IsLauncherVisible || _bar.IsResultsVisible ? PanelRoom : 0) + (IsChipShown ? ChipExtra : 0)
        : _expandedHeight;

    // Shows or hides the panel, with the categories or the results in it, to match the bar and the window's state, and
    // makes the panel and the window as tall as they need to be. A panel that would reach past the bottom of the
    // screen lifts the window until it fits. A panel that arrives on a bar that is up (the list of results once the typing
    // pauses, the categories once the pointer moves) grows down out of the bar to the height it needs; a list that is up already
    // eases to a new height, so that the glass stays still under the rows while what is typed changes.
    private void UpdateLauncher()
    {
        var launcherShown = IsLauncherShown;
        var resultsShown = IsResultsShown;
        var shown = launcherShown || resultsShown;
        var arriving = shown && !_panelWasShown && IsShowing;
        if (arriving)
        {
            // Where the reveal starts from (unseen, and narrower) is how the panel is drawn before it is there to draw at all: laying it out
            // resizes the window, and a window draws itself as it is resized, which would show the panel whole for a frame before it fades in.
            _resultsReveal.JumpTo(0);
            if (_animationsEnabled())
            {
                HoldBlur();
            }
        }

        LauncherLayer.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        LauncherLayer.IsHitTestVisible = shown;
        LauncherList.Visibility = launcherShown ? Visibility.Visible : Visibility.Collapsed;
        ResultsHost.Visibility = resultsShown ? Visibility.Visible : Visibility.Collapsed;
        var height = resultsShown ? MeasureResultsHeight() : _launcherHeight;
        if (arriving)
        {
            // It grows down from under the bar: from part of its height to all of it, while it widens and fades in. The height takes a little
            // longer than the rest, so that the bottom edge is still sliding down over the rows when they are there to read.
            GrowPanel(Math.Round(height * _panelRevealHeight), height, _panelRevealGrowDuration);
            _resultsReveal.MoveTo(1, _panelRevealDuration);
        }
        else if (!shown)
        {
            SetPanelHeight(height, ease: false);
            _resultsReveal.JumpTo(1);
            ReleaseBlur();
        }
        else if (!(_panelGrow.IsRunning && _panelTo == height))
        {
            // Only a list that stays a list eases: the categories giving way to results, or back, is another panel, at its own height at once.
            SetPanelHeight(height, ease: _panelWasShown && _panelHeldResults && resultsShown && IsShowing);
        }

        _panelWasShown = shown;
        _panelHeldResults = resultsShown;
        ArrangePanel();
    }

    // Places the panel and the chip under the bar, as tall as the panel is drawn now, and the window around them.
    private void ArrangePanel()
    {
        var shown = IsPanelShown;

        // The panel and the chip hang below the bar, however tall the answer to a sum has made it.
        var bar = BarForm.Height;
        LauncherLayer.Margin = new Thickness(0, _gutter.Top + bar + _launcherGap, 0, 0);
        LauncherShadowLayer.Clip = CreateLauncherShadowClip(new Size(_compactForm.Width, bar), new Size(_launcherWidth, _panelHeight));
        ActivityLayer.Margin = new Thickness(0, _gutter.Top + bar + (shown ? PanelExtra : 0) + _launcherGap, 0, 0);
        Root.Height = RestHeight(_state);
        if (PresentationSource.FromVisual(this) is not null)
        {
            UpdateLayout();
            ApplyMorph();
            if ((shown || IsChipShown) && IsVisible && SurfaceTop is { } top &&
                _placement.FitAnchorTop(Layout(AssistantWindowState.Compact), top) is { } fitted && fitted != top)
            {
                _placement.PlaceAnchorTopAt(Handle, Layout(AssistantWindowState.Compact), fitted);
            }
        }
    }

    // The panel is as tall as its chips and sections, no taller than the reference's, beyond which they scroll.
    private double MeasureResultsHeight()
    {
        // The list takes its sections from a binding, which may not have seen the newest yet when results arrive on their own
        // (the providers answer while the user is not typing): it, and the words around it, are brought up to date before they are measured.
        ResultsList.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
        ResultsNotice.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        ResultsNotice.GetBindingExpression(VisibilityProperty)?.UpdateTarget();
        ResultsEmpty.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        ResultsEmpty.GetBindingExpression(VisibilityProperty)?.UpdateTarget();
        ScopeChips.GetBindingExpression(VisibilityProperty)?.UpdateTarget();

        var chips = 0.0;
        if (ScopeChips.Visibility == Visibility.Visible)
        {
            ScopeChips.Measure(new Size(_launcherWidth, double.PositiveInfinity));
            chips = ScopeChips.DesiredSize.Height;
        }

        ResultsContent.Measure(new Size(_launcherWidth, double.PositiveInfinity));
        return Math.Min(chips + ResultsContent.DesiredSize.Height, _resultsMaxHeight);
    }

    // Gives the panel a height: at once, or, for a panel that is up, eased from the height it has.
    private void SetPanelHeight(double height, bool ease, TimeSpan? duration = null)
    {
        if (!ease || !_animationsEnabled())
        {
            _panelFrom = _panelTo = height;
            _panelGrow.JumpTo(1);
            if (height != _panelHeight)
            {
                _panelHeight = height;
                LauncherLayer.Height = height;
            }

            return;
        }

        if (height == _panelTo)
        {
            return;
        }

        // From wherever it is, which is part of the way when it was easing already.
        _panelFrom = _panelTo = _panelHeight;
        _panelGrow.JumpTo(0);
        _panelEase = PresenceTween.EaseOut;
        _panelTo = height;
        _panelGrow.MoveTo(1, duration ?? _panelResizeDuration);
    }

    // Starts the panel at one height on its way to another. The window is arranged for it once, by whoever asked, with room for the taller of
    // the two: not once for where it starts and again for where it ends.
    private void GrowPanel(double from, double to, TimeSpan duration)
    {
        if (!_animationsEnabled() || from == to)
        {
            SetPanelHeight(to, ease: false);
            return;
        }

        _panelStarting = true;
        try
        {
            _panelGrow.JumpTo(0);
            _panelEase = PresenceTween.EaseOutGently;
            _panelFrom = from;
            _panelTo = to;
            _panelHeight = from;
            LauncherLayer.Height = from;
            _panelGrow.MoveTo(1, duration);
        }
        finally
        {
            _panelStarting = false;
        }
    }

    // A frame of the panel easing to its height.
    private void ApplyPanelGrow()
    {
        if (_panelStarting)
        {
            return;
        }

        var height = _panelFrom + ((_panelTo - _panelFrom) * _panelEase(_panelGrow.Value));
        if (height == _panelHeight && _panelGrow.IsRunning)
        {
            return;
        }

        _panelHeight = height;
        LauncherLayer.Height = height;
        if (_state == AssistantWindowState.Compact)
        {
            ArrangePanel();
        }

        ReleaseBlurOnceArrived();
    }

    /// <summary>The menu the composer's plus opens.</summary>
    internal ContextMenu PlusMenu { get; }

    /// <summary>How tall the panel under the bar is drawn now, in DIPs.</summary>
    internal double PanelHeight => _panelHeight;

    // The bar is opening: its categories wait for the pointer to move from where it is now.
    private void WatchPointer()
    {
        _bar.ResetLauncher();
        if (!_bar.LauncherWaitsForPointer)
        {
            return;
        }

        _pointerAtOpen = _pointer();
        _pointerTimer.Start();
    }

    /// <summary>
    /// Looks where the pointer is, and brings the categories when it has moved since the bar opened. The window does it itself a few times a
    /// second while the categories wait.
    /// </summary>
    internal void CheckPointer()
    {
        if (!IsVisible || _state != AssistantWindowState.Compact || !_bar.LauncherWaitsForPointer || _bar.IsLauncherVisible)
        {
            _pointerTimer.Stop();
            return;
        }

        if (_pointer() is not { } now)
        {
            return;
        }

        if (_pointerAtOpen is not { } then)
        {
            _pointerAtOpen = now;
            return;
        }

        if (Math.Abs(now.X - then.X) + Math.Abs(now.Y - then.Y) >= PointerMoveThreshold)
        {
            _pointerTimer.Stop();
            _bar.RevealLauncher();
        }
    }

    // The answer to a sum comes into the bar as the bar grows to hold it, and goes as quickly as the sum does. Only the bar that shows animates;
    // anything else rests as it will show.
    private void UpdateCalculation()
    {
        var target = _bar.HasCalculation ? 1 : 0;
        if (!IsShowing || _state != AssistantWindowState.Compact)
        {
            _calculation.JumpTo(target);
            ApplyCalculation();
            return;
        }

        _calculation.MoveTo(target, target == 1 ? _calculationIn : _calculationOut);
    }

    private void ApplyCalculation()
    {
        var shown = _calculation.Value;
        CalculationCard.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        CalculationCard.Opacity = shown;
        if (_state == AssistantWindowState.Compact)
        {
            UpdateLauncher();
        }
    }

    // The blur beneath each piece of glass matches it as it is drawn now; the panel's, while the panel arrives, a moment later.
    private void MatchBackdrops()
    {
        _backdrop.MatchGlass();
        if (!_holdsBlur)
        {
            _launcherBackdrop.MatchGlass();
        }
        else if (_launcherBackdrop.Measure() is { } glass)
        {
            _blurOfFrame = glass;
        }
    }

    // From here the panel's blur follows its glass a few frames late, until the panel is there.
    private void HoldBlur()
    {
        _heldBlur.Clear();
        _blurOfFrame = null;
        _holdsBlur = true;
        _launcherBackdrop.IsHeld = true;
        _blurFrames.Start();
    }

    // A frame begins while the blur is held back: the glass as it was left by the frame before joins the line, and the blur is given the one that
    // has waited its frames.
    private void GiveHeldBlur()
    {
        _heldBlur.Enqueue(_blurOfFrame);
        _blurOfFrame = null;
        if (_heldBlur.Count >= _panelBlurLag && _heldBlur.Dequeue() is { } glass)
        {
            _launcherBackdrop.Give(glass);
        }
    }

    // The panel is there, or gone: its blur matches it at once again.
    private void ReleaseBlur()
    {
        if (_holdsBlur)
        {
            _holdsBlur = false;
            _heldBlur.Clear();
            _blurOfFrame = null;
            _blurFrames.Stop();
            _launcherBackdrop.IsHeld = false;
            _launcherBackdrop.MatchGlass();
        }
    }

    private void ReleaseBlurOnceArrived()
    {
        if (_holdsBlur && !_resultsReveal.IsRunning && !_panelGrow.IsRunning)
        {
            ReleaseBlur();
        }
    }

    private void SetState(AssistantWindowState state)
    {
        if (_state != state)
        {
            _state = state;
            UpdateChips();
            AssistantStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // What the Assistant is doing shows on the surface that is up: the Searching chip under the bar, or, in the panel, the indicator in the
    // conversation where the answer will be, with the composer left as it is. The one the surface has just left goes at once. The Working pill
    // says the same in its own words.
    private void UpdateChips()
    {
        var activity = (_bar.Activity ?? _conversation.Activity)?.IsVisible == true;
        ActivityLayer.Visibility = _state == AssistantWindowState.Compact ? Visibility.Visible : Visibility.Collapsed;
        BarChip.IsActive = activity && _state == AssistantWindowState.Compact;
        Conversation.IsWorking = activity && _state == AssistantWindowState.FloatingConversation && !IsWaitingForAnswer && !IsShowingResult;
    }

    // Draws the surface at the morph's progress: the glass, its shadow, its glow and every layer of contents.
    private void ApplyMorph()
    {
        var progress = _morph.Progress;
        _form = _motion == GlassMotion.Grow
            ? SurfaceForm.Blend(BarForm, _conversationForm, progress)
            : SurfaceForm.Interpolate(_from, _to, progress);
        var size = new Size(_form.Width, _form.Height);
        var outline = _form.CreateGeometry(new Rect(size));
        SurfaceArea.Width = size.Width;
        SurfaceArea.Height = size.Height;
        Surface.Form = ShadowCaster.Form = _form;
        UpdateShadow(size, outline);
        GlowLayer.Clip = outline;

        // Each layer stays where it rests and is clipped to the glass, so nothing sticks out of it as it grows. Folding, the bar's words go with
        // its left edge as it is pressed narrower, as the reference's do.
        var glassLeft = (_width - size.Width) / 2;
        var (compactOpacity, workingOpacity, conversationOpacity) = LayerOpacities(progress);
        var shift = _motion == GlassMotion.Fold ? glassLeft - _compactLeft : 0;
        CompactLayer.Opacity = compactOpacity;
        CompactLayer.Visibility = compactOpacity > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactLayer.IsHitTestVisible = _state == AssistantWindowState.Compact;
        CompactLayer.RenderTransform = shift == 0 ? Transform.Identity : new TranslateTransform(shift, 0);
        CompactLayer.Clip = _form.CreateGeometry(new Rect(glassLeft - _compactLeft - shift, 0, size.Width, size.Height));

        WorkingLayer.Opacity = workingOpacity;
        WorkingLayer.Visibility = workingOpacity > 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkingLayer.Width = size.Width;
        WorkingLayer.Clip = outline;
        WorkingIndicator.IsActive = workingOpacity > 0;
        var ring = _motion == GlassMotion.Fold ? 0.4 + (0.6 * Ramp(progress, 0.3, 1)) : 1;
        WorkingIndicatorScale.ScaleX = WorkingIndicatorScale.ScaleY = 1.1 * ring;

        // The result that shows alone comes in as the pill opens into it, and goes as the panel opens from it.
        var resultOpacity = _motion switch
        {
            GlassMotion.Result => Ramp(progress, 0.2, 0.85),
            GlassMotion.Rest => 1,
            GlassMotion.Open when _fromResult => 1 - Ramp(progress, 0, 0.2),
            _ => 0,
        };
        ResultLayer.Opacity = resultOpacity;
        ResultLayer.Visibility = resultOpacity > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultLayer.IsHitTestVisible = _motion == GlassMotion.Rest;
        ResultLayer.Clip = _form.CreateGeometry(new Rect(glassLeft - _conversationLeft, 0, size.Width, size.Height));

        ConversationLayer.Opacity = conversationOpacity;
        ConversationLayer.Visibility = conversationOpacity > 0 || _state == AssistantWindowState.FloatingConversation
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConversationLayer.IsHitTestVisible = _state == AssistantWindowState.FloatingConversation && _motion is GlassMotion.Grow or GlassMotion.Open;

        // While the spring carries the glass past the panel's size, the conversation's contents stretch with it from the top, so that they
        // bounce with the glass as the reference's do, rather than standing still inside it. Up to there they are clipped to the glass.
        var stretchX = size.Width / _conversationForm.Width;
        var stretchY = size.Height / _conversationForm.Height;
        if (_motion == GlassMotion.Open && stretchX >= 1 && stretchY >= 1 && (stretchX > 1 || stretchY > 1))
        {
            ConversationLayer.RenderTransformOrigin = new Point(0.5, 0);
            ConversationLayer.RenderTransform = new ScaleTransform(stretchX, stretchY);
            ConversationLayer.Clip = _conversationForm.CreateGeometry(new Rect(0, 0, _conversationForm.Width, _conversationForm.Height));
        }
        else
        {
            ConversationLayer.RenderTransform = Transform.Identity;
            ConversationLayer.Clip = _form.CreateGeometry(new Rect(glassLeft - _conversationLeft, 0, size.Width, size.Height));
        }

        if (_motion is GlassMotion.Grow or GlassMotion.Open)
        {
            MoveSlide(Math.Clamp(progress, 0, 1));
        }

        // The glass's size is laid out now, so the blur beneath it matches this very frame.
        if (PresentationSource.FromVisual(this) is not null)
        {
            UpdateLayout();
            MatchBackdrops();
        }
    }

    // How much each layer of contents shows at a progress of the motion: the bar's, the Working pill's and the conversation's.
    private (double Compact, double Working, double Conversation) LayerOpacities(double progress) => _motion switch
    {
        // Pressed, the bar's words dim to half, and they are gone a third of the way into the pill, as the spinner comes in.
        GlassMotion.Fold => (0.5 * (1 - Ramp(progress, 0, 0.35)), Ramp(progress, 0.3, 0.8), 0),
        GlassMotion.Wait or GlassMotion.Resize => (0, 1, 0),

        // Opening into the result, the pill's contents go at once, as when the panel opens; resting as it, only the result shows.
        GlassMotion.Result => (0, 1 - Ramp(progress, 0, 0.2), 0),
        GlassMotion.Rest => (0, 0, 0),
        GlassMotion.Open when _fromResult => (0, 0, Ramp(progress, 0.2, 0.85)),

        // Opening, the pill's contents go at once and the conversation's come in as the glass reaches the panel.
        GlassMotion.Open => (0, 1 - Ramp(progress, 0, 0.2), Ramp(progress, 0.2, 0.85)),
        _ => (_morphMotion.CompactOpacityAt(progress), 0, _morphMotion.ConversationOpacityAt(progress)),
    };

    private static double Ramp(double value, double from, double to) =>
        value <= from ? 0 : value >= to ? 1 : (value - from) / (to - from);

    private void OnMorphArrived(object? sender, EventArgs e)
    {
        switch (_motion)
        {
            case GlassMotion.Fold:
            case GlassMotion.Resize:
                // The pill rests, and waits for the answer.
                _motion = GlassMotion.Wait;
                _from = _to;
                ApplyMorph();
                return;
            case GlassMotion.Result:
                // The result's pill rests, until it is pressed or put away.
                _motion = GlassMotion.Rest;
                _from = _to;
                ApplyMorph();
                return;
            case GlassMotion.Open:
                // The panel rests as the end of the bar's growth does.
                _motion = GlassMotion.Grow;
                if (_fromResult)
                {
                    _fromResult = false;
                    ResultContent.Content = null;
                    ApplyMorph();
                }

                UpdateChips();
                break;
            case GlassMotion.Wait:
            case GlassMotion.Rest:
                return;
        }

        if (_slide is { } slide)
        {
            _slide = null;
            _placement.PlaceAnchorTopAt(Handle, Layout(AssistantWindowState.FloatingConversation), slide.To);
        }

        if (_state == AssistantWindowState.FloatingConversation)
        {
            Expanded?.Invoke(this, EventArgs.Empty);
        }
    }

    // The answer has come: the Working pill springs open into the panel where it hangs, past it a little and back, as in the reference. A panel
    // that would not fit below rises as it opens.
    private void OpenFromPill()
    {
        if (!IsWaitingForAnswer)
        {
            return;
        }

        StopWorkingLabel();
        _motion = GlassMotion.Open;
        _from = _form;
        _to = _conversationForm;
        _slide = PlanSlide(SurfaceTop);
        UpdateChips();
        Conversation.ScrollToEnd();
        FocusContent();
        _morph.SpringFromStart(_openSpring);
    }

    // The answer has come. One that is a device of the home, switched, and nothing more to read is shown alone, as a pill (the home reference); any
    // other opens the panel.
    private void OpenAnswer()
    {
        if (CompactResult() is { } result)
        {
            OpenResult(result.Device, result.Answer);
        }
        else
        {
            OpenFromPill();
        }
    }

    // The newest answer's device, when the answer is that and at most words after it.
    private (HomeDeviceContent Device, MessageViewModel Answer)? CompactResult()
    {
        var answer = _conversation.Messages.LastOrDefault(message => message.Role == MessageRole.Assistant);
        // What is put away behind the answer's three dots (the steps, a question that was answered) is not part of what the pill would have to show.
        var shown = answer?.Content.Where(part => !part.IsTucked).ToList() ?? [];
        return answer is not null && shown.Count > 0 && shown[0] is HomeDeviceContent device && shown.Skip(1).All(part => part is TextContent)
            ? (device, answer)
            : null;
    }

    // The Working pill springs open into the result's pill where it hangs: as wide as the panel, as tall as the device's row.
    private void OpenResult(HomeDeviceContent device, MessageViewModel answer)
    {
        if (!IsWaitingForAnswer)
        {
            return;
        }

        StopWorkingLabel();
        ForgetResult();
        _result = device;
        _resultAnswer = answer;
        device.OpenRequested += OnResultOpenRequested;
        answer.Content.CollectionChanged += OnResultAnswerChanged;
        ResultContent.Content = device;
        _motion = GlassMotion.Result;
        _from = _form;
        _to = _resultForm;
        _slide = null;
        UpdateChips();
        _morph.SpringFromStart(_openSpring);
    }

    // The result was pressed, anywhere but on its glyph: the panel opens around it, with what was done open under the device.
    private void OnResultOpenRequested(object? sender, EventArgs e) => OpenFromResult();

    // More came into the answer than the pill can show (a question to answer, a list): the panel opens for it.
    private void OnResultAnswerChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsShowingResult && CompactResult() is null)
        {
            OpenFromResult();
        }
    }

    // The result's pill springs open into the panel, as the Working pill does.
    private void OpenFromResult()
    {
        if (!IsShowingResult)
        {
            return;
        }

        ForgetResult(keepShown: true);
        _fromResult = true;
        _motion = GlassMotion.Open;
        _from = _form;
        _to = _conversationForm;
        _slide = PlanSlide(SurfaceTop);
        UpdateChips();
        Conversation.ScrollToEnd();
        FocusContent();
        _morph.SpringFromStart(_openSpring);
    }

    // Stops following the result that showed alone; it stays drawn while the panel opens from it.
    private void ForgetResult(bool keepShown = false)
    {
        if (_result is { } device)
        {
            device.OpenRequested -= OnResultOpenRequested;
            _result = null;
        }

        if (_resultAnswer is { } answer)
        {
            answer.Content.CollectionChanged -= OnResultAnswerChanged;
            _resultAnswer = null;
        }

        if (!keepShown)
        {
            _fromResult = false;
            ResultContent.Content = null;
        }
    }

    // What the Working pill says: what the Assistant is doing, as the Searching chip would say it ("Looking into it"), and "Working" until then.
    private string CurrentWorkingText()
    {
        var activity = _bar.Activity ?? _conversation.Activity;
        return activity is { IsVisible: true, StatusText.Length: > 0 } ? activity.StatusText : ActivityStatus.WorkingText;
    }

    // The pill that holds the words: as wide as they need, with the spinner before them.
    private SurfaceForm WorkingForm(string label)
    {
        var typeface = new Typeface(WorkingLabel.FontFamily, WorkingLabel.FontStyle, WorkingLabel.FontWeight, WorkingLabel.FontStretch);
        var text = new FormattedText(
            label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, WorkingLabel.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return SurfaceForm.Pill(Math.Ceiling(_workingLabelLeft + text.WidthIncludingTrailingWhitespace + _workingLabelRight), _workingHeight);
    }

    // What the Assistant is doing changed while the pill waits: the new words come in, and the pill widens or narrows to them.
    private void UpdateWorkingLabel()
    {
        if (!IsWaitingForAnswer)
        {
            return;
        }

        var label = CurrentWorkingText();
        if (label == WorkingLabel.Text)
        {
            return;
        }

        WorkingLabel.Text = label;
        StartWorkingLabel(LabelChangeDelay);
        var form = WorkingForm(label);
        if (_motion == GlassMotion.Fold)
        {
            // Still folding: the spring carries on to the new size.
            _to = form;
            return;
        }

        _motion = GlassMotion.Resize;
        _from = _form;
        _to = form;
        _morph.EaseFromStart(_resizeTime);
    }

    // The pill's words come in out of a blur, then breathe slowly between full and dim for as long as the pill waits, as the reference's do.
    private void StartWorkingLabel(TimeSpan delay)
    {
        WorkingLabelHost.BeginAnimation(OpacityProperty, null);
        WorkingLabel.BeginAnimation(OpacityProperty, null);
        if (!_animationsEnabled())
        {
            WorkingLabelHost.Opacity = 1;
            WorkingLabelHost.Effect = null;
            WorkingLabel.Opacity = 1;
            return;
        }

        var blur = new BlurEffect { Radius = 6, RenderingBias = RenderingBias.Performance };
        WorkingLabelHost.Effect = blur;
        var unblur = new DoubleAnimation(6, 0, LabelFade) { BeginTime = delay };
        unblur.Completed += (_, _) =>
        {
            if (ReferenceEquals(WorkingLabelHost.Effect, blur))
            {
                WorkingLabelHost.Effect = null;
            }
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
        WorkingLabelHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, LabelFade) { BeginTime = delay });

        var breathe = new DoubleAnimationUsingKeyFrames { BeginTime = delay + LabelFade, RepeatBehavior = RepeatBehavior.Forever };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        breathe.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        breathe.KeyFrames.Add(new EasingDoubleKeyFrame(PulseLow, KeyTime.FromTimeSpan(PulsePeriod / 2), ease));
        breathe.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(PulsePeriod), ease));
        WorkingLabel.BeginAnimation(OpacityProperty, breathe);
    }

    private void StopWorkingLabel()
    {
        WorkingLabel.BeginAnimation(OpacityProperty, null);
        WorkingLabel.Opacity = 1;
    }

    // The slide the panel needs to end up wholly on screen, if it needs one.
    private (ScreenPoint From, ScreenPoint To)? PlanSlide(ScreenPoint? from)
    {
        if (from is not { } start ||
            _placement.FitAnchorTop(Layout(AssistantWindowState.FloatingConversation), start) is not { } end ||
            (Math.Abs(end.X - start.X) <= SamePointTolerance && Math.Abs(end.Y - start.Y) <= SamePointTolerance))
        {
            return null;
        }

        _slidePlacedAt = start;
        return (start, end);
    }

    // Moves the window a share of the way along its slide. It moves as it is, so what is drawn in it is not touched.
    private void MoveSlide(double progress)
    {
        if (_slide is not { } slide)
        {
            return;
        }

        var point = new ScreenPoint(
            (int)Math.Round(slide.From.X + ((slide.To.X - slide.From.X) * progress), MidpointRounding.AwayFromZero),
            (int)Math.Round(slide.From.Y + ((slide.To.Y - slide.From.Y) * progress), MidpointRounding.AwayFromZero));

        // Not again where it already is: the same point placed once more could only move it by a rounding error.
        if (point != _slidePlacedAt)
        {
            _slidePlacedAt = point;
            _placement.PlaceAnchorTopAt(Handle, Layout(AssistantWindowState.Compact), point);
        }
    }

    // Keyboard focus goes to what the state is for: the draft, or the conversation, so Page Up and Down scroll it. (The
    // follow-up composer takes it when an answer ends.) A bar that is opening on what was typed before selects it, so that typing replaces it.
    private void FocusContent(bool opening = false)
    {
        if (_state == AssistantWindowState.Compact)
        {
            if (opening && _bar.Query.Length > 0)
            {
                PromptInput.FocusInputSelectingAll();
            }
            else
            {
                PromptInput.FocusInput();
            }
        }
        else if (_conversation.Messages.Count == 0 && _conversation.CanCompose)
        {
            // A conversation that only has an image or a document attached so far waits for what the user wants to know about it.
            ComposerInput.FocusInput();
        }
        else
        {
            Conversation.Focus();
        }
    }

    // Attaches what was chosen from the plus's menu and hands the keyboard to the composer, to ask about it.
    private void AttachAndCompose(Action attach)
    {
        attach();
        Activate();
        ComposerInput.FocusInputAtEnd();
    }

    // The glass's shadow shows everywhere around the glass out to the shadow gutter, and no farther than the gutter reaches. The panel hangs under
    // the pill, and the pill's shadow must not show through its glass: the panel's place is taken out of the shadow. While the panel arrives it is
    // narrower, shorter and still faint, and the shadow goes from under it as the panel is drawn then, and only as fast as the panel comes. Taken
    // out whole and at once, it showed the shape of the whole panel, lighter than what was around it, before the panel was there.
    private void UpdateShadow(Size glassSize, Geometry outline)
    {
        var panelShown = IsPanelShown;
        var surroundings = new Rect(
            -_gutter.Left, -_gutter.Top, glassSize.Width + _gutter.Left + _gutter.Right,
            glassSize.Height + _gutter.Top + _gutter.Bottom + (panelShown ? PanelExtra : 0));
        Geometry clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(surroundings), outline);
        Brush? mask = null;
        var presence = panelShown ? LauncherLayer.Opacity : 0;
        if (presence > 0)
        {
            // The panel as it is drawn now: scaled about the middle of its top edge.
            var scale = LauncherScale.ScaleX;
            var width = _launcherWidth * scale;
            var panel = PanelShape.CreateGeometry(
                new Rect((glassSize.Width - width) / 2, glassSize.Height + _launcherGap, width, _panelHeight * scale), _launcherCorner * scale);
            if (presence >= 1)
            {
                clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, panel);
            }
            else
            {
                mask = CreateShadowMask(surroundings, panel, 1 - presence);
            }
        }

        ShadowLayer.Clip = clip;
        ShadowLayer.OpacityMask = mask;
    }

    // A mask that leaves the shadow as it is everywhere but under the panel, where this much of it is left.
    private static DrawingBrush CreateShadowMask(Rect surroundings, Geometry panel, double left)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            Brushes.Black, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(surroundings), panel)));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * left), 0, 0, 0)), null, panel));
        var mask = new DrawingBrush(drawing)
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = surroundings,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = surroundings,
        };
        mask.Freeze();
        return mask;
    }

    // Everything around the launcher's panel out to the shadow gutter, except the panel itself and the bar above it,
    // so its shadow shows outside its own glass only and never over the bar's. Coordinates are the panel's own.
    private Geometry CreateLauncherShadowClip(Size bar, Size panel)
    {
        var surroundings = new RectangleGeometry(new Rect(
            -_gutter.Left, -_gutter.Top - bar.Height - _launcherGap, panel.Width + _gutter.Left + _gutter.Right,
            panel.Height + _gutter.Top + bar.Height + _launcherGap + _gutter.Bottom));
        var withoutPanel = new CombinedGeometry(
            GeometryCombineMode.Exclude, surroundings, PanelShape.CreateGeometry(new Rect(panel), _launcherCorner));
        var barOutline = (bar.Height == _compactForm.Height ? _compactForm : BarForm).CreateGeometry(
            new Rect((panel.Width - bar.Width) / 2, -_launcherGap - bar.Height, bar.Width, bar.Height));
        return new CombinedGeometry(GeometryCombineMode.Exclude, withoutPanel, barOutline);
    }

    // Placement moves the window in physical pixels while it is hidden, since Left and Top are converted at the DPI of
    // the monitor the window is leaving. The window has fixed sizes, so it is described by its state's glass, whose
    // size and shadow gutter are known before layout; the glass, not its shadow, is what appears centered.
    private void Place(ScreenPoint? surfaceTop)
    {
        var window = Handle;
        if (surfaceTop is { } point)
        {
            _placement.PlaceAnchorTopAt(window, Layout(_state), point);
        }
        else
        {
            _placement.PlaceOnActiveMonitor(window, Layout(_state));
        }
    }

    // A drag may leave the glass partly off screen; it comes back inside the work area where it was dropped.
    internal void OnDragged()
    {
        if (SurfaceTop is { } point)
        {
            _placement.PlaceAnchorTopAt(Handle, Layout(_state), point);
        }

        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether an element is one of the conversation's messages.</summary>
    internal bool IsMessage(DependencyObject element) => Conversation.IsMessage(element);

    // The window and the glass in it, for a state. Both states' glass is centered and hangs from the same line, so
    // their top centers coincide: growing never moves it, except to keep the panel on screen.
    internal OverlayLayout Layout(AssistantWindowState state)
    {
        var (form, height) = state == AssistantWindowState.Compact
            ? (BarForm, RestHeight(state))
            : (_conversationForm, _expandedHeight);
        return new OverlayLayout(
            _width, height, (_width - form.Width) / 2, _gutter.Top, form.Width, form.Height, VerticalPosition);
    }

    private nint Handle => new WindowInteropHelper(this).EnsureHandle();
}
