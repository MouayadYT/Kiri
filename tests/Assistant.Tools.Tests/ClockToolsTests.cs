using System.Text.Json;
using Assistant.Core.Clock;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Tools;
using Assistant.Tools.Clock;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The clock tools (PROJECT_SPEC §4.8): an alarm, a timer, the stopwatch and a focus session are set in the Windows Clock app itself, through
/// <see cref="IClockApp"/>, after the user says yes to exactly what will be set. Here the Clock app is a recorder; the real one is worked by
/// <c>WindowsClockApp</c>, which the release checklist rehearses.
/// </summary>
public sealed class ClockToolsTests
{
    private sealed class RecordingClock : IClockApp
    {
        public bool IsInstalled { get; init; } = true;

        public bool Works { get; init; } = true;

        public List<string> Done { get; } = [];

        public ClockAlarm? Alarm { get; private set; }

        public Task<ClockResult> SetAlarmAsync(ClockAlarm alarm, CancellationToken cancellationToken = default)
        {
            Alarm = alarm;
            return Answer($"alarm {alarm.Time:HH\\:mm} {alarm.Name} [{string.Join(',', alarm.RepeatOn)}]");
        }

        public Task<ClockResult> StartTimerAsync(TimeSpan duration, string? name = null, CancellationToken cancellationToken = default) =>
            Answer($"timer {duration} {name}");

        public Task<ClockResult> ControlTimerAsync(TimerAction action, string? name = null, CancellationToken cancellationToken = default) =>
            Answer($"timer {action} {name ?? "(the one meant)"}");

        public Task<ClockResult> ControlStopwatchAsync(StopwatchAction action, CancellationToken cancellationToken = default) => Answer($"stopwatch {action}");

        public Task<ClockResult> StartFocusSessionAsync(int minutes, bool skipBreaks = false, CancellationToken cancellationToken = default) =>
            Answer($"focus {minutes} {skipBreaks}");

        private Task<ClockResult> Answer(string what)
        {
            Done.Add(what);
            return Task.FromResult(Works ? ClockResult.Ok("It is set.") : ClockResult.Failed("The Clock app did not open, so nothing was set."));
        }
    }

    private sealed class AllowAll : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(capability, PermissionDecisionReason.Granted));
    }

    private static ToolCall Call(string name, string arguments) => new("c1", name, arguments);

    private static ITool[] Tools(IClockApp? clock) =>
        [new SetAlarmTool(clock), new StartTimerTool(clock), new ControlTimerTool(clock), new StopwatchTool(clock), new StartFocusSessionTool(clock)];

    private static (ToolExecutor Executor, FakeConfirmation Asked) Executor(IClockApp clock, bool confirm = true)
    {
        var asked = new FakeConfirmation(confirm);
        return (new ToolExecutor(Tools(clock), asked, new AllowAll()), asked);
    }

    private static ToolContext About(string request) => new(Guid.NewGuid(), request);

    [Theory]
    [InlineData("""{"time":"16:00"}""", "alarm 16:00  []")]
    [InlineData("""{"time":"4pm"}""", "alarm 16:00  []")]
    [InlineData("""{"time":"4:30 PM","name":"Take the bread out"}""", "alarm 16:30 Take the bread out []")]
    [InlineData("""{"time":"7.05 a.m.","days":"weekdays"}""", "alarm 07:05  [Monday,Tuesday,Wednesday,Thursday,Friday]")]
    [InlineData("""{"time":"noon","days":"sat and sun"}""", "alarm 12:00  [Sunday,Saturday]")]
    [InlineData("""{"time":"23","days":"every day"}""", "alarm 23:00  [Sunday,Monday,Tuesday,Wednesday,Thursday,Friday,Saturday]")]
    public async Task AnAlarmIsSetForTheTimeAndDaysThatWereAsked_AfterTheUserSaysYes(string arguments, string expected)
    {
        var clock = new RecordingClock();
        var (executor, asked) = Executor(clock);

        var result = await executor.ExecuteAsync(Call(ClockTool.SetAlarm, arguments), About("set an alarm"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([expected], clock.Done);

        // The user was shown the time as it will be set, and it is the Clock app that is changed.
        var shown = Assert.Single(asked.Shown);
        Assert.Equal(ConfirmationKind.ChangeSystem, shown.Kind);
        Assert.StartsWith("Set an alarm for ", shown.Title, StringComparison.Ordinal);
        Assert.Contains(shown.Details, detail => detail.Label == "Time");
    }

    [Fact]
    public async Task AnAlarmTheUserDoesNotAllowIsNotSet()
    {
        var clock = new RecordingClock();
        var (executor, asked) = Executor(clock, confirm: false);

        var result = await executor.ExecuteAsync(Call(ClockTool.SetAlarm, """{"time":"16:00"}"""), About("set an alarm for 4 pm"));

        Assert.NotEqual(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(1, asked.Asked);
        Assert.Empty(clock.Done);
    }

    [Theory]
    [InlineData(ClockTool.SetAlarm, """{"time":"later"}""")]
    [InlineData(ClockTool.SetAlarm, """{"time":"25:00"}""")]
    [InlineData(ClockTool.SetAlarm, """{"time":"16:00","days":"someday"}""")]
    [InlineData(ClockTool.StartTimer, """{}""")]
    [InlineData(ClockTool.StartTimer, """{"minutes":0,"seconds":0}""")]
    public async Task WhatCannotBeReadIsToldToTheModel_AndNobodyIsAsked(string tool, string arguments)
    {
        var clock = new RecordingClock();
        var (executor, asked) = Executor(clock);

        var result = await executor.ExecuteAsync(Call(tool, arguments), About("set an alarm or a timer"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(clock.Done);
    }

    [Theory]
    [InlineData("""{"minutes":10}""", "timer 00:10:00 ", "10 minutes")]
    [InlineData("""{"hours":1,"minutes":30,"name":"Roast"}""", "timer 01:30:00 Roast", "1 hour 30 minutes")]
    [InlineData("""{"seconds":90}""", "timer 00:01:30 ", "1 minute 30 seconds")]
    public async Task ATimerIsStartedForTheLengthThatWasAsked(string arguments, string expected, string shownLength)
    {
        var clock = new RecordingClock();
        var (executor, asked) = Executor(clock);

        var result = await executor.ExecuteAsync(Call(ClockTool.StartTimer, arguments), About("start a timer"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([expected], clock.Done);
        Assert.Equal($"Start a timer of {shownLength}?", Assert.Single(asked.Shown).Title);
    }

    // "Stop the timer" once had no tool of its own: the model reached for the stopwatch, or said it could not be done.
    [Theory]
    [InlineData("""{"action":"stop"}""", "timer Stop (the one meant)", "Stop the timer?")]
    [InlineData("""{"action":"pause","name":"Tea"}""", "timer Pause Tea", "Pause the timer?")]
    [InlineData("""{"action":"resume"}""", "timer Resume (the one meant)", "Carry on with the timer?")]
    [InlineData("""{"action":"restart","name":"  Eggs  "}""", "timer Restart Eggs", "Start the timer again from its full length?")]
    public async Task ATimerIsStoppedPausedCarriedOnWithAndStartedAgain_AfterTheUserSaysYes(string arguments, string expected, string question)
    {
        var clock = new RecordingClock();
        var (executor, asked) = Executor(clock);

        var result = await executor.ExecuteAsync(Call(ClockTool.ControlTimer, arguments), About("stop the timer"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([expected], clock.Done);
        var shown = Assert.Single(asked.Shown);
        Assert.Equal(question, shown.Title);
        Assert.Contains(shown.Details, detail => detail.Label == "Timer");
    }

    [Fact]
    public async Task ATimerIsLeftAloneWhenTheUserSaysNo_AndWhenWhatToDoWithItCannotBeRead()
    {
        var clock = new RecordingClock();
        var (refusing, asked) = Executor(clock, confirm: false);
        Assert.NotEqual(ToolResultStatus.Succeeded, (await refusing.ExecuteAsync(Call(ClockTool.ControlTimer, """{"action":"stop"}"""), About("stop the timer"))).Status);
        Assert.Equal(1, asked.Asked);

        var (executor, unasked) = Executor(clock);
        Assert.Equal(ToolResultStatus.Failed, (await executor.ExecuteAsync(Call(ClockTool.ControlTimer, """{"action":"explode"}"""), About("stop the timer"))).Status);
        Assert.Equal(0, unasked.Asked);
        Assert.Empty(clock.Done);
    }

    [Theory]
    [InlineData("start", "stopwatch Start")]
    [InlineData("pause", "stopwatch Pause")]
    [InlineData("reset", "stopwatch Reset")]
    public async Task TheStopwatchIsStartedPausedAndReset(string action, string expected)
    {
        var clock = new RecordingClock();
        var (executor, _) = Executor(clock);

        var result = await executor.ExecuteAsync(Call(ClockTool.Stopwatch, $$"""{"action":"{{action}}"}"""), About("stopwatch"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([expected], clock.Done);
    }

    [Theory]
    [InlineData("""{}""", "focus 30 False")]
    [InlineData("""{"minutes":25,"skip_breaks":true}""", "focus 25 True")]
    public async Task FocusModeIsAFocusSessionOfTheClockApp_HalfAnHourWhenNoLengthIsSaid(string arguments, string expected)
    {
        var clock = new RecordingClock();
        var (executor, asked) = Executor(clock);

        var result = await executor.ExecuteAsync(Call(ClockTool.StartFocusSession, arguments), About("turn on focus mode"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([expected], clock.Done);
        Assert.Contains(Assert.Single(asked.Shown).Details, detail => detail.Label == "Notifications");
    }

    [Fact]
    public async Task AClockAppThatDoesNotDoItIsSaidSo_AndTheModelIsToldNotToSayItWasSet()
    {
        var clock = new RecordingClock { Works = false };
        var (executor, _) = Executor(clock);

        var result = await executor.ExecuteAsync(Call(ClockTool.StartTimer, """{"minutes":5}"""), About("timer for 5 minutes"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out _, out var message));
        Assert.Contains("The Clock app did not open", message, StringComparison.Ordinal);
        Assert.Contains("do not say it was set", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("set an alarm for 4 pm", true)]
    [InlineData("Wake me at seven", true)]
    [InlineData("start a 10 minute timer", true)]
    [InlineData("stop the timer", true)]
    [InlineData("time this with the stopwatch", true)]
    [InlineData("turn on focus mode", true)]
    [InlineData("do not disturb me for an hour", true)]
    [InlineData("what is the capital of France", false)]
    [InlineData("open my resume", false)]
    public void TheClockToolsAreOfferedOnlyForRequestsAboutTheClock(string request, bool offered)
    {
        var context = About(request);
        Assert.All(Tools(new RecordingClock()), tool => Assert.Equal(offered, tool.IsOffered(context)));
    }

    [Fact]
    public void AConversationThatBeganWithTheClockKeepsItsTools_AndOneWithoutTheClockAppHasNone()
    {
        var conversation = Guid.NewGuid();
        var tools = Tools(new RecordingClock());
        Assert.All(tools, tool => Assert.True(tool.IsOffered(new ToolContext(conversation, "set a timer for ten minutes"))));

        // "Make it fifteen" says nothing of a clock, and is still about the timer.
        Assert.All(tools, tool => Assert.True(tool.IsOffered(new ToolContext(conversation, "make it fifteen"))));

        Assert.All(Tools(new RecordingClock { IsInstalled = false }), tool => Assert.False(tool.IsOffered(About("set an alarm"))));
        Assert.All(Tools(null), tool => Assert.False(tool.IsOffered(About("set an alarm"))));
    }

    [Fact]
    public void EveryClockToolChangesSomething_SoEachCallIsConfirmed_AndItsSchemaIsWellFormed()
    {
        foreach (var tool in Tools(new RecordingClock()))
        {
            Assert.Equal(RiskLevel.SideEffect, tool.Definition.RiskLevel);
            using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("16:00", 16, 0)]
    [InlineData("7:5", null, null)]
    [InlineData("07:30", 7, 30)]
    [InlineData("12am", 0, 0)]
    [InlineData("12 PM", 12, 0)]
    [InlineData("midnight", 0, 0)]
    [InlineData("4 o'clock", null, null)]
    [InlineData("", null, null)]
    public void ATimeOfDayIsReadByFixedRules(string text, int? hour, int? minute)
    {
        var time = ClockTimes.ParseTime(text);
        Assert.Equal(hour is null ? null : new TimeOnly(hour.Value, minute!.Value), time);
    }
}
