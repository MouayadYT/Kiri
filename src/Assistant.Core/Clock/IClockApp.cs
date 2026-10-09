namespace Assistant.Core.Clock;

/// <summary>What is done to the Clock app's stopwatch.</summary>
public enum StopwatchAction
{
    /// <summary>Start it, or carry on from where it was paused.</summary>
    Start = 0,

    /// <summary>Pause it where it is.</summary>
    Pause = 1,

    /// <summary>Put it back to zero.</summary>
    Reset = 2,
}

/// <summary>What is done to a timer that is in the Clock app.</summary>
public enum TimerAction
{
    /// <summary>End its countdown, so that it does not ring, and put it back to its full length. A timer the Assistant added is taken off the list too.</summary>
    Stop = 0,

    /// <summary>Hold it where it is.</summary>
    Pause = 1,

    /// <summary>Carry on from where it was paused.</summary>
    Resume = 2,

    /// <summary>Count down again from its full length.</summary>
    Restart = 3,
}

/// <summary>An alarm to set in the Clock app.</summary>
/// <param name="Time">When it rings, in the PC's own time.</param>
/// <param name="Name">What it is called in the Clock app, or <see langword="null"/> for the name the app gives it.</param>
public sealed record ClockAlarm(TimeOnly Time, string? Name = null)
{
    /// <summary>The days it rings on every week; none for an alarm that rings once.</summary>
    public IReadOnlyList<DayOfWeek> RepeatOn { get; init; } = [];
}

/// <summary>What became of something asked of the Clock app.</summary>
/// <param name="Done">Whether it was done.</param>
/// <param name="Message">What happened, in words that can be told to the user: what was set, or why it was not.</param>
public sealed record ClockResult(bool Done, string Message)
{
    /// <summary>It was done.</summary>
    public static ClockResult Ok(string message) => new(true, message);

    /// <summary>It was not done.</summary>
    public static ClockResult Failed(string message) => new(false, message);
}

/// <summary>
/// The Windows Clock app, as the Assistant uses it for the user (PROJECT_SPEC §4.8): setting an alarm, starting a timer, running the stopwatch and starting a
/// focus session, which also turns Windows' own focus (do not disturb) on. Windows gives other programs no interface for any of these (the Clock app's own
/// service answers only Microsoft's apps), so the app is worked the way a person works it: it is opened, and its own buttons and fields are pressed and
/// filled in through UI Automation. Everything therefore ends up in the Clock app itself, where the user sees, changes and removes it as always.
/// </summary>
public interface IClockApp
{
    /// <summary>Whether the Clock app is installed for this user.</summary>
    bool IsInstalled { get; }

    /// <summary>Adds <paramref name="alarm"/> to the Clock app's alarms, turned on.</summary>
    Task<ClockResult> SetAlarmAsync(ClockAlarm alarm, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a timer of <paramref name="duration"/> to the Clock app, starts it and keeps it on top: the Clock app's own "Keep on top", which shrinks
    /// its window to the timer alone, above the other windows.
    /// </summary>
    /// <param name="duration">How long it counts down, from one second to under a hundred hours.</param>
    /// <param name="name">What it is called in the Clock app, or <see langword="null"/> for the name the app gives it.</param>
    Task<ClockResult> StartTimerAsync(TimeSpan duration, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops, pauses, resumes or restarts a timer that is in the Clock app: the one called <paramref name="name"/>, or, with no name, the one the
    /// action can only mean (the timer that is running, or the one that is paused), and of several, the one the Assistant started last. Only a
    /// timer the Assistant itself added is ever taken off the Clock app's list; the user's own are stopped and left where they are.
    /// </summary>
    Task<ClockResult> ControlTimerAsync(TimerAction action, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>Starts, pauses or resets the Clock app's stopwatch.</summary>
    Task<ClockResult> ControlStopwatchAsync(StopwatchAction action, CancellationToken cancellationToken = default);

    /// <summary>Starts a focus session of <paramref name="minutes"/> minutes in the Clock app, which turns notifications off for as long as it lasts.</summary>
    /// <param name="minutes">How long it lasts; held to what the Clock app accepts.</param>
    /// <param name="skipBreaks">Whether the session runs without the breaks the app would put in it.</param>
    Task<ClockResult> StartFocusSessionAsync(int minutes, bool skipBreaks = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the Clock app's window, when it has one open, to the display the user chose for it (<see cref="ClockPlace"/>). The same is done by itself
    /// whenever the Assistant opens the Clock app, so this is only for a choice that was made just now.
    /// </summary>
    Task<ClockResult> ApplyPlaceAsync(CancellationToken cancellationToken = default) => Task.FromResult(ClockResult.Ok(string.Empty));

    /// <summary>
    /// Where the Clock app's window is on its display now, with how much of the display it takes, or <see langword="null"/> when it has no window open
    /// (the app is not opened to find out) or the window fills its display.
    /// </summary>
    Task<ClockSpot?> ReadSpotAsync(CancellationToken cancellationToken = default) => Task.FromResult<ClockSpot?>(null);
}
