using Assistant.Core.Clock;
using Assistant.Core.Contracts;
using Assistant.Core.Displays;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.Tools;
using Assistant.Tools.Clock;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// What time a timer "for 4:20" ends at (the next time the clock shows it, unless the user said which half of the day), that a conversation about a
/// timer stays one, and where on its display the Clock app's window is put ("move it down 30%").
/// </summary>
public sealed class ClockTimeAndPlaceTests
{
    // A clock that stands at a time of day, in a time zone with no offset, so that local time is the time given.
    private sealed class At(int hour, int minute) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(2026, 10, 8, hour, minute, 0, TimeSpan.Zero);
    }

    private sealed class RecordingClock : IClockApp
    {
        public List<string> Done { get; } = [];

        public ClockSpot? Spot { get; set; }

        public int Placed { get; private set; }

        public bool IsInstalled => true;

        public Task<ClockResult> SetAlarmAsync(ClockAlarm alarm, CancellationToken cancellationToken = default) => Answer($"alarm {alarm.Time:HH\\:mm}");

        public Task<ClockResult> StartTimerAsync(TimeSpan duration, string? name = null, CancellationToken cancellationToken = default) => Answer($"timer {duration}");

        public Task<ClockResult> ControlTimerAsync(TimerAction action, string? name = null, CancellationToken cancellationToken = default) => Answer("control");

        public Task<ClockResult> ControlStopwatchAsync(StopwatchAction action, CancellationToken cancellationToken = default) => Answer("stopwatch");

        public Task<ClockResult> StartFocusSessionAsync(int minutes, bool skipBreaks = false, CancellationToken cancellationToken = default) => Answer("focus");

        public Task<ClockResult> ApplyPlaceAsync(CancellationToken cancellationToken = default)
        {
            Placed++;
            return Task.FromResult(ClockResult.Ok("The Clock window was moved there."));
        }

        public Task<ClockSpot?> ReadSpotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Spot);

        private Task<ClockResult> Answer(string what)
        {
            Done.Add(what);
            return Task.FromResult(ClockResult.Ok("It is set."));
        }
    }

    private static ToolCall Call(string name, string arguments) => new("c1", name, arguments);

    private static (ToolExecutor Executor, FakeConfirmation Asked, RecordingClock Clock) Clock(int hour, int minute)
    {
        var clock = new RecordingClock();
        var asked = new FakeConfirmation(true);
        return (new ToolExecutor([new SetAlarmTool(clock), new StartTimerTool(clock, new At(hour, minute))], asked, new FakePermissions(true)), asked, clock);
    }

    // ---- which half of the day ----

    [Theory]
    [InlineData("set a timer for 4:20", DayHalf.NotSaid)]
    [InlineData("timer until 12:20 please", DayHalf.NotSaid)]
    [InlineData("I am late, set a timer for 4.20", DayHalf.NotSaid)] // "am" that is not after a number is a word.
    [InlineData("a timer for 4:20 pm", DayHalf.Afternoon)]
    [InlineData("a timer for 4:20PM", DayHalf.Afternoon)]
    [InlineData("I meant 12:22 am", DayHalf.Morning)]
    [InlineData("12:22 a.m.", DayHalf.Morning)]
    [InlineData("pm", DayHalf.Afternoon)] // The whole answer to "am or pm?".
    [InlineData("A.M.", DayHalf.Morning)]
    [InlineData("at 7 in the morning", DayHalf.Morning)]
    [InlineData("this evening at 6", DayHalf.Afternoon)]
    [InlineData("a timer for 16:20", DayHalf.Given)]
    [InlineData("a timer for 04:20", DayHalf.Given)]
    [InlineData("until midnight", DayHalf.Given)]
    [InlineData("from 9 am to 5 pm", DayHalf.Given)] // Both: the time is read as it is written.
    [InlineData(null, DayHalf.NotSaid)]
    public void WhichHalfOfTheDayTheUserSaidIsReadFromTheirOwnWords(string? request, DayHalf expected) =>
        Assert.Equal(expected, ClockTimes.HalfSaid(request));

    [Theory]
    [InlineData(4, 20, 16, 0, 16, 20)] // At four in the afternoon, 4:20 is twenty minutes on.
    [InlineData(4, 20, 17, 0, 4, 20)] // At five it has passed: the next 4:20 is in the morning.
    [InlineData(12, 20, 16, 0, 0, 20)] // 12:20 at four in the afternoon is twenty past midnight.
    [InlineData(12, 20, 11, 0, 12, 20)]
    [InlineData(16, 20, 3, 0, 4, 20)] // However the model wrote it, it is a time on a 12-hour face.
    [InlineData(4, 20, 16, 20, 4, 20)] // The time it is now is not "next".
    public void TheNextTimeTheClockShowsATimeIsTheNearerOfItsTwoReadings(int hour, int minute, int nowHour, int nowMinute, int expectedHour, int expectedMinute) =>
        Assert.Equal(new TimeOnly(expectedHour, expectedMinute), ClockTimes.Next(new TimeOnly(hour, minute), new TimeOnly(nowHour, nowMinute)));

    [Theory]
    [InlineData("set a timer for 4:20", "4:20", "timer 00:20:00")]
    [InlineData("set a timer for 4:20", "04:20", "timer 00:20:00")] // The model wrote a morning the user did not say.
    [InlineData("set a timer for 12:20", "12:20", "timer 08:20:00")]
    [InlineData("set a timer for 4:20 am", "4:20", "timer 12:20:00")] // They said which, so it is that.
    [InlineData("set a timer for 4:20 am", "16:20", "timer 12:20:00")] // Even when the model wrote the other.
    [InlineData("set a timer for 9:18 pm", "9:18 pm", "timer 05:18:00")]
    [InlineData("a timer until 16:45", "16:45", "timer 00:45:00")]
    [InlineData("a timer until 03:00", "03:00", "timer 11:00:00")] // A 24-hour time is read as it is written.
    public async Task ATimerForATimeOfDayEndsTheNextTimeItIsThatTime_UnlessTheUserSaidWhichHalfOfTheDay(string request, string until, string expected)
    {
        var (executor, asked, clock) = Clock(16, 0);

        var result = await executor.ExecuteAsync(Call(ClockTool.StartTimer, System.Text.Json.JsonSerializer.Serialize(new { until })), new ToolContext(Guid.NewGuid(), request));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([expected], clock.Done);
        var shown = Assert.Single(asked.Shown);
        Assert.StartsWith("Start a timer until ", shown.Title, StringComparison.Ordinal);
        Assert.Contains(shown.Details, detail => detail.Label == "Ends at");
    }

    [Fact]
    public void TheModelIsToldToGiveTheTimeAsTheUserSaidIt_AndNotToAskAmOrPm()
    {
        var definition = new StartTimerTool(new RecordingClock()).Definition;

        Assert.Contains("never ask am or pm", definition.Description, StringComparison.Ordinal);
        Assert.Contains("as the user said it", definition.InputSchemaJson, StringComparison.Ordinal);
    }

    // ---- a timer is not an alarm ----

    [Fact]
    public async Task InAConversationAboutATimerATimeOfDayDoesNotBecomeAnAlarm()
    {
        var (executor, asked, clock) = Clock(16, 0);
        var conversation = Guid.NewGuid();
        var tools = new ITool[] { new SetAlarmTool(clock), new StartTimerTool(clock, new At(16, 0)) };

        // The tools are offered for each request, which is how the conversation is known to be about a timer.
        Assert.All(tools, tool => Assert.True(tool.IsOffered(new ToolContext(conversation, "set a timer for 12:20"))));
        var corrected = new ToolContext(conversation, "I meant 12:22 am");
        Assert.All(tools, tool => Assert.True(tool.IsOffered(corrected)));

        // The model reaches for the alarm: it is told that a timer was meant, and nobody is asked.
        var alarm = await executor.ExecuteAsync(Call(ClockTool.SetAlarm, """{"time":"00:22"}"""), corrected);
        Assert.Equal(ToolResultStatus.Failed, alarm.Status);
        Assert.True(ToolErrors.TryRead(alarm.OutputJson, out _, out var message));
        Assert.Contains("a timer, not an alarm", message, StringComparison.Ordinal);
        Assert.Contains("start_timer", message, StringComparison.Ordinal);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(clock.Done);

        // The timer then runs until 12:22 in the morning: eight hours and twenty-two minutes from four in the afternoon.
        var timer = await executor.ExecuteAsync(Call(ClockTool.StartTimer, """{"until":"12:22 am"}"""), corrected);
        Assert.Equal(ToolResultStatus.Succeeded, timer.Status);
        Assert.Equal(["timer 08:22:00"], clock.Done);

        // When they do ask for an alarm, they get one.
        var asksForAlarm = new ToolContext(conversation, "now set an alarm for 7");
        Assert.All(tools, tool => Assert.True(tool.IsOffered(asksForAlarm)));
        Assert.Equal(ToolResultStatus.Succeeded, (await executor.ExecuteAsync(Call(ClockTool.SetAlarm, """{"time":"07:00"}"""), asksForAlarm)).Status);
        Assert.Equal("alarm 07:00", clock.Done[^1]);
    }

    [Theory]
    [InlineData("set a timer for 9:18 pm", """{"time":"21:18"}""")]
    [InlineData("wake me at seven", """{"time":"07:00","name":"timer"}""")] // An alarm called "timer" is a timer that went the wrong way.
    public async Task AnAlarmIsNotSetForSomethingTheUserCalledATimer(string request, string arguments)
    {
        var (executor, asked, clock) = Clock(16, 0);
        var context = new ToolContext(Guid.NewGuid(), request);
        _ = new SetAlarmTool(clock).IsOffered(context);

        var result = await executor.ExecuteAsync(Call(ClockTool.SetAlarm, arguments), context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(clock.Done);
    }

    [Theory]
    [InlineData("set an alarm for 7 pm", "07:00", "alarm 19:00")] // The half of the day the user said wins over what was worked out from it.
    [InlineData("set an alarm for 7 am", "19:00", "alarm 07:00")]
    [InlineData("set an alarm for 7", "07:00", "alarm 07:00")] // An alarm is not moved to the nearest time: it is usually for the morning.
    [InlineData("set an alarm for 19:30", "19:30", "alarm 19:30")]
    public async Task AnAlarmIsInTheHalfOfTheDayTheUserSaid(string request, string time, string expected)
    {
        var (executor, _, clock) = Clock(16, 0);
        var context = new ToolContext(Guid.NewGuid(), request);

        var result = await executor.ExecuteAsync(Call(ClockTool.SetAlarm, System.Text.Json.JsonSerializer.Serialize(new { time })), context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([expected], clock.Done);
    }

    // ---- where on the display the Clock window sits ----

    private static readonly DisplayInfo Left = new(@"\\.\DISPLAY2", 2, -2560, 0, 2560, 1440, false);
    private static readonly DisplayInfo Main = new(@"\\.\DISPLAY1", 1, 0, 0, 1920, 1080, true);

    private sealed class FixedDisplays(params DisplayInfo[] displays) : IDisplays
    {
        public IReadOnlyList<DisplayInfo> List() => displays;
    }

    private static readonly ToolContext AboutTheClock = new(Guid.NewGuid(), "move the timer window down around 30% of the monitor");

    private static (ToolExecutor Executor, FakeConfirmation Asked, RecordingClock Clock, InMemoryMemoryStore Memory) Placing(
        ClockSpot? spot, bool approve = true, params DisplayInfo[] displays)
    {
        var clock = new RecordingClock { Spot = spot };
        var memory = new InMemoryMemoryStore();
        var asked = new FakeConfirmation(approve);
        var tool = new SetClockDisplayTool(clock, new FixedDisplays(displays.Length == 0 ? [Main, Left] : displays), memory);
        return (new ToolExecutor([tool], asked, new FakePermissions(true)), asked, clock, memory);
    }

    [Fact]
    public void ASpotIsMovedByAShareOfTheWholeDisplay_AndStaysOnIt()
    {
        // A window a quarter as tall as its display, a fifth of the way down the room it has: "down 30%" of the display is 0.3 / 0.75 of that room.
        var spot = new ClockSpot(0.5, 0.2, Width: 0.125, Height: 0.25);

        var down = spot.Moved(0, 0.3);
        Assert.Equal((0.5, 0.6), (down.X, Math.Round(down.Y, 6)));

        var left = spot.Moved(-0.3, 0);
        Assert.Equal(Math.Round(0.5 - (0.3 / 0.875), 6), Math.Round(left.X, 6));

        // Never past an edge.
        Assert.Equal((0, 1), (spot.Moved(-5, 5).X, spot.Moved(-5, 5).Y));
        Assert.Equal("50% across and 60% down", down.Describe());
    }

    [Theory]
    [InlineData("""{"move":"down","percent":30}""", "down by 30% of the display", 0.5, 0.6)]
    [InlineData("""{"move":"up","percent":15}""", "up by 15% of the display", 0.5, 0.0)] // As far up as it goes.
    [InlineData("""{"move":"right","percent":30}""", "right by 30% of the display", 0.842857, 0.2)]
    [InlineData("""{"move":"left"}""", "left by 10% of the display", 0.385714, 0.2)] // A small step when no amount is said.
    public async Task TheUserIsAskedAboutTheMove_AndOnAYesTheWindowIsMovedAndItsPlaceRemembered(string arguments, string moveSaid, double x, double y)
    {
        var (executor, asked, clock, memory) = Placing(new ClockSpot(0.5, 0.2, 0.125, 0.25));

        var result = await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, arguments), AboutTheClock);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        var question = Assert.Single(asked.Shown);
        Assert.Equal($"Move the Clock window {moveSaid}?", question.Title);
        Assert.Equal("Yes, move it", question.ApproveLabel);
        Assert.Equal(moveSaid, question.Details.Single(detail => detail.Label == "Move").Value);

        // The place is kept as shares of the display, with no display chosen: wherever the window is.
        var kept = ClockPlace.ReadSpot(memory);
        Assert.NotNull(kept);
        Assert.Equal(x, kept.X, 3);
        Assert.Equal(y, kept.Y, 3);
        Assert.Null(ClockPlace.Read(memory, [Main, Left]));
        Assert.StartsWith("The Clock window (alarms and timers) opens where it is, ", Assert.Single(memory.Entries).Text, StringComparison.Ordinal);
        Assert.Equal(1, clock.Placed);
    }

    [Theory]
    [InlineData("top right", 1.0, 0.0)]
    [InlineData("bottom-left", 0.0, 1.0)]
    [InlineData("centre", 0.5, 0.5)]
    [InlineData("top", 0.3, 0.0)] // An edge keeps where it is along the other way.
    public async Task ANamedPlaceIsThatCornerEdgeOrTheMiddle(string place, double x, double y)
    {
        var (executor, asked, _, memory) = Placing(new ClockSpot(0.3, 0.7));

        var result = await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, System.Text.Json.JsonSerializer.Serialize(new { place })), AboutTheClock);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Single(asked.Shown);
        Assert.Equal(new ClockSpot(x, y), ClockPlace.ReadSpot(memory));
    }

    [Fact]
    public async Task AMoveKeepsTheDisplayThatWasChosenBefore_AndANewDisplayKeepsThePlace()
    {
        var (executor, asked, _, memory) = Placing(new ClockSpot(0.5, 0.5));

        await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"display":"left"}"""), AboutTheClock);
        await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"place":"top right"}"""), AboutTheClock);

        Assert.Equal(Left, ClockPlace.Read(memory, [Main, Left]));
        Assert.Equal(new ClockSpot(1, 0), ClockPlace.ReadSpot(memory));
        Assert.Equal("The Clock window (alarms and timers) opens on Display 2, on the left, 100% across and 0% down.", Assert.Single(memory.Entries).Text);

        // Both at once is one question, about the place.
        await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"display":"main","place":"bottom"}"""), AboutTheClock);
        Assert.Equal("Is this the right place for the Clock window?", asked.Shown[^1].Title);
        Assert.Equal(Main, ClockPlace.Read(memory, [Main, Left]));
        Assert.Equal(new ClockSpot(0.5, 1), ClockPlace.ReadSpot(memory));
    }

    [Fact]
    public async Task APcWithOneDisplayCanStillHaveTheWindowMoved()
    {
        var (executor, asked, clock, memory) = Placing(new ClockSpot(0.5, 0.5, 0.2, 0.2), true, Main);

        var result = await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"move":"down","percent":20}"""), AboutTheClock);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Single(asked.Shown);
        Assert.Equal(0.75, Math.Round(ClockPlace.ReadSpot(memory)!.Y, 6));
        Assert.Equal(1, clock.Placed);
    }

    [Theory]
    [InlineData("""{}""", "Say what to change")]
    [InlineData("""{"move":"sideways"}""", "must be one of: up, down, left, right")]
    [InlineData("""{"place":"somewhere nice"}""", "That is not a place")]
    public async Task WhatCannotBeReadIsToldToTheModel_AndNobodyIsAsked(string arguments, string says)
    {
        var (executor, asked, clock, memory) = Placing(new ClockSpot(0.5, 0.5));

        var result = await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, arguments), AboutTheClock);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out _, out var message));
        Assert.Contains(says, message, StringComparison.Ordinal);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(memory.Entries);
        Assert.Equal(0, clock.Placed);
    }

    [Fact]
    public async Task AMoveNeedsSomewhereToMoveFrom_AndSomewhereToGo()
    {
        // No Clock window is open, and no place was chosen before: there is nothing to move.
        var (closed, asked, _, _) = Placing(null);
        var nothing = await closed.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"move":"down","percent":30}"""), AboutTheClock);
        Assert.Equal(ToolResultStatus.Failed, nothing.Status);
        Assert.Contains("The Clock window is not open", nothing.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, asked.Asked);

        // At the bottom already.
        var (bottom, unasked, _, _) = Placing(new ClockSpot(0.5, 1));
        var stuck = await bottom.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"move":"down","percent":30}"""), AboutTheClock);
        Assert.Equal(ToolResultStatus.Failed, stuck.Status);
        Assert.Contains("already as far down as it goes", stuck.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, unasked.Asked);
    }

    [Fact]
    public async Task OnANoNothingIsRememberedAndNothingIsMoved()
    {
        var (executor, asked, clock, memory) = Placing(new ClockSpot(0.5, 0.5), approve: false);

        var result = await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"move":"down","percent":30}"""), AboutTheClock);

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(1, asked.Asked);
        Assert.Empty(memory.Entries);
        Assert.Equal(0, clock.Placed);
    }

    [Fact]
    public async Task WhatWasRememberedBeforeThereWasAPlaceStillSaysWhichDisplay()
    {
        var memory = new InMemoryMemoryStore();
        await memory.SaveAsync(new MemoryEntry(Guid.NewGuid(), MemoryKind.Preference, "x", DateTimeOffset.UtcNow) { Key = ClockPlace.Key, Value = Left.Id + "|left" });

        Assert.Equal(Left, ClockPlace.Read(memory, [Main, Left]));
        Assert.Null(ClockPlace.ReadSpot(memory));
        Assert.Equal(new ClockPlacement(Left, null), ClockPlace.ReadPlacement(memory, [Main, Left]));
        Assert.Null(ClockPlace.ReadPlacement(new InMemoryMemoryStore(), [Main, Left]));
    }
}
