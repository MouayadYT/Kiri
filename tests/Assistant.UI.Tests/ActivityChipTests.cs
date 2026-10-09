using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.Core.Activity;
using Assistant.Core.Domain;
using Assistant.UI.Animation;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.Windowing;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    // ---- The droplets' motion ------------------------------------------------------------------------------------

    [Fact]
    public void TheRingHasSixDropletsSixtyDegreesApartThatNeverMove()
    {
        var motion = new DropletRingMotion();
        Assert.Equal(6, motion.Count);
        Assert.Equal([-8, 52, 112, 172, 232, 292], Enumerable.Range(0, 6).Select(motion.AngleOf));

        // Only sizes depend on time: a droplet's place is the same at every moment.
        Assert.All(Enumerable.Range(0, 6), i => Assert.Equal(motion.AngleOf(i), motion.AngleOf(i)));
    }

    [Fact]
    public void TheEmphasisPassesFromOneDropletToTheNextAndEverySizeStaysBetweenBaseAndPeak()
    {
        var motion = new DropletRingMotion();
        var largest = new List<int>();
        for (var step = 0; step < 6 * 20; step++)
        {
            var peak = step / 20.0;
            var lengths = Enumerable.Range(0, 6).Select(i => motion.LengthOf(i, peak)).ToArray();
            Assert.All(lengths, length => Assert.InRange(length, motion.BaseLength - 1e-9, motion.PeakLength + 1e-9));

            // One area is largest at a time: the droplets at full size are one, or two neighbors.
            var full = Enumerable.Range(0, 6).Where(i => lengths[i] > motion.PeakLength - 1e-9).ToArray();
            Assert.InRange(full.Length, 1, 2);
            if (full.Length == 2) Assert.True((full[1] - full[0] + 6) % 6 is 1 or 5, "Two droplets at full size are neighbors.");

            // At least one droplet is at the base size: the ring is never all swollen.
            Assert.Contains(lengths, length => length < motion.BaseLength + 0.01);
            largest.Add(Array.IndexOf(lengths, lengths.Max()));
        }

        // The largest droplet goes round in order, clockwise: 0, 1, 2, 3, 4, 5, 0, ...
        var order = largest.Zip(largest.Skip(1)).Where(pair => pair.First != pair.Second).Select(pair => pair.Second).ToArray();
        for (var i = 1; i < order.Length; i++)
        {
            Assert.Equal((order[i - 1] + 1) % 6, order[i]);
        }

        Assert.True(order.Length >= 6, "The emphasis goes all the way round.");
    }

    [Fact]
    public void SizesChangeSmoothlyAndSizeDropsBehindThePeakMoreSteeplyThanItRisesAheadOfIt()
    {
        var motion = new DropletRingMotion();

        // Half a percent of a lap moves no size by more than a small step: nothing blinks.
        for (var i = 0; i < 6; i++)
        {
            var previous = motion.LengthOf(i, 0);
            for (var step = 1; step <= 600; step++)
            {
                var length = motion.LengthOf(i, step / 100.0);
                Assert.True(Math.Abs(length - previous) < 0.06, $"Droplet {i} jumped by {Math.Abs(length - previous):F3} at step {step}.");
                previous = length;
            }
        }

        // The reference's still, with the peak between droplets 4 and 5: they are the largest, the droplet after them
        // (clockwise) is medium and the one before is at the base.
        var still = Enumerable.Range(0, 6).Select(i => motion.LengthOf(i, motion.RestPeak)).ToArray();
        Assert.Equal(motion.PeakLength, still[4], 6);
        Assert.Equal(motion.PeakLength, still[5], 6);
        Assert.InRange(still[0], 4.6, 6);
        Assert.InRange(still[1], 3.5, 4.6);
        Assert.Equal(motion.BaseLength, still[2], 6);
        Assert.InRange(still[3], motion.BaseLength, motion.BaseLength + 0.3);
    }

    [Fact]
    public void OneLapTakesTheSetTimeAndReturnsToWhereItStarted()
    {
        var motion = new DropletRingMotion();
        Assert.Equal(motion.RestPeak, motion.PeakAt(TimeSpan.Zero), 9);
        Assert.Equal(motion.RestPeak, motion.PeakAt(motion.Lap), 9);
        Assert.Equal((motion.RestPeak + 3) % 6, motion.PeakAt(motion.Lap / 2), 9);
        Assert.InRange(motion.PeakAt(TimeSpan.FromMinutes(3.3)), 0, 6);
    }

    [Fact]
    public void TheMotionTokenComesFromTheThemeAndMatchesTheReference() => RunSta(() =>
    {
        var theme = new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) };
        var motion = Assert.IsType<DropletRingMotion>(theme["Motion.SearchingRing"]);
        Assert.Equal(new DropletRingMotion(), motion);
        Assert.Equal(41, (double)theme["Size.ActivityChip"]);
    });

    // ---- The indicator -------------------------------------------------------------------------------------------

    [Fact]
    public void TheIndicatorRunsOnlyWhileItIsActiveAndVisibleAndStopsTheMomentEitherEnds() => RunSta(() =>
    {
        var (window, indicator, frames) = CreateIndicator();
        try
        {
            window.Show();
            Assert.False(frames.Running); // Idle: no frame loop, no cost.

            indicator.IsActive = true;
            Assert.True(frames.Running);
            frames.Tick(T0);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(650));
            Assert.Equal((indicator.Motion.RestPeak + 3) % 6, indicator.Peak, 1); // Half a lap on.

            indicator.IsActive = false;
            Assert.False(frames.Running);
            indicator.IsActive = true;
            window.Hide();
            Assert.False(frames.Running); // Hidden: nothing to draw.
            window.Show();
            Assert.True(frames.Running);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WithoutAnimationEffectsTheIndicatorHoldsTheReferencePose() => RunSta(() =>
    {
        var (window, indicator, frames) = CreateIndicator(animations: false);
        try
        {
            window.Show();
            indicator.IsActive = true;
            Assert.False(frames.Running);
            Assert.Equal(indicator.Motion.RestPeak, indicator.Peak);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheDropletsStayWhereTheyAreWhileTheirSizesChange() => RunSta(() =>
    {
        var (window, indicator, frames) = CreateIndicator();
        try
        {
            window.Show();
            indicator.IsActive = true;
            var motion = indicator.Motion;
            var center = new Point(indicator.ActualWidth / 2, indicator.ActualHeight / 2);
            var stands = Enumerable.Range(0, 6).Select(i =>
            {
                var radians = motion.AngleOf(i) * Math.PI / 180;
                return new Point(center.X + (motion.RingRadius * Math.Sin(radians)), center.Y - (motion.RingRadius * Math.Cos(radians)));
            }).ToArray();
            var between = Enumerable.Range(0, 6).Select(i =>
            {
                var radians = (motion.AngleOf(i) + 30) * Math.PI / 180;
                return new Point(center.X + (motion.RingRadius * Math.Sin(radians)), center.Y - (motion.RingRadius * Math.Cos(radians)));
            }).ToArray();

            // At many poses around a lap, each droplet's center is solid white and the gaps between them are clear of
            // droplets, so nothing rotated and nothing moved: only the sizes differ from pose to pose.
            var seenLengths = new HashSet<double>();
            for (var pose = 0; pose < 12; pose++)
            {
                indicator.HeldPeak = pose / 2.0;
                var pixels = RenderWhite(indicator);
                foreach (var stand in stands)
                {
                    Assert.True(Brightness(pixels, stand) > 0.97, $"No droplet at {stand} in pose {pose}.");
                }

                foreach (var gap in between)
                {
                    Assert.True(Brightness(pixels, gap) < 0.6, $"A droplet has strayed to {gap} in pose {pose}.");
                }

                seenLengths.Add(indicator.Lengths[0]);
            }

            Assert.True(seenLengths.Count > 6, "The sizes did not change from pose to pose.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheDropletsAreSoftSlightlyElongatedEggsAlongTheRingNotRoundDots() => RunSta(() =>
    {
        var (window, indicator, _) = CreateIndicator();
        try
        {
            window.Show();
            indicator.IsActive = true;
            indicator.HeldPeak = 1; // Droplet 1, on the ring at 52 degrees, is at its largest.
            var pixels = RenderWhite(indicator, scale: 8);
            var motion = indicator.Motion;
            var radians = motion.AngleOf(1) * Math.PI / 180;
            var center = new Point((indicator.ActualWidth / 2) + (motion.RingRadius * Math.Sin(radians)),
                (indicator.ActualHeight / 2) - (motion.RingRadius * Math.Cos(radians)));

            // Its solid core is measured along the ring (the tangent) and across it (the radius).
            double Reach(double dx, double dy)
            {
                var length = 0.0;
                while (Brightness(pixels, new Point(center.X + (dx * length), center.Y + (dy * length)), 8) > 0.97 && length < 10)
                {
                    length += 0.05;
                }

                return length;
            }

            var (tx, ty) = (Math.Cos(radians), Math.Sin(radians));
            var along = Reach(tx, ty) + Reach(-tx, -ty);
            var across = Reach(-ty, tx) + Reach(ty, -tx);
            Assert.Equal(motion.PeakLength, along, 0.5);
            Assert.Equal(motion.Elongation, along / across, 0.1);
        }
        finally { window.Close(); }
    });

    // ---- The chip ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheChipWidensFromTheOrbIntoTheChipAndFoldsBackAndIsGoneWhenItIsNotWanted() => RunSta(() => WithTheme(() =>
    {
        var (window, chip, frames) = CreateChip();
        try
        {
            window.Show();
            Assert.Equal(Visibility.Collapsed, chip.Visibility);
            Assert.False(frames.Running);

            chip.IsActive = true;
            Assert.True(frames.Running);
            frames.Tick(T0);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(20));
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, chip.Visibility);
            Assert.Equal(41, chip.ActualHeight);
            var early = chip.ActualWidth;
            Assert.InRange(early, 41, 60); // Still the orb, as wide as it is tall.
            Assert.True(Part<ChipGlass>(chip, "PART_Glass").Orb > 0.8);
            Assert.Equal(0, Part<StackPanel>(chip, "PART_Content").Opacity);

            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(180));
            window.UpdateLayout();
            Assert.InRange(chip.ActualWidth, early + 20, 122);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(420));
            window.UpdateLayout();
            Assert.False(frames.Running);
            Assert.Equal(1, chip.Presence);
            Assert.Equal(1, chip.Opacity);
            Assert.InRange(chip.ActualWidth, 118, 128); // The reference chip is 124 wide.
            Assert.Equal(0, Part<ChipGlass>(chip, "PART_Glass").Orb);
            Assert.Equal(1, Part<StackPanel>(chip, "PART_Content").Opacity);
            Assert.True(Part<SearchingIndicator>(chip, "PART_Indicator").IsActive);

            // Not wanted any more: it folds back and goes, quicker than it came, and the indicator stops.
            chip.IsActive = false;
            Assert.True(frames.Running);
            frames.Tick(T0 + Second);
            frames.RunUntil(T0 + Second + TimeSpan.FromMilliseconds(130));
            Assert.InRange(chip.Presence, 0.05, 0.9);
            frames.RunUntil(T0 + Second + TimeSpan.FromMilliseconds(300));
            Assert.Equal(0, chip.Presence);
            Assert.Equal(Visibility.Collapsed, chip.Visibility);
            Assert.False(frames.Running);
            Assert.False(Part<SearchingIndicator>(chip, "PART_Indicator").IsActive);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheChipTurnsBackFromWhereItIsWithoutJumping() => RunSta(() => WithTheme(() =>
    {
        var (window, chip, frames) = CreateChip();
        try
        {
            window.Show();
            chip.IsActive = true;
            frames.Tick(T0);
            frames.RunUntil(T0 + TimeSpan.FromMilliseconds(100));
            var before = chip.Presence;
            chip.IsActive = false;
            frames.Tick(T0 + Second);
            Assert.InRange(chip.Presence, before - 0.05, before + 0.001);
            chip.IsActive = true;
            frames.Tick(T0 + Second + TimeSpan.FromMilliseconds(10));
            Assert.InRange(chip.Presence, before - 0.1, 1);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void WithoutAnimationEffectsTheChipComesAndGoesAtOnce() => RunSta(() => WithTheme(() =>
    {
        var (window, chip, frames) = CreateChip(animations: false);
        try
        {
            window.Show();
            chip.IsActive = true;
            Assert.Equal(1, chip.Presence);
            Assert.Equal(Visibility.Visible, chip.Visibility);
            Assert.False(frames.Running);
            chip.IsActive = false;
            Assert.Equal(Visibility.Collapsed, chip.Visibility);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheChipSaysItsStatusToScreenReadersAndRunsCancelWhenPressedOrInvoked() => RunSta(() => WithTheme(() =>
    {
        var (window, chip, frames) = CreateChip(animations: false);
        var cancel = new RecordingCommand();
        try
        {
            window.Show();
            chip.Text = "Searching";
            chip.CancelCommand = cancel;
            chip.IsActive = true;
            window.UpdateLayout();

            var peer = UIElementAutomationPeer.CreatePeerForElement(chip);
            Assert.Equal("Searching", peer.GetName());
            Assert.Equal(System.Windows.Automation.Peers.AutomationControlType.Text, peer.GetAutomationControlType());
            Assert.Equal(AutomationLiveSetting.Polite, peer.GetLiveSetting());
            Assert.Contains("Escape", peer.GetHelpText(), StringComparison.Ordinal);
            chip.Text = "Searching files";
            Assert.Equal("Searching files", peer.GetName());

            // Pressing it cancels; so does the automation Invoke pattern.
            chip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = chip,
            });
            Assert.Equal(1, cancel.Count);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
            Pump();
            Assert.Equal(2, cancel.Count);

            // A chip that is not up cannot be cancelled, and is not in the automation tree.
            chip.IsActive = false;
            Assert.False(chip.CanCancel);
            Assert.False(peer.IsControlElement());
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void ChipFixtureRenders() => RunSta(() => WithTheme(() =>
    {
        // The chip at rest and part way from its orb, on white like the reference, at 2x, with the ring held in the
        // reference's pose. It only saves the pictures for measuring against the reference (ASSISTANT_UI_RENDER_DIR).
        foreach (var (name, milliseconds) in new[] { ("chip", 1000), ("chip-orb", 25), ("chip-mid", 100) })
        {
            var (window, chip, frames) = CreateChip(width: 196, height: 91);
            try
            {
                window.Show();
                chip.IsActive = true;
                frames.Tick(T0);
                frames.RunUntil(T0 + TimeSpan.FromMilliseconds(milliseconds));
                window.UpdateLayout();
                foreach (var indicator in Descendants<SearchingIndicator>(chip)) indicator.HeldPeak = chip.Motion.RestPeak;
                Pump();
                RenderFixture((FrameworkElement)window.Content, name + "-2x.png", 2);
            }
            finally { window.Close(); }
        }
    }));

    // ---- What the chip shows -------------------------------------------------------------------------------------

    [Fact]
    public void AnOperationShowsTheChipOnlyAfterTheDelayAndItGoesTheMomentItEnds() => RunSta(() =>
    {
        var (tracker, clock, model) = CreateActivity();
        var shown = new List<bool>();
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ActivityViewModel.IsVisible)) shown.Add(model.IsVisible); };

        // One that ends before the delay shows nothing at all.
        tracker.Begin(ActivityKind.WindowsSearch).Dispose();
        clock.Advance(Second);
        Pump();
        Assert.Empty(shown);
        Assert.False(model.IsVisible);

        var scope = tracker.Begin(ActivityKind.WebSearch);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Pump();
        Assert.False(model.IsVisible);
        clock.Advance(TimeSpan.FromMilliseconds(60));
        Pump();
        Assert.True(model.IsVisible);
        Assert.Equal("Looking into it", model.StatusText); // A web lookup's own words, as the reference says them.

        scope.Update("Searching the web");
        Assert.Equal("Searching the web", model.StatusText);

        scope.Dispose();
        Assert.False(model.IsVisible);
        Assert.Equal("Searching the web", model.StatusText); // The words stay while the chip folds away.
        Assert.Equal([true, false], shown);
    });

    [Fact]
    public void CancellingHidesTheChipAtOnceAndWorksBeforeItHasShown() => RunSta(() =>
    {
        var (tracker, clock, model) = CreateActivity();
        var early = tracker.Begin(ActivityKind.Model);
        Assert.False(model.IsVisible);
        Assert.True(model.Cancel());
        Assert.True(early.CancellationToken.IsCancellationRequested);
        early.Dispose();

        var scope = tracker.Begin(ActivityKind.Tool);
        clock.Advance(Second);
        Pump();
        Assert.True(model.IsVisible);
        Assert.True(model.CancelCommand.CanExecute(null));
        model.CancelCommand.Execute(null);
        Assert.True(scope.CancellationToken.IsCancellationRequested);
        Assert.False(model.IsVisible);
        Assert.False(model.CancelCommand.CanExecute(null));
        scope.Dispose();
        Assert.False(model.Cancel());
    });

    [Fact]
    public void ANestedOperationChangesTheWordsWithoutTheChipLeavingAndComingBack() => RunSta(() =>
    {
        var (tracker, clock, model) = CreateActivity();
        var hides = 0;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ActivityViewModel.IsVisible) && !model.IsVisible) hides++; };
        var tool = tracker.Begin(ActivityKind.Tool);
        clock.Advance(Second);
        Pump();
        Assert.Equal("Working", model.StatusText);
        var search = tracker.Begin(ActivityKind.WindowsSearch);
        Assert.Equal("Searching", model.StatusText);
        search.Dispose();
        Assert.Equal("Working", model.StatusText);
        tool.Dispose();
        Assert.Equal(1, hides);
    });

    [Fact]
    public async Task AnOperationOnAnotherThreadReachesTheChipThroughTheDispatcher()
    {
        var tracker = new ActivityTracker();
        var ui = UiDispatcher.Value;
        var model = ui.Invoke(() => new ActivityViewModel(tracker, showDelay: TimeSpan.Zero));
        using var scope = await Task.Run(() => tracker.Begin(ActivityKind.WebSearch));
        for (var i = 0; i < 100 && !ui.Invoke(() => model.IsVisible); i++) await Task.Delay(20);
        Assert.True(ui.Invoke(() => model.IsVisible));
        scope.Dispose();
        for (var i = 0; i < 100 && ui.Invoke(() => model.IsVisible); i++) await Task.Delay(20);
        Assert.False(ui.Invoke(() => model.IsVisible));
        ui.Invoke(model.Dispose);
    }

    // ---- On the Assistant's surfaces -----------------------------------------------------------------------------

    [Fact]
    public void TheChipHangsUnderTheBarWhileSomethingRunsAndMakesRoomForItself() => RunSta(() => WithTheme(() =>
    {
        var (tracker, clock, model) = CreateActivity();
        var frames = new List<FakeFrames>();
        var assistant = CreateAssistant(frames: frames, activity: model);
        var (window, bar, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var host = GridNamed(window, "SurfaceHost");
            var chip = Named<ActivityChip>(window, "BarChip");
            Assert.Equal(new Size(610, 28 + 91 + 64), new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(Visibility.Collapsed, chip.Visibility);
            Assert.False(window.IsChipShown);

            // The chip is up after the delay, 10.5 under the pill and centered on it, and the window is taller by it.
            var scope = tracker.Begin(ActivityKind.WebSearch);
            clock.Advance(Second);
            Pump();
            Assert.True(chip.IsActive);
            Assert.Equal("Looking into it", chip.Text);
            WaitUntil(() => chip.Presence == 1, "The chip did not finish arriving.");
            Assert.True(window.IsChipShown);
            window.UpdateLayout();
            Assert.Equal(28 + 91 + 10.5 + 41 + 64, window.ActualHeight, 1);
            Assert.Equal(new Rect(45, 28, 520, 91), GlassRegion(window)); // The pill has not moved.
            var bounds = BoundsIn(host, chip);
            Assert.Equal(28 + 91 + 10.5, bounds.Top, 1);
            Assert.Equal(305, bounds.Left + (bounds.Width / 2), 1);
            Assert.Equal(41, bounds.Height, 1);
            RenderFixture(host, "chip-under-bar-2x.png", 2, new Rect(0, 0, 610, 28 + 91 + 10.5 + 41 + 40));

            // Esc cancels it before anything else: the text is kept and the bar stays.
            bar.Query = "keep this";
            Assert.False(bar.HandleEscape());
            Assert.True(scope.CancellationToken.IsCancellationRequested);
            Assert.Equal("keep this", bar.Query);
            Assert.False(chip.IsActive);
            scope.Dispose();
            WaitUntil(() => !window.IsChipShown, "The chip did not leave.");
            window.UpdateLayout();
            Assert.Equal(28 + 91 + 64, window.ActualHeight, 1);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheChipHangsBelowThePanelWhenOneShowsUnderTheBar() => RunSta(() => WithTheme(() =>
    {
        var (tracker, clock, model) = CreateActivity();
        var frames = new List<FakeFrames>();
        var (launcher, _) = CreateLauncher();
        var assistant = CreateAssistant(frames: frames, launcher: launcher, activity: model);
        var (window, _, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            using var scope = tracker.Begin(ActivityKind.WindowsSearch);
            clock.Advance(Second);
            Pump();
            var chip = Named<ActivityChip>(window, "BarChip");
            WaitUntil(() => chip.Presence == 1, "The chip did not finish arriving.");
            window.UpdateLayout();
            Assert.Equal(28 + 91 + 10.5 + 215.5 + 10.5 + 41 + 64, window.ActualHeight, 1);
            Assert.Equal(28 + 91 + 10.5 + 215.5 + 10.5, BoundsIn(GridNamed(window, "SurfaceHost"), chip).Top, 1);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void AskingASlowQuestionShowsTheWorkingPillAndAFollowUpTheChipInThePanelAndCancellingStopsTheWait() => RunSta(() => WithTheme(() =>
    {
        var (tracker, clock, model) = CreateActivity();
        var answers = new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System, tracker, TimeSpan.FromMinutes(5));
        var frames = new List<FakeFrames>();
        var assistant = CreateAssistant(answers: answers, frames: frames, activity: model);
        var (window, bar, conversation) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var barChip = Named<ActivityChip>(window, "BarChip");
            var transcript = Named<ConversationView>(window, "Conversation");

            // Asked from the bar, a slow question folds the bar into the Working pill, which says what the chip would have: neither chip shows.
            bar.Query = "demo searching";
            bar.AskCommand.Execute(null);
            Advance(frames[1], 600);
            Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
            Assert.True(window.IsWaitingForAnswer);
            Assert.Single(conversation.Messages); // The question is there; the answer is still being looked for.

            clock.Advance(Second);
            Pump();
            Assert.True(model.IsVisible);
            Assert.False(barChip.IsActive);
            Assert.False(transcript.IsWorking);
            Assert.Equal("Looking into it", window.WorkingText);

            // Esc on the conversation cancels the search; its answer says it stopped, and the pill opens into the panel with it.
            Assert.False(conversation.HandleEscape());
            WaitUntil(() => { Pump(); return conversation.Messages.Count == 2; }, "The stopped search was not answered.");
            Assert.Equal("Stopped. Nothing was found.", conversation.Messages[1].Text);
            Assert.False(window.IsWaitingForAnswer);
            Advance(frames[1], 1000);
            Assert.False(window.IsExpanding);
            Assert.Equal(new Rect(96, 28, 418, 598), GlassRegion(window));

            // A follow-up asked in the panel has no pill to wait in: the indicator turns in the conversation, under the question, where the answer will be,
            // and the composer stays at the bottom of the panel with nothing over it.
            Assert.True(conversation.Ask("demo searching"));
            clock.Advance(Second);
            Pump();
            Assert.True(model.IsVisible);
            Assert.False(barChip.IsActive);
            Assert.True(transcript.IsWorking);
            Assert.Equal("Looking into it", transcript.WorkingText);
            window.UpdateLayout();
            var dots = Assert.Single(Descendants<SearchingIndicator>(transcript));
            Assert.True(dots.IsVisible);
            Assert.True(dots.IsActive);
            Assert.Equal("Looking into it", System.Windows.Automation.AutomationProperties.GetName(dots));

            // Under the last message, at the left where an answer begins, and above the composer, which shows and is clear of it.
            var host = GridNamed(window, "SurfaceHost");
            var glass = GlassRegion(window);
            var bounds = BoundsIn(host, dots);
            var composer = BoundsIn(host, GridNamed(window, "Composer"));
            Assert.Equal(Visibility.Visible, GridNamed(window, "Composer").Visibility);
            Assert.InRange(bounds.Left, glass.Left + 20, glass.Left + 40);
            Assert.True(bounds.Bottom <= composer.Top, "The indicator stands over the composer.");
            Assert.True(composer.Bottom <= glass.Bottom && composer.Top > glass.Top + (glass.Height / 2));
            RenderGlass(window, "working-in-conversation-2x.png", 2);

            // Esc cancels the search before it closes the panel; the panel stays, and says the search stopped.
            Assert.False(conversation.HandleEscape());
            WaitUntil(() => { Pump(); return conversation.Messages.Count == 4; }, "The stopped search was not answered.");
            Assert.Equal("Stopped. Nothing was found.", conversation.Messages[3].Text);
            Assert.False(model.IsVisible);
            Assert.True(conversation.HandleEscape()); // Nothing is running now: Esc closes the panel.
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void ANewConversationEndsTheWaitForTheLastAnswer() => RunSta(() =>
    {
        var tracker = new ActivityTracker();
        var answers = new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System, tracker, TimeSpan.FromMinutes(5));
        var conversation = CreateConversationModel(answers: answers);
        conversation.StartNew("demo working");
        Assert.Equal(ActivityKind.Tool, tracker.Current!.Kind);
        Assert.Single(conversation.Messages);

        conversation.StartNew("what is 9+10");
        Assert.Equal(2, conversation.Messages.Count); // The new question and its answer, at once.
        WaitUntil(() => { Pump(); return tracker.Current is null; }, "The old wait went on.");
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal(MessageRole.User, conversation.Messages[0].Role);
    });

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static T Part<T>(ActivityChip chip, string name) where T : class => Assert.IsType<T>(chip.Template.FindName(name, chip));

    private static (Window Window, SearchingIndicator Indicator, FakeFrames Frames) CreateIndicator(bool animations = true)
    {
        var frames = new FakeFrames();
        var indicator = new SearchingIndicator(frames, () => animations) { Motion = new DropletRingMotion(), Fill = Brushes.White };
        var window = new Window
        {
            Left = -10000, Top = -10000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight, Content = indicator,
            Background = Brushes.Black,
        };
        return (window, indicator, frames);
    }

    private static (Window Window, ActivityChip Chip, FakeFrames Frames) CreateChip(
        bool animations = true, double width = double.NaN, double height = double.NaN)
    {
        var frames = new FakeFrames();
        var chip = new ActivityChip(frames, () => animations) { Text = "Searching", HorizontalAlignment = HorizontalAlignment.Center };
        var host = new Grid { Background = Brushes.White, Width = width, Height = height };
        host.Children.Add(chip);
        var window = new Window
        {
            Left = -10000, Top = -10000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight, Content = host,
        };
        return (window, chip, frames);
    }

    private static (ActivityTracker Tracker, ManualTime Clock, ActivityViewModel Model) CreateActivity()
    {
        var tracker = new ActivityTracker();
        var clock = new ManualTime();
        return (tracker, clock, new ActivityViewModel(tracker, clock));
    }

    // The brightness (0 to 1) of an indicator drawn white on black, at a point in DIPs.
    private static double Brightness(byte[] pixels, Point point, double scale = 4)
    {
        var side = (int)Math.Round(Math.Sqrt(pixels.Length / 4.0));
        var (x, y) = ((int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale));
        return pixels[((y * side) + x) * 4 + 1] / 255.0;
    }

    private static byte[] RenderWhite(SearchingIndicator indicator, double scale = 4)
    {
        indicator.UpdateLayout();
        var bitmap = Render(indicator, scale);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    // A clock that runs only when told to, for a view model's timers.
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _now;

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToArray())
            {
                timer.RunIfDue(_now);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _now + dueTime, _timers);
            _timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due, List<ManualTimer> owner) : ITimer
        {
            private bool _done;

            public void RunIfDue(TimeSpan now)
            {
                if (!_done && due <= now)
                {
                    _done = true;
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
                _done = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
