using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Assistant.Core.Clock;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools.Clock;

/// <summary>
/// Which requests are about the clock: an alarm, a timer, the stopwatch or a focus session. The clock tools are offered only for these, so that they
/// cost a small model nothing in every other request, and for the rest of a conversation that began with one ("make it five", "and pause it"). A
/// word that is a slip of the keyboard away counts ("set a tiemr": <see cref="RequestWords"/>): without the clock's tools the model opened the
/// Calculator for it.
/// </summary>
internal static class ClockRequests
{
    private const int MaxRemembered = 64;

    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "alarm", "alarms", "wake", "timer", "timers", "countdown", "stopwatch", "lap", "laps", "focus", "pomodoro", "clock", "snooze", "remind",
        "disturb", "distractions",
    };

    private static readonly HashSet<string> TimerWords = new(StringComparer.Ordinal) { "timer", "timers", "countdown" };

    private static readonly HashSet<string> AlarmWords = new(StringComparer.Ordinal) { "alarm", "alarms", "wake" };

    // Each conversation that is about the clock, with whether what it last named was a timer (1), an alarm (2) or neither (0).
    private static readonly ConcurrentDictionary<Guid, byte> Active = new();

    /// <summary>Whether the request in <paramref name="context"/> is about the clock, or an earlier one in its conversation was.</summary>
    public static bool IsAbout(ToolContext context)
    {
        if (context.Request is null)
        {
            return true;
        }

        var words = RequestWords.Of(context.Request);
        if (RequestWords.Names(words, Words, slips: true))
        {
            if (Active.Count >= MaxRemembered)
            {
                Active.Clear();
            }

            // What the conversation is about stays what it was until the user names the other thing: "I meant 12:22" is still about the timer.
            var (timer, alarm) = (RequestWords.Names(words, TimerWords, slips: true), RequestWords.Names(words, AlarmWords, slips: true));
            var named = timer == alarm ? (byte)0 : timer ? (byte)1 : (byte)2;
            Active.AddOrUpdate(context.ConversationId, named, (_, before) => timer || alarm ? named : before);
            return true;
        }

        return Active.ContainsKey(context.ConversationId);
    }

    /// <summary>
    /// Whether the user is talking about a timer and has not asked for an alarm: the conversation last named a timer, and this request does not name an
    /// alarm. An alarm is then not what they mean, whatever time of day they give.
    /// </summary>
    public static bool IsAboutATimer(ToolContext context)
    {
        if (context.Request is null)
        {
            return false;
        }

        var words = RequestWords.Of(context.Request);
        if (RequestWords.Names(words, AlarmWords, slips: true))
        {
            return false;
        }

        return RequestWords.Names(words, TimerWords, slips: true) || (Active.TryGetValue(context.ConversationId, out var named) && named == 1);
    }
}

/// <summary>
/// The tools that work the Windows Clock app for the user (PROJECT_SPEC §4.8): <c>set_alarm</c>, <c>start_timer</c>, <c>control_timer</c>, <c>stopwatch</c> and
/// <c>start_focus_session</c>. Each does one fixed thing in the Clock app through <see cref="IClockApp"/>, and what it sets ends up there, where the user
/// sees and changes it as always. They change something, so the user confirms each call, with the time or the length shown as it will be set; a time or
/// a length that cannot be read is told to the model and nobody is asked. They are offered only while the Clock app is installed and the request is
/// about the clock (<see cref="ClockRequests"/>).
/// </summary>
public abstract class ClockTool(IClockApp? clock) : ITool
{
    /// <summary>The name of the tool that sets an alarm.</summary>
    public const string SetAlarm = "set_alarm";

    /// <summary>The name of the tool that starts a timer.</summary>
    public const string StartTimer = "start_timer";

    /// <summary>The name of the tool that stops, pauses, resumes and restarts a timer.</summary>
    public const string ControlTimer = "control_timer";

    /// <summary>The name of the tool that starts, pauses and resets the stopwatch.</summary>
    public const string Stopwatch = "stopwatch";

    /// <summary>The name of the tool that starts a focus session.</summary>
    public const string StartFocusSession = "start_focus_session";

    /// <summary>The longest a name of an alarm or a timer may be.</summary>
    public const int MaxNameLength = 60;

    /// <inheritdoc/>
    public abstract ToolDefinition Definition { get; }

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return clock is { IsInstalled: true } && ClockRequests.IsAbout(context);
    }

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => IsOffered(context);

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (action, refusal) = Read(call, arguments, context);
        return refusal ?? await DoAsync(call, action!, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>What was asked is read before the user is asked, so a time that cannot be read is told to the model, and the user is shown what will be set.</remarks>
    public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (action, refusal) = Read(call, arguments, context);
        if (refusal is not null)
        {
            return Task.FromResult(ToolPlan.Refuse(refusal));
        }

        var planned = action!;
        return Task.FromResult(ToolPlan.Do(
            token => DoAsync(call, planned, token),
            new ToolConfirmation(ConfirmationKind.ChangeSystem, planned.Question, planned.Details, planned.Button, "It is done in the Windows Clock app, which opens.")));
    }

    /// <summary>What a call would do, read from its arguments: the question put to the user, and the action itself.</summary>
    protected sealed record ClockAction(string Question, IReadOnlyList<ConfirmationDetail> Details, string Button, Func<IClockApp, CancellationToken, Task<ClockResult>> Run);

    /// <summary>Reads the call: what it would do, or why it cannot be done, as the call's result.</summary>
    protected abstract (ClockAction? Action, ToolResult? Refusal) Read(ToolCall call, JsonElement arguments, ToolContext context);

    /// <summary>A failed result that says <paramref name="message"/> to the model.</summary>
    protected static ToolResult Failed(ToolCall call, string message) => ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);

    /// <summary>A result that says the arguments were not right, with how the tool is called.</summary>
    protected ToolResult Invalid(ToolCall call, string message) =>
        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, message, ToolUsage.Describe(Definition));

    /// <summary>The text of the argument <paramref name="name"/>, tidied to one line, or empty.</summary>
    protected static string Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? string.Join(' ', (value.GetString() ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            : string.Empty;

    /// <summary>The whole number of the argument <paramref name="name"/>, or <see langword="null"/> when it was not given.</summary>
    protected static int? Number(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && number is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Round(number)
            : null;

    /// <summary>Whether the argument <paramref name="name"/> was given as true.</summary>
    protected static bool Flag(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task<ToolResult> DoAsync(ToolCall call, ClockAction action, CancellationToken cancellationToken)
    {
        if (clock is not { IsInstalled: true })
        {
            return Failed(call, "The Windows Clock app is not installed on this PC, so this cannot be done. Tell the user.");
        }

        var result = await action.Run(clock, cancellationToken).ConfigureAwait(false);
        return result.Done
            ? new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, SystemToolResults.Done(result.Message))
            : Failed(call, result.Message + " Tell the user what happened; do not say it was set.");
    }
}

/// <summary><c>set_alarm</c>: adds an alarm to the Windows Clock app.</summary>
public sealed class SetAlarmTool(IClockApp? clock) : ClockTool(clock)
{
    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        SetAlarm,
        "Set an alarm in the Windows Clock app for a time of day, such as 4 pm or 07:30. Use it only when the user says alarm or asks to be woken: it sets the alarm " +
        "itself, so do not open the Clock app or tell the user how to do it. When the user says timer, use start_timer, even for a time of day (a timer for 9:18 pm), " +
        "and for a length of time from now (in 10 minutes).",
        [
            new ToolParameter("time", ToolParameterType.String, "The time of day in 24-hour HH:MM, such as 16:00 for 4 pm or 07:30 for half past seven in the morning.", MaxLength: 20),
            new ToolParameter("name", ToolParameterType.String, "What the alarm is for, if the user said, such as Take the bread out. Leave it out otherwise.", Required: false, MaxLength: MaxNameLength),
            new ToolParameter(
                "days", ToolParameterType.String,
                "For an alarm that repeats: the days, such as monday,wednesday or weekdays, weekends, every day. Leave it out for an alarm that rings once.", Required: false, MaxLength: 80),
        ],
        RiskLevel.SideEffect,
        timeout: TimeSpan.FromSeconds(45));

    /// <inheritdoc/>
    protected override (ClockAction? Action, ToolResult? Refusal) Read(ToolCall call, JsonElement arguments, ToolContext context)
    {
        // The user is talking about a timer ("set a timer for 12:20", then "I meant 12:22 am"): a time of day does not make it an alarm.
        if (ClockRequests.IsAboutATimer(context) || Text(arguments, "name").Contains("timer", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Failed(
                call,
                "The user is asking for a timer, not an alarm, so no alarm was set. Call start_timer now with until set to the time they mean, such as 12:22 am."));
        }

        if (ClockTimes.ParseTime(Text(arguments, "time")) is not { } read)
        {
            return (null, Invalid(call, "That is not a time of day. Give it as HH:MM in 24 hours, such as 16:00 for 4 pm."));
        }

        // When the user said am or pm, that is the half of the day it is, whatever was worked out from it.
        var time = ClockTimes.HalfSaid(context.Request) switch
        {
            DayHalf.Morning => new TimeOnly(read.Hour % 12, read.Minute),
            DayHalf.Afternoon => new TimeOnly((read.Hour % 12) + 12, read.Minute),
            _ => read,
        };

        var named = Text(arguments, "days");
        if (ClockTimes.ParseDays(named) is not { } days)
        {
            return (null, Invalid(call, "Those days could not be read. Give them as names, such as monday,wednesday, or weekdays, weekends or every day."));
        }

        var name = Text(arguments, "name");
        var shown = time.ToString("t", CultureInfo.CurrentCulture);
        var details = new List<ConfirmationDetail> { new("Time", shown) };
        if (days.Count > 0)
        {
            details.Add(new ConfirmationDetail("Repeats", string.Join(", ", days.Select(day => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day)))));
        }

        if (name.Length > 0)
        {
            details.Add(new ConfirmationDetail("Name", name));
        }

        var alarm = new ClockAlarm(time, name.Length > 0 ? name : null) { RepeatOn = days };
        return (new ClockAction($"Set an alarm for {shown}?", details, "Set alarm", (app, token) => app.SetAlarmAsync(alarm, token)), null);
    }
}

/// <summary><c>start_timer</c>: adds a timer to the Windows Clock app and starts it.</summary>
/// <param name="clock">The Clock app.</param>
/// <param name="time">The PC's clock, for a timer that is asked for by the time it ends at ("a timer for 9:18 pm").</param>
public sealed class StartTimerTool(IClockApp? clock, TimeProvider? time = null) : ClockTool(clock)
{
    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        StartTimer,
        "Start a countdown timer in the Windows Clock app whenever the user says timer: it starts it itself, so do not open an app. For a length give hours, " +
        "minutes or seconds. For a timer until a time of day (9:18 pm, or 4:20) give only until, as the user said it: never work the length out, never ask am " +
        "or pm, and never use set_alarm for a timer.",
        [
            new ToolParameter("hours", ToolParameterType.Integer, "Hours, or leave it out.", Required: false, Minimum: 0, Maximum: 99),
            new ToolParameter("minutes", ToolParameterType.Integer, "Minutes, or leave it out.", Required: false, Minimum: 0, Maximum: 5999),
            new ToolParameter("seconds", ToolParameterType.Integer, "Seconds, or leave it out.", Required: false, Minimum: 0, Maximum: 359999),
            new ToolParameter("until", ToolParameterType.String, "The time of day it ends at, as the user said it, such as 9:18 pm or 4:20.", Required: false, MaxLength: 20),
            new ToolParameter("name", ToolParameterType.String, "What it is for, if the user said, such as Tea.", Required: false, MaxLength: MaxNameLength),
        ],
        RiskLevel.SideEffect,
        timeout: TimeSpan.FromSeconds(45));

    /// <inheritdoc/>
    protected override (ClockAction? Action, ToolResult? Refusal) Read(ToolCall call, JsonElement arguments, ToolContext context)
    {
        var total = TimeSpan.FromHours(Math.Max(0, Number(arguments, "hours") ?? 0))
            + TimeSpan.FromMinutes(Math.Max(0, Number(arguments, "minutes") ?? 0))
            + TimeSpan.FromSeconds(Math.Max(0, Number(arguments, "seconds") ?? 0));

        // A timer asked for by when it ends runs from now until then: the length is the PC's sum and not the model's, which once made a timer of
        // 17 hours 51 minutes of one. A time that has passed today is tomorrow's.
        var until = Text(arguments, "until");
        TimeOnly? ends = null;
        if (until.Length > 0)
        {
            if (ClockTimes.ParseTime(until) is not { } read)
            {
                return (null, Invalid(call, "That is not a time of day. Give until as HH:MM in 24 hours, such as 21:18 for 9:18 pm, or give hours, minutes or seconds."));
            }

            // "A timer for 4:20" at four in the afternoon ends at 4:20 pm, twenty minutes on: when the user did not say which half of the day, it is
            // the next time the clock shows that time. When they did (am, pm, in the morning, or a 24-hour time), it is what they said.
            var now = (time ?? TimeProvider.System).GetLocalNow();
            var end = ClockTimes.HalfSaid(context.Request) switch
            {
                DayHalf.Morning => new TimeOnly(read.Hour % 12, read.Minute),
                DayHalf.Afternoon => new TimeOnly((read.Hour % 12) + 12, read.Minute),
                DayHalf.Given => read,
                _ => ClockTimes.Next(read, TimeOnly.FromTimeSpan(now.TimeOfDay)),
            };
            var left = end.ToTimeSpan() - now.TimeOfDay;
            total = left <= TimeSpan.Zero ? left + TimeSpan.FromDays(1) : left;

            // The Clock app counts whole seconds, and a timer that ends at 21:18 should not ring at 21:17:59.
            total = TimeSpan.FromSeconds(Math.Ceiling(total.TotalSeconds));
            ends = end;
        }

        if (total < TimeSpan.FromSeconds(1) || total >= TimeSpan.FromHours(100))
        {
            return (null, Invalid(call, "Give how long the timer runs: hours, minutes or seconds, from one second to under a hundred hours, or until with the time it ends at."));
        }

        var name = Text(arguments, "name");
        var shown = ClockTimes.Describe(total);
        var details = new List<ConfirmationDetail> { new("Length", shown) };
        if (ends is { } at)
        {
            details.Insert(0, new ConfirmationDetail("Ends at", at.ToString("t", CultureInfo.CurrentCulture)));
        }

        if (name.Length > 0)
        {
            details.Add(new ConfirmationDetail("Name", name));
        }

        var question = ends is { } then ? $"Start a timer until {then.ToString("t", CultureInfo.CurrentCulture)}?" : $"Start a timer of {shown}?";
        return (new ClockAction(question, details, "Start timer", (app, token) => app.StartTimerAsync(total, name.Length > 0 ? name : null, token)), null);
    }
}

/// <summary><c>control_timer</c>: stops, pauses, resumes or restarts a timer that is in the Windows Clock app.</summary>
public sealed class ControlTimerTool(IClockApp? clock) : ClockTool(clock)
{
    private static readonly string[] Actions = ["stop", "pause", "resume", "restart"];

    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        ControlTimer,
        "Stop, pause, resume or restart a countdown timer in the Windows Clock app, such as the one you started with start_timer. Use it whenever the user asks to stop, " +
        "cancel, end, pause, resume or restart a timer: it works the timer itself, so do not open the Clock app or say it cannot be done. stop ends the countdown so it " +
        "will not ring; pause holds it where it is.",
        [
            new ToolParameter("action", ToolParameterType.String, "What to do: stop (cancel it), pause, resume (carry on), or restart (from its full length).", Choices: Actions),
            new ToolParameter(
                "name", ToolParameterType.String, "The timer's name, if the user said which one, such as Tea. Leave it out for the timer that is running, or the one you started.",
                Required: false, MaxLength: MaxNameLength),
        ],
        RiskLevel.SideEffect,
        timeout: TimeSpan.FromSeconds(45));

    /// <inheritdoc/>
    protected override (ClockAction? Action, ToolResult? Refusal) Read(ToolCall call, JsonElement arguments, ToolContext context)
    {
        var action = Text(arguments, "action").ToLowerInvariant() switch
        {
            "stop" or "cancel" or "end" or "delete" or "remove" or "dismiss" => TimerAction.Stop,
            "pause" or "hold" => TimerAction.Pause,
            "resume" or "continue" or "start" => TimerAction.Resume,
            "restart" or "reset" => TimerAction.Restart,
            _ => (TimerAction?)null,
        };
        if (action is not { } wanted)
        {
            return (null, Invalid(call, "Say what to do with the timer: stop, pause, resume or restart."));
        }

        var name = Text(arguments, "name");
        var (question, button) = wanted switch
        {
            TimerAction.Stop => ("Stop the timer?", "Stop timer"),
            TimerAction.Pause => ("Pause the timer?", "Pause"),
            TimerAction.Resume => ("Carry on with the timer?", "Resume"),
            _ => ("Start the timer again from its full length?", "Restart"),
        };
        var details = new List<ConfirmationDetail> { new("Timer", name.Length > 0 ? name : "The one that is running, or the one started last") };
        return (new ClockAction(question, details, button, (app, token) => app.ControlTimerAsync(wanted, name.Length > 0 ? name : null, token)), null);
    }
}

/// <summary><c>stopwatch</c>: starts, pauses or resets the Windows Clock app's stopwatch.</summary>
public sealed class StopwatchTool(IClockApp? clock) : ClockTool(clock)
{
    private static readonly string[] Actions = ["start", "pause", "reset"];

    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        Stopwatch,
        "Start, pause or reset the stopwatch of the Windows Clock app, which counts up. Use it whenever the user asks to time something with a stopwatch: it works the " +
        "stopwatch itself, so do not open the Clock app or tell the user how to do it. For a countdown timer use start_timer and control_timer instead.",
        [
            new ToolParameter("action", ToolParameterType.String, "What to do: start (or carry on), pause, or reset to zero.", Choices: Actions),
        ],
        RiskLevel.SideEffect,
        timeout: TimeSpan.FromSeconds(45));

    /// <inheritdoc/>
    protected override (ClockAction? Action, ToolResult? Refusal) Read(ToolCall call, JsonElement arguments, ToolContext context)
    {
        var action = Text(arguments, "action").ToLowerInvariant() switch
        {
            "start" or "resume" or "continue" => StopwatchAction.Start,
            "pause" or "stop" => StopwatchAction.Pause,
            "reset" or "clear" => StopwatchAction.Reset,
            _ => (StopwatchAction?)null,
        };
        if (action is not { } wanted)
        {
            return (null, Invalid(call, "Say what to do with the stopwatch: start, pause or reset."));
        }

        var (question, button) = wanted switch
        {
            StopwatchAction.Start => ("Start the stopwatch?", "Start"),
            StopwatchAction.Pause => ("Pause the stopwatch?", "Pause"),
            _ => ("Reset the stopwatch to zero?", "Reset"),
        };
        return (new ClockAction(question, [new ConfirmationDetail("Stopwatch", button)], button, (app, token) => app.ControlStopwatchAsync(wanted, token)), null);
    }
}

/// <summary><c>start_focus_session</c>: starts a focus session in the Windows Clock app, which turns Windows' focus (do not disturb) on while it lasts.</summary>
public sealed class StartFocusSessionTool(IClockApp? clock) : ClockTool(clock)
{
    /// <summary>How long a focus session is when the user does not say.</summary>
    public const int DefaultMinutes = 30;

    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        StartFocusSession,
        "Start a focus session in the Windows Clock app: a set number of minutes with notifications silenced until it ends. Use it when the user asks to focus, for focus " +
        "mode, for a pomodoro, or for quiet for a length of time: it starts the session itself, so do not open the Clock app or Settings. To only turn Do not disturb on " +
        "or off, with no length of time, use set_do_not_disturb.",
        [
            new ToolParameter("minutes", ToolParameterType.Integer, "How long to focus, in minutes, such as 25. Leave it out for 30 minutes.", Required: false, Minimum: 5, Maximum: 240),
            new ToolParameter("skip_breaks", ToolParameterType.Boolean, "True to run the session without breaks. Leave it out otherwise.", Required: false),
        ],
        RiskLevel.SideEffect,
        timeout: TimeSpan.FromSeconds(45));

    /// <inheritdoc/>
    protected override (ClockAction? Action, ToolResult? Refusal) Read(ToolCall call, JsonElement arguments, ToolContext context)
    {
        var minutes = Math.Clamp(Number(arguments, "minutes") ?? DefaultMinutes, 5, 240);
        var skip = Flag(arguments, "skip_breaks");
        var details = new List<ConfirmationDetail> { new("Length", minutes + " minutes"), new("Notifications", "Quiet until it ends") };
        if (skip)
        {
            details.Add(new ConfirmationDetail("Breaks", "None"));
        }

        return (new ClockAction($"Start a focus session of {minutes} minutes?", details, "Start focus", (app, token) => app.StartFocusSessionAsync(minutes, skip, token)), null);
    }
}

/// <summary>Which half of the day a request says a time is in.</summary>
public enum DayHalf
{
    /// <summary>The request does not say: "4:20".</summary>
    NotSaid,

    /// <summary>Before noon: "4:20 am", "in the morning".</summary>
    Morning,

    /// <summary>From noon on: "4:20 pm", "this afternoon", "in the evening".</summary>
    Afternoon,

    /// <summary>The request says it in a way that is read with the time itself: a 24-hour time ("16:20"), noon, midnight, tonight, or more than one time.</summary>
    Given,
}

/// <summary>Reads the times and days the model writes for the clock tools, by fixed rules.</summary>
public static partial class ClockTimes
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:\d\s?(?<half>a\.?m\.?|p\.?m\.?)(?![a-z]))|(?:^\s*(?<half>a\.?m\.?|p\.?m\.?)\s*[.!]?\s*$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex HalfMark();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\d)(?:1[3-9]|2[0-3]|0\d)[:.]\d\d(?!\d)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex TwentyFourHours();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![a-z])(noon|midday|midnight|tonight|night)(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex GivenWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![a-z])(morning|afternoon|evening)(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex HalfWords();

    /// <summary>
    /// Which half of the day the user's own words put a time in: am or pm written after a number (or as the whole answer to "am or pm?"), morning,
    /// afternoon or evening. A 24-hour time, noon, midnight and tonight are read with the time itself, and so are words that say both halves.
    /// </summary>
    public static DayHalf HalfSaid(string? request)
    {
        var text = request ?? string.Empty;
        if (text.Length == 0)
        {
            return DayHalf.NotSaid;
        }

        var (morning, afternoon) = (false, false);
        foreach (System.Text.RegularExpressions.Match match in HalfMark().Matches(text))
        {
            var isMorning = match.Groups["half"].Value.StartsWith("a", StringComparison.OrdinalIgnoreCase);
            morning |= isMorning;
            afternoon |= !isMorning;
        }

        foreach (System.Text.RegularExpressions.Match match in HalfWords().Matches(text))
        {
            var isMorning = match.Value.Equals("morning", StringComparison.OrdinalIgnoreCase);
            morning |= isMorning;
            afternoon |= !isMorning;
        }

        if (morning != afternoon)
        {
            return morning ? DayHalf.Morning : DayHalf.Afternoon;
        }

        return morning || TwentyFourHours().IsMatch(text) || GivenWords().IsMatch(text) ? DayHalf.Given : DayHalf.NotSaid;
    }

    /// <summary>
    /// The next time the clock shows <paramref name="time"/> on a 12-hour face after <paramref name="now"/>: of 4:20 in the morning and 4:20 in the
    /// afternoon, the one that comes first. At four in the afternoon "4:20" is 16:20; at five it is 04:20.
    /// </summary>
    public static TimeOnly Next(TimeOnly time, TimeOnly now)
    {
        var early = new TimeOnly(time.Hour % 12, time.Minute, time.Second);
        var late = early.AddHours(12);
        static TimeSpan Until(TimeOnly then, TimeOnly from)
        {
            var left = then - from;
            return left <= TimeSpan.Zero ? left + TimeSpan.FromDays(1) : left;
        }

        return Until(early, now) <= Until(late, now) ? early : late;
    }

    private static readonly string[] TimeFormats =
    [
        "H:mm", "HH:mm", "H.mm", "HH.mm", "H:mm:ss", "h:mm tt", "h:mmtt", "h tt", "htt", "hh:mm tt", "hh:mmtt", "h.mm tt", "h.mmtt",
    ];

    /// <summary>
    /// The time of day <paramref name="text"/> says: 24-hour <c>16:00</c> or <c>7.30</c>, or with AM and PM (<c>4pm</c>, <c>4:30 PM</c>, <c>4 p.m.</c>), or a bare hour
    /// (<c>16</c>), or <c>noon</c> and <c>midnight</c>. <see langword="null"/> for anything else.
    /// </summary>
    public static TimeOnly? ParseTime(string? text)
    {
        var tidy = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        tidy = tidy.Replace("A.M.", "AM", StringComparison.Ordinal).Replace("P.M.", "PM", StringComparison.Ordinal)
            .Replace("A.M", "AM", StringComparison.Ordinal).Replace("P.M", "PM", StringComparison.Ordinal);
        switch (tidy)
        {
            case "":
                return null;
            case "NOON" or "MIDDAY":
                return new TimeOnly(12, 0);
            case "MIDNIGHT":
                return new TimeOnly(0, 0);
        }

        if (TimeOnly.TryParseExact(tidy, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return time;
        }

        return int.TryParse(tidy, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) && hour is >= 0 and <= 23 ? new TimeOnly(hour, 0) : null;
    }

    /// <summary>
    /// The days <paramref name="text"/> names, in the order of the week: names and their first three letters, separated by commas, spaces or "and"; or
    /// <c>weekdays</c>, <c>weekends</c>, <c>every day</c> (<c>daily</c>). Empty for nothing given or <c>once</c>, and <see langword="null"/> when a word is not a day.
    /// </summary>
    public static IReadOnlyList<DayOfWeek>? ParseDays(string? text)
    {
        var tidy = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (tidy.Length == 0 || tidy is "once" or "none" or "no" or "never")
        {
            return [];
        }

        var days = new SortedSet<DayOfWeek>();
        foreach (var word in tidy.Split([',', ' ', ';', '/', '&', '+'], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (word)
            {
                case "and" or "every" or "each" or "on":
                    continue;
                case "day" or "days" or "daily" or "everyday":
                    days.UnionWith(Enum.GetValues<DayOfWeek>());
                    continue;
                case "weekdays" or "weekday" or "workdays":
                    days.UnionWith([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
                    continue;
                case "weekends" or "weekend":
                    days.UnionWith([DayOfWeek.Saturday, DayOfWeek.Sunday]);
                    continue;
            }

            var name = word.TrimEnd('s', '.');
            var day = Enum.GetValues<DayOfWeek>().Cast<DayOfWeek?>().FirstOrDefault(candidate =>
            {
                var full = candidate!.Value.ToString().ToLowerInvariant();
                return name == full || (name.Length >= 3 && full.StartsWith(name, StringComparison.Ordinal)) || name == full.TrimEnd('s');
            });
            if (day is null)
            {
                return null;
            }

            days.Add(day.Value);
        }

        return [.. days];
    }

    /// <summary>A length of time in words: "1 hour 5 minutes", "90 seconds".</summary>
    public static string Describe(TimeSpan duration)
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
        return parts.Count == 0 ? "0 seconds" : string.Join(' ', parts);
    }
}
