using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Assistant.Core.Clock;
using Assistant.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Clock;

/// <summary>
/// The app's <see cref="IClockApp"/>: works the Windows Clock app (Microsoft.WindowsAlarms) through UI Automation, the way a person does. The app is opened
/// with its own address (<c>ms-clock:</c>), its tab is chosen, and its own dialog is filled in and saved: the pickers of an alarm's time and a timer's
/// length take a value directly, so nothing is typed with the keyboard and the pointer is never moved. The controls are found by the ids the app gives
/// them (<c>AddAlarmButton</c>, <c>HourPicker</c>, <c>PrimaryButton</c>…), which do not change with the language; what does (AM and PM, the names of the
/// days) is matched against the PC's own words for them. A control that is not where it is expected ends the action with a failure that says so, and a
/// dialog that was opened is cancelled, so a version of the app that has moved things never leaves half an alarm behind.
/// </summary>
/// <remarks>
/// Windows offers no other way in: the Clock app's own service (<c>ms-alarmsService</c>) answers only Microsoft's apps, and alarms and timers created
/// through its address are accepted only from them too. Only what was asked for is logged, never an alarm's name.
/// </remarks>
public sealed partial class WindowsClockApp : IClockApp
{
    private const string PackageFamilyName = "Microsoft.WindowsAlarms_8wekyb3d8bbwe";
    private const string ProcessName = "Time";

    private static readonly TimeSpan OpenTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StepTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    // How long an app that is running is given to show a window before it is taken to be in its keep-on-top view.
    private static readonly TimeSpan KeepOnTopLook = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    private readonly bool _rehearse;

    // The Clock app's frame window as it was last seen, and what it is called (in the language of the PC).
    private nint _frame;
    private string _frameTitle = "Clock";

    /// <summary>Creates the driver.</summary>
    /// <param name="logger">Where what was done is noted.</param>
    /// <param name="rehearse">
    /// Goes through every step and cancels before anything is saved or started: a check that the Clock app's controls are where they are expected
    /// (RELEASE_CHECKLIST.md), which leaves the user's alarms and timers as they were.
    /// </param>
    public WindowsClockApp(ILogger<WindowsClockApp>? logger = null, bool rehearse = false)
    {
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _rehearse = rehearse;
    }

    /// <inheritdoc/>
    public bool IsInstalled
    {
        get
        {
            // A package that is installed for the user has its folder of data, whether it was ever started or not.
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return local.Length > 0 && Directory.Exists(Path.Combine(local, "Packages", PackageFamilyName));
        }
    }

    /// <inheritdoc/>
    public Task<ClockResult> SetAlarmAsync(ClockAlarm alarm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return RunAsync("alarm", (window, token) => SetAlarm(window, alarm, token), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ClockResult> StartTimerAsync(TimeSpan duration, string? name = null, CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration >= TimeSpan.FromHours(100))
        {
            return Task.FromResult(ClockResult.Failed("A timer runs for at least a second and less than a hundred hours."));
        }

        return RunAsync("timer", (window, token) => StartTimer(window, duration, name, token), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ClockResult> ControlStopwatchAsync(StopwatchAction action, CancellationToken cancellationToken = default) =>
        RunAsync("stopwatch", (window, token) => ControlStopwatch(window, action, token), cancellationToken);

    /// <inheritdoc/>
    public Task<ClockResult> StartFocusSessionAsync(int minutes, bool skipBreaks = false, CancellationToken cancellationToken = default) =>
        RunAsync("focus", (window, token) => StartFocus(window, minutes, skipBreaks, token), cancellationToken);

    // One action at a time, off the caller's thread: the app is opened, and what goes wrong on the way is a failure in words.
    private async Task<ClockResult> RunAsync(string what, Func<AutomationElement, CancellationToken, ClockResult> work, CancellationToken cancellationToken)
    {
        if (!IsInstalled)
        {
            return ClockResult.Failed("The Windows Clock app is not installed on this PC, so there is nothing to set it in.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    try
                    {
                        if (Open(cancellationToken) is not { } window)
                        {
                            LogFailed(_logger, what, "open");
                            return ClockResult.Failed("The Clock app did not open, so nothing was set.");
                        }

                        // The window is put on the display the user chose for it before anything is done in it, so that its dialog comes up there, and again
                        // afterwards, when a timer has shrunk it to its small view on top.
                        PlaceFrame();
                        var result = work(window, cancellationToken);
                        PlaceFrame();
                        LogDone(_logger, what, result.Done);
                        return result;
                    }
                    catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException
                        or TimeoutException or ArgumentException or Win32Exception or ClockControlException)
                    {
                        // A control that was not where it is expected says which, in the driver's own words; anything else only its type.
                        LogFailed(_logger, what, exception is ClockControlException ? exception.Message : exception.GetType().Name);
                        return ClockResult.Failed(
                            "The Clock app did not respond the way it was expected to, so this could not be finished. Look in the Clock app to see what is there.");
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- The four things --------------------------------------------------------------------------------------------------------------

    private ClockResult SetAlarm(AutomationElement window, ClockAlarm alarm, CancellationToken token)
    {
        GoTo(window, "AlarmButton", "AddAlarmButton", token);
        Press(Require(window, "AddAlarmButton"));
        var dialog = WaitFor(() => Find(window, "EditFlyout"), StepTime, token) ?? throw new ClockControlException("the alarm dialog did not open");

        // The dialog is up a moment before what is in it, and longer than that in a Clock app that has only just opened: an alarm once failed there.
        var hour = WaitFor(() => Find(dialog, "HourPicker"), StepTime, token) ?? throw new ClockControlException("the alarm dialog has no hour picker");
        var minute = WaitFor(() => Find(dialog, "MinutePicker"), StepTime, token) ?? throw new ClockControlException("the alarm dialog has no minute picker");
        Thread.Sleep(150);
        var period = Find(dialog, "TwelveHourPicker");

        // A clock that shows AM and PM takes the hour from 1 to 12 and the half of the day apart; one that does not takes it from 0 to 23.
        var shownHour = period is null ? alarm.Time.Hour : alarm.Time.Hour % 12 == 0 ? 12 : alarm.Time.Hour % 12;
        SetNumber(hour, shownHour);
        SetNumber(minute, alarm.Time.Minute);
        if (period is not null && !SetPeriod(period, afternoon: alarm.Time.Hour >= 12))
        {
            Cancel(dialog);
            return ClockResult.Failed("The Clock app's choice of AM or PM could not be set, so no alarm was made.");
        }

        if (!string.IsNullOrWhiteSpace(alarm.Name) && FindEdit(dialog) is { } name)
        {
            SetText(name, alarm.Name.Trim());
        }

        if (!SetRepeat(dialog, alarm.RepeatOn))
        {
            Cancel(dialog);
            return ClockResult.Failed("The Clock app's days could not be chosen, so no alarm was made.");
        }

        var time = alarm.Time.ToString("t", CultureInfo.CurrentCulture);
        var days = alarm.RepeatOn.Count == 0 ? string.Empty : " every " + string.Join(", ", alarm.RepeatOn.Distinct().OrderBy(day => day).Select(DayName));
        if (_rehearse)
        {
            Cancel(dialog);
            return ClockResult.Ok($"Rehearsed an alarm for {time}{days}; nothing was saved.");
        }

        Press(Require(dialog, "PrimaryButton"));
        if (!WaitUntil(() => Find(window, "EditFlyout") is null, StepTime, token))
        {
            Cancel(dialog);
            return ClockResult.Failed("The Clock app did not take the alarm, so none was made.");
        }

        return ClockResult.Ok($"An alarm is set for {time}{days} in the Clock app. It rings while this PC is awake.");
    }

    private ClockResult StartTimer(AutomationElement window, TimeSpan duration, string? name, CancellationToken token)
    {
        GoTo(window, "TimerButton", "AddTimerButton", token);
        Press(Require(window, "AddTimerButton"));
        var dialog = WaitFor(() => Find(window, "EditFlyout"), StepTime, token) ?? throw new ClockControlException("the timer dialog did not open");

        // Hours, minutes and seconds, in that order whatever they are called. The dialog is up a moment before what is in it.
        var isPicker = new PropertyCondition(AutomationElement.ClassNameProperty, "LoopingPicker");
        WaitUntil(() => dialog.FindAll(TreeScope.Descendants, isPicker).Count >= 3, StepTime, token);
        var pickers = dialog.FindAll(TreeScope.Descendants, isPicker);
        if (pickers.Count < 3)
        {
            Cancel(dialog);
            throw new ClockControlException("the timer dialog has no three pickers");
        }

        SetNumber(pickers[0], (int)duration.TotalHours);
        SetNumber(pickers[1], duration.Minutes);
        SetNumber(pickers[2], duration.Seconds);

        var nameBox = FindEdit(dialog);
        if (!string.IsNullOrWhiteSpace(name) && nameBox is not null)
        {
            SetText(nameBox, name.Trim());
        }

        // The name it has now is how it is found in the list once it is saved, to start it.
        var label = nameBox is null ? null : TextOf(nameBox);
        var length = Describe(duration);
        if (_rehearse)
        {
            Cancel(dialog);
            return ClockResult.Ok($"Rehearsed a timer of {length}; nothing was saved.");
        }

        Press(Require(dialog, "PrimaryButton"));
        if (!WaitUntil(() => Find(window, "EditFlyout") is null, StepTime, token))
        {
            Cancel(dialog);
            return ClockResult.Failed("The Clock app did not take the timer, so none was made.");
        }

        var card = WaitFor(() => TimerCardOf(window, label), StepTime, token);
        if (!string.IsNullOrEmpty(label))
        {
            _added.Remove(label);
            _added.Add(label);
        }

        if (card is null || Find(card, "TimerPlayPauseButton") is not { } start)
        {
            return ClockResult.Failed($"A timer of {length} was added to the Clock app, but it could not be started from here. Press Start on it in the Clock app.");
        }

        Press(start);

        // Every timer the Assistant starts is kept on top, as the user asked: the Clock app's own "Keep on top" shrinks its window to the timer alone, small
        // and above the other windows, so the countdown is in sight without the whole app in the way. Where that cannot be done the timer runs in the
        // ordinary window, which is said.
        // (The card is found again: the app builds a card anew when its timer starts.)
        return KeepOnTop(TimerCardOf(window, label) ?? card, token)
            ? ClockResult.Ok($"A timer of {length} is running in the Clock app, kept small on top of your other windows.")
            : ClockResult.Ok($"A timer of {length} is running in the Clock app. It could not be kept on top from here; press Keep on top on it for that.");
    }

    // Presses the timer's own "Keep on top", and says whether the Clock app then shows its small view.
    //
    // The button is pressed with the keyboard, as a person presses it: when it is invoked through UI Automation the app gives up its window and shows no
    // small one, and has to be started again (seen with Clock 11.2607). So the Clock app is brought to the front, the button is given the keyboard, and
    // Space is sent only while both are so, which keeps the key from ever landing in another application.
    private bool KeepOnTop(AutomationElement? card, CancellationToken token)
    {
        if (card is null)
        {
            return false;
        }

        try
        {
            // The button is the card's a moment after the timer starts.
            var keep = WaitFor(() => Find(card, "AotButton"), SettleTime, token);
            var frame = _frame;
            if (keep is null || !keep.Current.IsEnabled || frame == 0 || !User32.IsWindow(frame))
            {
                return false;
            }

            User32.SetForegroundWindow(frame);
            keep.SetFocus();
            if (!WaitUntil(() => User32.GetForegroundWindow() == frame && keep.Current.HasKeyboardFocus, SettleTime, token))
            {
                return false;
            }

            var before = AreaOf(frame);
            if (User32.GetForegroundWindow() != frame || !ClockKeys.TapSpace())
            {
                return false;
            }

            // The small view is the same window, a good deal smaller, still called what the app is called. (A window that is there with no name is the
            // app having given its window up, which the next action puts right by opening it again.)
            return WaitUntil(
                () => User32.IsWindow(frame) && User32.IsWindowVisible(frame) && AreaOf(frame) is > 0 and var now && now < before * 0.6 && ClockKeys.HasTitle(frame),
                StepTime, token);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException or ClockControlException)
        {
            return false;
        }
    }

    private static long AreaOf(nint window) =>
        User32.GetWindowRect(window, out var rect) ? Math.Max(0L, rect.Right - rect.Left) * Math.Max(0L, rect.Bottom - rect.Top) : 0;

    private ClockResult ControlStopwatch(AutomationElement window, StopwatchAction action, CancellationToken token)
    {
        GoTo(window, "StopwatchButton", "StopwatchPlayPauseButton", token);
        var play = Require(window, "StopwatchPlayPauseButton");

        // Laps can only be taken while it runs, whatever language the buttons are in.
        var running = Find(window, "StopWatchLapButton")?.Current.IsEnabled == true;
        switch (action)
        {
            case StopwatchAction.Start:
                if (running)
                {
                    return ClockResult.Ok("The stopwatch is already running in the Clock app.");
                }

                if (!_rehearse)
                {
                    Press(play);
                }

                return ClockResult.Ok(_rehearse ? "Rehearsed starting the stopwatch; nothing was pressed." : "The stopwatch is running in the Clock app.");

            case StopwatchAction.Pause:
                if (!running)
                {
                    return ClockResult.Ok("The stopwatch is not running, so there was nothing to pause.");
                }

                if (!_rehearse)
                {
                    Press(play);
                }

                return ClockResult.Ok(_rehearse ? "Rehearsed pausing the stopwatch; nothing was pressed." : "The stopwatch is paused in the Clock app.");

            default:
                var reset = Require(window, "StopWatchResetButton");
                if (!reset.Current.IsEnabled)
                {
                    return ClockResult.Ok("The stopwatch is at zero already.");
                }

                if (!_rehearse)
                {
                    Press(reset);
                }

                return ClockResult.Ok(_rehearse ? "Rehearsed resetting the stopwatch; nothing was pressed." : "The stopwatch is back at zero in the Clock app.");
        }
    }

    private ClockResult StartFocus(AutomationElement window, int minutes, bool skipBreaks, CancellationToken token)
    {
        var tab = SelectTab(window, "FocusButton", token);
        var start = WaitFor(() => Find(window, "StartButton"), StepTime, token);
        if (start is null)
        {
            // The tab is the focus tab, and there is nothing to start a session with: one is running.
            return IsSelected(tab)
                ? ClockResult.Failed("A focus session is already running in the Clock app, so another was not started.")
                : throw new ClockControlException();
        }

        var length = minutes;
        if (Find(window, "FullNumberBox") is { } box && box.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern) && pattern is RangeValuePattern range)
        {
            // Held to what the app accepts, so that what is said is what was started.
            length = (int)Math.Clamp(minutes, range.Current.Minimum, range.Current.Maximum);
            range.SetValue(length);
        }
        else if (Find(window, "InputBox") is { } input)
        {
            SetText(input, minutes.ToString(CultureInfo.InvariantCulture));
        }

        // Short sessions have no breaks to skip, and the box is then not to be pressed.
        if (Find(window, "HasBreaksCheckbox") is { Current.IsEnabled: true } breaks && breaks.TryGetCurrentPattern(TogglePattern.Pattern, out var toggled)
            && toggled is TogglePattern toggle && (toggle.Current.ToggleState == ToggleState.On) != skipBreaks)
        {
            toggle.Toggle();
        }

        if (_rehearse)
        {
            return ClockResult.Ok($"Rehearsed a focus session of {length} minutes; it was not started.");
        }

        Press(start);
        return ClockResult.Ok($"A focus session of {length} minutes has started in the Clock app. Notifications are quiet until it ends.");
    }

    // ---- Finding the app and moving about in it -------------------------------------------------------------------------------------------

    // Opens the Clock app, or brings it forward, and gives its window once its tabs are there.
    //
    // An app that is running and shows no window that can be found is in its small keep-on-top view: Windows keeps that view out of what other programs
    // can list, look into or press, so nothing can be done in it. It is closed, as its own close button closes it, and the app is opened again in full;
    // its alarms are kept and a timer that was running goes on running.
    private AutomationElement? Open(CancellationToken token)
    {
        var wasRunning = IsRunning();
        _wasAway = !wasRunning;
        _wasOnTop = false;
        Launch();
        var window = WaitFor(Ready, wasRunning ? KeepOnTopLook : OpenTime, token);
        if (window is null && IsRunning())
        {
            _wasOnTop = true;
            LeaveKeepOnTop(token);
            Launch();
            window = WaitFor(Ready, OpenTime, token);
        }

        return window;
    }

    private static void Launch()
    {
        using (Process.Start(new ProcessStartInfo("ms-clock:") { UseShellExecute = true }))
        {
        }
    }

    private AutomationElement? Ready()
    {
        var window = FindWindow();
        return window is not null && Find(window, "NavView") is not null ? window : null;
    }

    private static bool IsRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    // Closes the small keep-on-top view: the window that was the app's when it was last seen, or the one of the app's name, is asked to close, and an app
    // that is still there with no window after that is ended. Nothing the user set is lost by either: the app keeps its alarms and timers itself.
    private void LeaveKeepOnTop(CancellationToken token)
    {
        var frame = _frame != 0 && User32.IsWindow(_frame) && User32.IsWindowVisible(_frame) ? _frame : ClockKeys.FindFrame(_frameTitle);
        _frame = 0;
        if (frame != 0 && User32.IsWindow(frame))
        {
            User32.PostMessage(frame, ClockKeys.WM_CLOSE, 0, 0);
            if (WaitUntil(() => !IsRunning(), StepTime, token))
            {
                return;
            }
        }

        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // It ended by itself, or is not this user's to end.
                }
            }
        }
    }

    // The Clock app's own window: the content of the frame Windows draws for it. The frame is remembered, with what it is called, since it is the same
    // window that becomes the small keep-on-top view, which cannot be found by looking.
    private AutomationElement? FindWindow()
    {
        var clock = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                clock.Add(process.Id);
            }
        }

        if (clock.Count == 0)
        {
            return null;
        }

        var frames = AutomationElement.RootElement.FindAll(
            TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow"));
        foreach (AutomationElement frame in frames)
        {
            var content = frame.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Windows.UI.Core.CoreWindow"));
            if (content is null || !clock.Contains(content.Current.ProcessId))
            {
                continue;
            }

            // A window that was minimized is not drawn, and its controls do not answer until it is put back.
            if (frame.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) && pattern is WindowPattern shown
                && shown.Current.WindowVisualState == WindowVisualState.Minimized)
            {
                shown.SetWindowVisualState(WindowVisualState.Normal);
            }

            _frame = frame.Current.NativeWindowHandle;
            if (frame.Current.Name is { Length: > 0 } title)
            {
                _frameTitle = title;
            }

            return content;
        }

        return null;
    }

    // Goes to a tab and waits for the control that says its page is up. A dialog left open from before is cancelled first.
    private static void GoTo(AutomationElement window, string tabId, string pageControlId, CancellationToken token)
    {
        if (Find(window, "EditFlyout") is { } open)
        {
            Cancel(open);
            WaitUntil(() => Find(window, "EditFlyout") is null, StepTime, token);
        }

        if (Find(window, pageControlId) is null)
        {
            SelectTab(window, tabId, token);
        }

        if (WaitFor(() => Find(window, pageControlId), StepTime, token) is null)
        {
            throw new ClockControlException("the page of a tab did not come up");
        }
    }

    // Chooses a tab. A narrow window keeps its tabs behind the menu button, which is pressed first to bring them out.
    private static AutomationElement SelectTab(AutomationElement window, string tabId, CancellationToken token)
    {
        var tab = Find(window, tabId);
        if (tab is null && Find(window, "MenuButton") is { } menu)
        {
            Press(menu);
            tab = WaitFor(() => Find(window, tabId), StepTime, token);
        }

        if (tab is null || !tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) || pattern is not SelectionItemPattern item)
        {
            throw new ClockControlException("a tab was not found");
        }

        item.Select();
        return tab;
    }

    private static bool IsSelected(AutomationElement tab)
    {
        try
        {
            return tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) && pattern is SelectionItemPattern item && item.Current.IsSelected;
        }
        catch (ElementNotAvailableException)
        {
            // The pane the tab was in has closed, which it does once a tab is chosen.
            return true;
        }
    }

    // The card of the timer called `label`, with its Start and Keep on top buttons: the last one of that name, which is the one just added; any last
    // timer when the name is not known.
    private static AutomationElement? TimerCardOf(AutomationElement window, string? label)
    {
        var cards = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TimerViewGrid"));
        for (var index = cards.Count - 1; index >= 0; index--)
        {
            var card = cards[index];
            if ((label is null || string.Equals(Find(card, "TimerNameText")?.Current.Name, label, StringComparison.CurrentCulture))
                && Find(card, "TimerPlayPauseButton") is not null)
            {
                return card;
            }
        }

        // The list shows as many as fit: the new one is at its end, which is scrolled to for the next look.
        if (Find(window, "TimerScrollViewer") is { } list && list.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && pattern is ScrollPattern scroll
            && scroll.Current.VerticallyScrollable)
        {
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
        }

        return null;
    }

    // ---- The dialog's fields ------------------------------------------------------------------------------------------------------------------

    // Chooses AM or PM, by the PC's own words for them and then by the English ones; a picker that takes neither is turned once, since it has two
    // values. Says whether the picker ended on the half of the day that was wanted.
    private static bool SetPeriod(AutomationElement period, bool afternoon)
    {
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        string[] wanted = afternoon ? [format.PMDesignator, "PM"] : [format.AMDesignator, "AM"];
        string[] other = afternoon ? [format.AMDesignator, "AM"] : [format.PMDesignator, "PM"];
        bool Is(string[] words) => words.Any(word => word.Length > 0 && string.Equals(Letters(TextOf(period)), Letters(word), StringComparison.OrdinalIgnoreCase));

        if (Is(wanted))
        {
            return true;
        }

        foreach (var word in wanted.Where(word => word.Length > 0))
        {
            try
            {
                SetText(period, word);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or COMException)
            {
                // Not a value this picker has; the next way is tried.
            }

            if (Is(wanted))
            {
                return true;
            }
        }

        // It shows the other half: one step of the picker is the wanted one.
        if (Is(other) && period.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)) is { } step)
        {
            var before = TextOf(period);
            Press(step);
            Thread.Sleep(200);
            return !string.Equals(TextOf(period), before, StringComparison.Ordinal);
        }

        return false;
    }

    // Turns "repeat" on or off, and with it on, each day as wanted. Says whether the days could be told apart.
    private static bool SetRepeat(AutomationElement dialog, IReadOnlyList<DayOfWeek> days)
    {
        var wanted = days.Count > 0;
        if (Find(dialog, "RepeatCheckBox") is { } repeat && repeat.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern) && pattern is TogglePattern toggle)
        {
            if ((toggle.Current.ToggleState == ToggleState.On) != wanted)
            {
                toggle.Toggle();
                Thread.Sleep(150);
            }
        }
        else if (wanted)
        {
            return false;
        }

        if (!wanted)
        {
            return true;
        }

        var buttons = dialog.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "ToggleButton"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)));
        var set = new HashSet<DayOfWeek>();
        foreach (AutomationElement button in buttons)
        {
            if (DayOf(button.Current.Name) is not { } day || !button.TryGetCurrentPattern(TogglePattern.Pattern, out var dayPattern) || dayPattern is not TogglePattern dayToggle)
            {
                continue;
            }

            if ((dayToggle.Current.ToggleState == ToggleState.On) != days.Contains(day))
            {
                dayToggle.Toggle();
            }

            set.Add(day);
        }

        return days.All(set.Contains);
    }

    // The day a button is for, by its name in the PC's language or in English.
    private static DayOfWeek? DayOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var culture in new[] { CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                if (string.Equals(name.Trim(), culture.DateTimeFormat.GetDayName(day), StringComparison.CurrentCultureIgnoreCase)
                    || string.Equals(name.Trim(), culture.DateTimeFormat.GetAbbreviatedDayName(day), StringComparison.CurrentCultureIgnoreCase))
                {
                    return day;
                }
            }
        }

        return null;
    }

    private static string DayName(DayOfWeek day) => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);

    // Gives a picker a number and makes sure it took it: a picker that shows another number has not been set. The picker turns to the number, which
    // takes it a moment, so it is looked at until it shows it.
    private static void SetNumber(AutomationElement picker, int value)
    {
        SetText(picker, value.ToString(CultureInfo.InvariantCulture));
        var clock = Stopwatch.StartNew();
        while (!int.TryParse(Digits(TextOf(picker)), NumberStyles.None, CultureInfo.InvariantCulture, out var shown) || shown != value)
        {
            if (clock.Elapsed >= SettleTime)
            {
                throw new ClockControlException("a picker did not take its number");
            }

            Thread.Sleep(Poll);
        }
    }

    private static void SetText(AutomationElement element, string value)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern text || text.Current.IsReadOnly)
        {
            throw new ClockControlException("a field cannot be written to");
        }

        text.SetValue(value);
    }

    private static string TextOf(AutomationElement element) =>
        element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern text ? text.Current.Value ?? string.Empty : string.Empty;

    private static AutomationElement? FindEdit(AutomationElement dialog) =>
        dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

    private static void Cancel(AutomationElement dialog)
    {
        try
        {
            if (Find(dialog, "CloseButton") is { } close)
            {
                Press(close);
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // The dialog has gone by itself.
        }
    }

    private static void Press(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) || pattern is not InvokePattern invoke)
        {
            throw new ClockControlException();
        }

        invoke.Invoke();
    }

    private static AutomationElement? Find(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    private static AutomationElement Require(AutomationElement root, string automationId) =>
        Find(root, automationId) ?? throw new ClockControlException();

    private static AutomationElement? WaitFor(Func<AutomationElement?> find, TimeSpan time, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (find() is { } found)
            {
                return found;
            }

            if (clock.Elapsed >= time)
            {
                return null;
            }

            Thread.Sleep(Poll);
        }
    }

    private static bool WaitUntil(Func<bool> done, TimeSpan time, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= time)
            {
                return false;
            }

            Thread.Sleep(Poll);
        }

        return true;
    }

    private static string Digits(string text) => new([.. text.Where(char.IsAsciiDigit)]);

    private static string Letters(string text) => new([.. text.Where(char.IsLetter)]);

    // "1 hour 5 minutes", "90 seconds".
    private static string Describe(TimeSpan duration)
    {
        var parts = new List<string>(3);
        void Add(int amount, string unit)
        {
            if (amount > 0)
            {
                parts.Add(amount.ToString(CultureInfo.CurrentCulture) + " " + unit + (amount == 1 ? string.Empty : "s"));
            }
        }

        Add((int)duration.TotalHours, "hour");
        Add(duration.Minutes, "minute");
        Add(duration.Seconds, "second");
        return string.Join(' ', parts);
    }

    // A control of the Clock app was not where it is expected, or did not take what it was given. The message says which, in words of this class's own.
    private sealed class ClockControlException(string what = "a control was not found") : Exception(what)
    {
    }

    // What was asked for and why it stopped are this class's own fixed words (never a timer's or an alarm's name), under the names the log's privacy
    // filter lets through, so that a control that has moved in a new version of the Clock app can be read from the log.
    [LoggerMessage(EventId = 6400, Level = LogLevel.Information, Message = "Clock app: {EventName} done: {Done}")]
    private static partial void LogDone(ILogger logger, string eventName, bool done);

    [LoggerMessage(EventId = 6401, Level = LogLevel.Warning, Message = "Clock app: {EventName} could not be finished ({Outcome})")]
    private static partial void LogFailed(ILogger logger, string eventName, string outcome);
}
