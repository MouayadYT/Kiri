using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Assistant.Core.Clock;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Clock;

/// <summary>
/// A timer on the Clock app's list, as it was read: what it is called, which of the timers of that name it is (counted from the top, from 0), whether
/// it has been started (it is not at its full length) and whether it is counting.
/// </summary>
internal sealed record ListedTimer(string Name, int Place, bool Started, bool Running);

/// <summary>Decides which of the Clock app's timers an action means, by fixed rules.</summary>
internal static class TimerChoice
{
    /// <summary>
    /// The timer <paramref name="action"/> means. Of the timers called <paramref name="wanted"/> (the name itself, in any case, or else a name that has
    /// it in it), or of all of them when no name was given, it is one the action can be done to: a counting one to pause, a paused one to carry on
    /// with, and to stop or start again a counting one before a paused one. Of several, it is the one the Assistant started last
    /// (<paramref name="added"/>, newest last), and else the last on the list. A timer asked for by name that is at rest can still be started again,
    /// and one the Assistant added can still be stopped, which takes it off the list.
    /// </summary>
    /// <param name="named">Whether any timer has the name that was asked for.</param>
    public static ListedTimer? Choose(IReadOnlyList<ListedTimer> timers, TimerAction action, string? wanted, IReadOnlyList<string> added, out bool named)
    {
        var among = timers;
        if (wanted is not null)
        {
            among = [.. timers.Where(timer => string.Equals(timer.Name, wanted, StringComparison.CurrentCultureIgnoreCase))];
            if (among.Count == 0)
            {
                among = [.. timers.Where(timer => timer.Name.Contains(wanted, StringComparison.CurrentCultureIgnoreCase))];
            }
        }

        named = wanted is not null && among.Count > 0;
        List<ListedTimer> fitting = action switch
        {
            TimerAction.Pause => [.. among.Where(timer => timer.Running)],
            TimerAction.Resume => [.. among.Where(timer => timer.Started && !timer.Running)],
            _ => among.Any(timer => timer.Running) ? [.. among.Where(timer => timer.Running)] : [.. among.Where(timer => timer.Started)],
        };

        if (fitting.Count == 0 && named)
        {
            fitting = action switch
            {
                TimerAction.Restart => [.. among],
                TimerAction.Stop => [.. among.Where(timer => added.Contains(timer.Name))],
                _ => fitting,
            };
        }

        for (var index = added.Count - 1; index >= 0; index--)
        {
            if (fitting.LastOrDefault(timer => string.Equals(timer.Name, added[index], StringComparison.CurrentCulture)) is { } own)
            {
                return own;
            }
        }

        return fitting.Count > 0 ? fitting[^1] : null;
    }
}

// The part of the driver that works a timer that is already on the Clock app's list.
public sealed partial class WindowsClockApp
{
    // How long a timer is watched to see whether it is counting: longer than a second, which is how often its time changes.
    private static readonly TimeSpan RunningLook = TimeSpan.FromMilliseconds(1250);

    private static readonly PropertyCondition IsTimerCard = new(AutomationElement.AutomationIdProperty, "TimerViewGrid");

    // What the timers the Assistant itself added since it started are called in the Clock app, the newest last. They are the only timers it ever takes
    // off the Clock app's list, and, of several that an action could mean, the ones it means first. Read and written inside the gate only.
    private readonly List<string> _added = [];

    // How the Clock app was found by the action that is running: not running at all, or in its small keep-on-top view (which had to be left to reach
    // anything). What the action leaves behind goes by these: a timer that was small on top is put back there, and a window that was opened only to
    // stop a timer is closed again.
    private bool _wasAway;
    private bool _wasOnTop;

    /// <inheritdoc/>
    public Task<ClockResult> ControlTimerAsync(TimerAction action, string? name = null, CancellationToken cancellationToken = default) =>
        RunAsync("timer " + action.ToString().ToLowerInvariant(), (window, token) => ControlTimer(window, action, name, token), cancellationToken);

    private ClockResult ControlTimer(AutomationElement window, TimerAction action, string? name, CancellationToken token)
    {
        GoTo(window, "TimerButton", "AddTimerButton", token);

        // A list left in its Edit mode has nothing on it that can be pressed but the bins.
        if (Find(window, "DoneButton") is { } leave)
        {
            Press(leave);
            WaitUntil(() => Find(window, "DoneButton") is null, SettleTime, token);
        }

        // The list fills in a moment after its page is up, and a little after its first card its last: it is read once it has stopped growing.
        WaitUntil(() => window.FindAll(TreeScope.Descendants, IsTimerCard).Count > 0, StepTime, token);
        for (var (seen, tries) = (-1, 0); tries < 8; tries++)
        {
            var count = window.FindAll(TreeScope.Descendants, IsTimerCard).Count;
            if (count == seen)
            {
                break;
            }

            seen = count;
            Thread.Sleep(200);
        }
        var wanted = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var timer = TimerChoice.Choose(ReadTimers(window, token), action, wanted, _added, out var named);

        // The list shows as many as fit; the newest are at its end, which is looked at when the timer was not among the first.
        if (timer is null && Find(window, "TimerScrollViewer") is { } list && list.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)
            && pattern is ScrollPattern { Current.VerticallyScrollable: true } scroll)
        {
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
            Thread.Sleep(300);
            timer = TimerChoice.Choose(ReadTimers(window, token), action, wanted, _added, out var namedAtEnd);
            named |= namedAtEnd;
        }

        if (timer is null)
        {
            return wanted is not null && !named
                ? ClockResult.Failed($"There is no timer called \"{wanted}\" in the Clock app, so nothing was changed.")
                : ClockResult.Ok(action switch
                {
                    TimerAction.Pause => "No timer is running in the Clock app, so there was nothing to pause.",
                    TimerAction.Resume => "No timer is paused in the Clock app, so there was nothing to carry on with.",
                    TimerAction.Restart => "No timer has been started in the Clock app, so there was nothing to start again.",
                    _ => "No timer is running in the Clock app, so there was nothing to stop.",
                });
        }

        var called = timer.Name.Length > 0 ? $"The timer \"{timer.Name}\"" : "The timer";
        if (_rehearse)
        {
            return ClockResult.Ok($"Rehearsed: {called} would be told to {action.ToString().ToLowerInvariant()}; nothing was pressed.");
        }

        // Where the timer was small on top before, it is put back there; a timer that counts again is kept on top as every timer the Assistant starts is.
        var wasOnTop = _wasOnTop;
        switch (action)
        {
            case TimerAction.Pause:
                PressOn(window, timer, "TimerPlayPauseButton", token);
                if (wasOnTop)
                {
                    KeepOnTop(CardOf(window, timer), token);
                }

                return ClockResult.Ok($"{called} is paused in the Clock app.");

            case TimerAction.Resume:
                PressOn(window, timer, "TimerPlayPauseButton", token);
                return ClockResult.Ok(KeepOnTop(CardOf(window, timer), token)
                    ? $"{called} is counting down again in the Clock app, kept small on top of your other windows."
                    : $"{called} is counting down again in the Clock app.");

            case TimerAction.Restart:
                PutBack(window, timer, token);
                PressOn(window, timer, "TimerPlayPauseButton", token);
                return ClockResult.Ok(KeepOnTop(CardOf(window, timer), token)
                    ? $"{called} is counting down again from its full length in the Clock app, kept small on top of your other windows."
                    : $"{called} is counting down again from its full length in the Clock app.");

            default:
                PutBack(window, timer, token);

                // A timer the Assistant added for the user goes from the list with the countdown; one of the user's own stays, stopped.
                var removed = timer.Name.Length > 0 && _added.Contains(timer.Name) && Remove(window, timer, token);
                if (removed)
                {
                    _added.Remove(timer.Name);
                }

                // The Clock app was brought out only to stop the timer: there is nothing left in it to look at.
                if (wasOnTop || _wasAway)
                {
                    CloseWindow();
                }

                return ClockResult.Ok(removed
                    ? $"{called} is stopped and taken off the Clock app's list. It will not ring."
                    : $"{called} is stopped and back at its full length in the Clock app. It will not ring.");
        }
    }

    // The timers on the list as it shows now. One that has been started can be reset; one that is counting shows another time a moment later, which
    // tells it from a paused one whatever language its buttons are in.
    private static List<ListedTimer> ReadTimers(AutomationElement window, CancellationToken token)
    {
        List<(string Name, bool Started, string Time)> Look()
        {
            var seen = new List<(string Name, bool Started, string Time)>();
            foreach (AutomationElement card in window.FindAll(TreeScope.Descendants, IsTimerCard))
            {
                if (Find(card, "TimerPlayPauseButton") is not null)
                {
                    seen.Add((NameOn(card), Find(card, "TimerResetButton")?.Current.IsEnabled == true, TimeOn(card)));
                }
            }

            return seen;
        }

        var first = Look();
        var later = first;
        if (first.Any(timer => timer.Started))
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < RunningLook)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(Poll);
            }

            later = Look();
        }

        // A list that changed between the two looks is read as it is now, with nothing on it taken to be counting.
        var same = later.Count == first.Count && later.Select(timer => timer.Name).SequenceEqual(first.Select(timer => timer.Name), StringComparer.Ordinal);
        var timers = new List<ListedTimer>(later.Count);
        var places = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < later.Count; index++)
        {
            var (name, started, time) = later[index];
            var place = places.GetValueOrDefault(name);
            places[name] = place + 1;
            timers.Add(new ListedTimer(name, place, started, started && same && !string.Equals(time, first[index].Time, StringComparison.Ordinal)));
        }

        return timers;
    }

    // The card a timer has now. It is found again before every press: the app builds a card anew when its timer starts, stops or is put back, and what
    // was this timer's card a moment ago may be another timer's by then. A card is only ever this timer's while it carries its name.
    private static AutomationElement? CardOf(AutomationElement window, ListedTimer timer)
    {
        var place = 0;
        foreach (AutomationElement card in window.FindAll(TreeScope.Descendants, IsTimerCard))
        {
            if (string.Equals(NameOn(card), timer.Name, StringComparison.Ordinal) && place++ == timer.Place)
            {
                return card;
            }
        }

        return null;
    }

    private static void PressOn(AutomationElement window, ListedTimer timer, string buttonId, CancellationToken token)
    {
        var card = WaitFor(() => CardOf(window, timer), StepTime, token) ?? throw new ClockControlException("the timer's card was not found again");
        var button = Require(card, buttonId);

        // Looked at once more with the button in hand: nothing is pressed on a card that is not this timer's.
        if (!string.Equals(NameOn(card), timer.Name, StringComparison.Ordinal))
        {
            throw new ClockControlException("the timer's card changed under the press");
        }

        Press(button);
    }

    private static string NameOn(AutomationElement card) => Find(card, "TimerNameText")?.Current.Name ?? string.Empty;

    // The time a timer's card shows, as the app says it.
    private static string TimeOn(AutomationElement card) =>
        Find(card, "TimerValueText") is { } value ? TextOf(value) is { Length: > 0 } text ? text : value.Current.Name ?? string.Empty : string.Empty;

    // Ends a timer's countdown and puts it back to its full length: a timer that is counting is paused first, then reset.
    private static void PutBack(AutomationElement window, ListedTimer timer, CancellationToken token)
    {
        if (timer.Running)
        {
            PressOn(window, timer, "TimerPlayPauseButton", token);
            Thread.Sleep(300);
        }

        bool CanReset() => CardOf(window, timer) is { } card && Find(card, "TimerResetButton")?.Current.IsEnabled == true;
        if (WaitUntil(CanReset, timer.Started ? SettleTime : TimeSpan.Zero, token))
        {
            PressOn(window, timer, "TimerResetButton", token);
            WaitUntil(() => !CanReset(), SettleTime, token);
        }
    }

    // Takes a timer off the list the way a person does: Edit, the bin on its card, Done. Says whether it went. The list is never left in its Edit mode.
    private static bool Remove(AutomationElement window, ListedTimer timer, CancellationToken token)
    {
        try
        {
            if (Find(window, "EditTimersButton") is not { Current.IsEnabled: true } edit)
            {
                return false;
            }

            int Named() => window.FindAll(TreeScope.Descendants, IsTimerCard).Cast<AutomationElement>()
                .Count(card => string.Equals(NameOn(card), timer.Name, StringComparison.Ordinal));
            var before = Named();
            Press(edit);
            var gone = false;
            try
            {
                if (WaitFor(() => CardOf(window, timer) is { } card ? Find(card, "DeleteButton") : null, SettleTime, token) is not null)
                {
                    PressOn(window, timer, "DeleteButton", token);
                    gone = WaitUntil(() => Named() < before, SettleTime, token);
                }
            }
            finally
            {
                if (Find(window, "DoneButton") is { } done)
                {
                    Press(done);
                }
            }

            return gone;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException or ClockControlException)
        {
            return false;
        }
    }

    // Closes the Clock app's window, as its own close button does. Its alarms and timers are the app's to keep, window or no window.
    private void CloseWindow()
    {
        if (_frame != 0 && User32.IsWindow(_frame))
        {
            User32.PostMessage(_frame, ClockKeys.WM_CLOSE, 0, 0);
            _frame = 0;
        }
    }
}
