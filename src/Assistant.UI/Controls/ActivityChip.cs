using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using Assistant.UI.Animation;

namespace Assistant.UI.Controls;

/// <summary>
/// The Searching chip: a small pill of dark glass holding the Searching indicator and a word saying what the Assistant
/// is busy with, shown for as long as a search, the model or a tool runs (PROJECT_SPEC §4.1). It arrives as the
/// reference's glass orb, widening into the chip as its indicator and word come in, and leaves the same way in reverse.
/// Host it anywhere the state is wanted, in the compact bar or the floating conversation alike; it takes only what it
/// needs to say, and cancelling is a command it offers, not a button it draws.
/// </summary>
/// <remarks>
/// Its status text is read out by screen readers as it changes, without moving focus, and pressing the chip runs
/// <see cref="CancelCommand"/> (as the automation Invoke pattern does). It runs a frame loop only while it appears or
/// leaves; the indicator's own loop runs while it is shown. With Windows animation effects off it appears and leaves at
/// once (PROJECT_SPEC §4.0). Its theme is Themes/Controls/ActivityChip.xaml.
/// </remarks>
[TemplatePart(Name = GlassPart, Type = typeof(ChipGlass))]
[TemplatePart(Name = ContentPart, Type = typeof(FrameworkElement))]
[TemplatePart(Name = ClipPart, Type = typeof(Canvas))]
[TemplatePart(Name = IndicatorPart, Type = typeof(SearchingIndicator))]
public sealed class ActivityChip : Control
{
    private const string GlassPart = "PART_Glass";
    private const string ContentPart = "PART_Content";
    private const string ClipPart = "PART_Clip";
    private const string IndicatorPart = "PART_Indicator";

    /// <summary>Identifies the <see cref="IsActive"/> property.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ActivityChip), new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>Identifies the <see cref="Text"/> property.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ActivityChip), new PropertyMetadata("", OnTextChanged));

    /// <summary>Identifies the <see cref="CancelCommand"/> property.</summary>
    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
        nameof(CancelCommand), typeof(ICommand), typeof(ActivityChip), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="Motion"/> property.</summary>
    public static readonly DependencyProperty MotionProperty = SearchingIndicator.MotionProperty.AddOwner(typeof(ActivityChip));

    // How long it takes to widen from the orb into the chip, and to fold back into the orb.
    private static readonly TimeSpan AppearDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan DisappearDuration = TimeSpan.FromMilliseconds(260);

    // A frame later than this counts as this long, so a stalled frame slows the chip rather than making it jump.
    private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    // Where in its appearance each part comes and goes: the chip is clear by 0.2, the orb's look has given way to the
    // chip's by 0.6, and the indicator and word show from 0.55 and are fully in by 1.
    private const double OpaqueBy = 0.2;
    private const double OrbGoneBy = 0.6;
    private const double ContentFrom = 0.55;

    private readonly IFrameSource _frames;
    private readonly Func<bool> _animationsEnabled;
    private readonly PresenceTween _presence = new(0);
    private ChipGlass? _glass;
    private FrameworkElement? _content;
    private Canvas? _clip;
    private SearchingIndicator? _indicator;
    private TimeSpan? _lastFrame;
    private bool _running;

    static ActivityChip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ActivityChip), new FrameworkPropertyMetadata(typeof(ActivityChip)));
    }

    public ActivityChip() : this(new RenderingFrameSource(), () => SystemParameters.ClientAreaAnimation)
    {
    }

    internal ActivityChip(IFrameSource frames, Func<bool> animationsEnabled)
    {
        _frames = frames;
        _animationsEnabled = animationsEnabled;
        Focusable = false;
        Visibility = Visibility.Collapsed;
        frames.Frame += OnFrame;
    }

    /// <summary>Whether the chip is wanted: it arrives while this is true and leaves when it turns false.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>What the Assistant is busy with, in a word or two such as "Searching". It is read out as it changes.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Cancels the activity: run when the chip is pressed, or invoked through automation.</summary>
    public ICommand? CancelCommand
    {
        get => (ICommand?)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    /// <summary>How the indicator's droplets circulate.</summary>
    public DropletRingMotion Motion
    {
        get => (DropletRingMotion)GetValue(MotionProperty);
        set => SetValue(MotionProperty, value);
    }

    /// <summary>How far the chip has come from the orb (0, hidden) to the whole chip (1), after easing.</summary>
    internal double Presence => _presence.Value;

    /// <summary>Whether its own frame loop is running, which it does only while it appears or leaves.</summary>
    internal bool IsAnimating => _running;

    /// <summary>Whether the chip can be pressed to cancel: it is fully shown and has a command that can run.</summary>
    internal bool CanCancel => IsActive && Presence > 0.9 && CancelCommand?.CanExecute(null) == true;

    /// <inheritdoc/>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _glass = GetTemplateChild(GlassPart) as ChipGlass;
        _content = GetTemplateChild(ContentPart) as FrameworkElement;
        _clip = GetTemplateChild(ClipPart) as Canvas;
        _indicator = GetTemplateChild(IndicatorPart) as SearchingIndicator;
        Apply();
    }

    /// <summary>Runs <see cref="CancelCommand"/> when the chip can be cancelled.</summary>
    /// <returns>Whether the command ran.</returns>
    internal bool Cancel()
    {
        if (!CanCancel)
        {
            return false;
        }

        CancelCommand!.Execute(null);
        return true;
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Cancel())
        {
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new ActivityChipAutomationPeer(this);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size constraint)
    {
        // The chip is as wide as its parts need when whole, and grows to that from the orb, which is as wide as it is tall.
        base.MeasureOverride(new Size(double.PositiveInfinity, constraint.Height));
        var content = _content?.DesiredSize ?? default;
        var height = double.IsInfinity(constraint.Height) ? content.Height : constraint.Height;
        var width = height + ((content.Width - height) * _presence.Value);
        return new Size(Math.Min(width, constraint.Width), height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var size = base.ArrangeOverride(arrangeBounds);

        // The indicator and word keep their places at the chip's left, centered, and are cut off by its outline as it widens.
        if (_content is not null && _clip is not null)
        {
            Canvas.SetTop(_content, (size.Height - _content.DesiredSize.Height) / 2);
            _clip.Clip = PillShape.CreateGeometry(new Rect(size));
        }

        return size;
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ActivityChip)d).Update();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chip = (ActivityChip)d;

        // A screen reader speaks the new status without the focus moving to it.
        if (chip.IsActive && chip.Presence > 0 && UIElementAutomationPeer.FromElement(chip) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    // Heads for shown while the chip is wanted, and for gone otherwise.
    private void Update()
    {
        var target = IsActive ? 1 : 0;
        if (!_animationsEnabled())
        {
            Stop();
            _presence.JumpTo(target);
            Apply();
        }
        else if (_presence.Target != target || (!_presence.IsRunning && _presence.Value != target))
        {
            _presence.Start(target, IsActive ? AppearDuration : DisappearDuration);
            if (_presence.IsRunning)
            {
                _lastFrame = null;
                Start();
            }
            else
            {
                Apply();
            }
        }

        if (IsActive && UIElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void Start()
    {
        if (!_running)
        {
            _running = true;
            _frames.Start();
        }
    }

    private void Stop()
    {
        if (_running)
        {
            _running = false;
            _frames.Stop();
        }
    }

    private void OnFrame(object? sender, TimeSpan time)
    {
        if (!_running)
        {
            return;
        }

        // The first frame anchors the clock.
        var elapsed = _lastFrame is { } last ? time - last : TimeSpan.Zero;
        _lastFrame = time;
        _presence.Advance(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > MaxFrameStep ? MaxFrameStep : elapsed);
        Apply();
        if (!_presence.IsRunning)
        {
            Stop();
        }
    }

    // Draws the chip as far along as it has come: how wide it is, how much of the orb still shows, and how far its
    // indicator and word have come in.
    private void Apply()
    {
        var presence = _presence.Value;
        Visibility = presence > 0 ? Visibility.Visible : Visibility.Collapsed;
        Opacity = Math.Clamp(presence / OpaqueBy, 0, 1);
        IsHitTestVisible = IsActive && presence > 0.9;
        InvalidateMeasure();

        if (_glass is not null)
        {
            _glass.Orb = 1 - SmoothStep(0, OrbGoneBy, presence);
        }

        if (_content is not null)
        {
            _content.Opacity = SmoothStep(ContentFrom, 1, presence);
        }

        if (_indicator is not null)
        {
            _indicator.IsActive = presence > 0;
        }
    }

    private static double SmoothStep(double from, double to, double value)
    {
        var t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - (2 * t));
    }

    private sealed class ActivityChipAutomationPeer(ActivityChip owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(ActivityChip);

        // A status, not a button: it is spoken as it appears and as its words change.
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetNameCore() => owner.Text;

        protected override string GetHelpTextCore() => owner.CanCancel ? "Press Escape to cancel" : "";

        protected override bool IsControlElementCore() => owner.IsActive;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke && owner.CancelCommand is not null ? this : base.GetPattern(patternInterface);

        void IInvokeProvider.Invoke() => owner.Dispatcher.BeginInvoke(() => owner.Cancel());
    }
}
