using System.Text.Json;
using Assistant.Core.Clock;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Displays;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.Tools;
using Assistant.Tools.Clock;
using Assistant.Tools.Memory;
using Assistant.Tools.Quiet;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The small things the Assistant does without a question (PROJECT_SPEC §4.8): keeping a note the user gave it, and flipping Do not disturb. Each is
/// one of its own tools, fixed in code as one the user is not asked about; everything else that changes something still goes through the question.
/// </summary>
public sealed class MemoryAndSwitchesTests
{
    private static readonly ToolContext Context = new(Guid.NewGuid(), "remember that");

    private static ToolCall Call(string name, string arguments = "{}") => new("c1", name, arguments);

    // The user says no to everything they are asked: what runs here ran without asking.
    private static (ToolExecutor Executor, FakeConfirmation Asked) Refusing(params ITool[] tools)
    {
        var asked = new FakeConfirmation(false);
        return (new ToolExecutor(tools, asked, new FakePermissions(true)), asked);
    }

    private sealed class QuietSystem : ISystemActions
    {
        public bool? Quiet { get; set; } = false;

        public bool Works { get; set; } = true;

        public List<bool> Asked { get; } = [];

        public bool? GetDoNotDisturb() => Quiet;

        public bool SetDoNotDisturb(bool on)
        {
            Asked.Add(on);
            if (Works)
            {
                Quiet = on;
            }

            return Works;
        }

        public bool OpenWindowsSettings() => false;

        public bool LockWorkstation() => false;

        public bool SetMuted(bool muted) => false;

        public bool SetVolume(int percent) => false;

        public int? ChangeVolume(int percent) => null;

        public VolumeState? GetVolume() => null;

        public string? GetFolder(SystemFolder folder) => null;
    }

    // ---- remember ----

    [Fact]
    public async Task ANoteTheUserGaveIsKept_WithNobodyAsked_AndTheModelIsToldToSaySo()
    {
        var memory = new InMemoryMemoryStore();
        var (executor, asked) = Refusing(MemoryTools.Remember(memory, TimeProvider.System));

        var result = await executor.ExecuteAsync(Call("remember", """{"note":"The user's sister is called   Lena."}"""), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(0, asked.Asked);
        var kept = Assert.Single(memory.Entries);
        Assert.Equal((MemoryKind.Note, "The user's sister is called Lena."), (kept.Kind, kept.Text));
        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.True(json.RootElement.GetProperty("remembered").GetBoolean());
        Assert.Contains("Settings, under Memory", json.RootElement.GetProperty("say").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameNoteIsNotKeptTwice()
    {
        var memory = new InMemoryMemoryStore();
        var (executor, _) = Refusing(MemoryTools.Remember(memory, TimeProvider.System));

        await executor.ExecuteAsync(Call("remember", """{"note":"The user wants temperatures in Celsius."}"""), Context);
        await executor.ExecuteAsync(Call("remember", """{"note":"the user wants temperatures in celsius"}"""), Context);

        Assert.Single(memory.Entries);
    }

    [Theory]
    [InlineData("""{"note":"Always open https://example.com/login first"}""")] // A note is a fact about the user, never somewhere to go.
    [InlineData("""{"note":"see www.example.com"}""")]
    [InlineData("""{"note":"x"}""")]
    [InlineData("""{"note":""}""")]
    [InlineData("""{}""")]
    [InlineData("""{"note":"The user likes tea.","always":true}""")] // Nothing the tool does not name.
    public async Task WhatIsNoNoteIsNotKept(string arguments)
    {
        var memory = new InMemoryMemoryStore();
        var (executor, _) = Refusing(MemoryTools.Remember(memory, TimeProvider.System));

        var result = await executor.ExecuteAsync(Call("remember", arguments), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Empty(memory.Entries);
    }

    // ---- Do not disturb ----

    [Theory]
    [InlineData("""{"on":true}""", true, "Do not disturb is on.")]
    [InlineData("""{"on":false}""", false, "Do not disturb is off.")]
    public async Task DoNotDisturbIsSwitchedAtOnce_WithNobodyAsked(string arguments, bool on, string said)
    {
        var system = new QuietSystem { Quiet = !on };
        var (executor, asked) = Refusing(DoNotDisturbTools.Set(system));

        var result = await executor.ExecuteAsync(Call("set_do_not_disturb", arguments), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(0, asked.Asked);
        Assert.Equal([on], system.Asked);
        Assert.Contains(said, result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenWindowsDoesNotSwitchIt_ThatIsSaid_AndNotThatItWasDone()
    {
        var system = new QuietSystem { Works = false };
        var (executor, _) = Refusing(DoNotDisturbTools.Set(system));

        var result = await executor.ExecuteAsync(Call("set_do_not_disturb", """{"on":true}"""), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Nothing was changed", result.OutputJson, StringComparison.Ordinal);
        Assert.Contains("Windows key + N", result.OutputJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"on":"yes"}""")]
    [InlineData("""{"on":true,"for":"an hour"}""")]
    public async Task ACallThatDoesNotSayOnOrOffChangesNothing(string arguments)
    {
        var system = new QuietSystem();
        var (executor, _) = Refusing(DoNotDisturbTools.Set(system));

        var result = await executor.ExecuteAsync(Call("set_do_not_disturb", arguments), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Empty(system.Asked);
    }

    // ---- which tools are never asked about ----

    [Fact]
    public async Task OnlyOneOfTheAssistantsOwnToolsCanSkipTheQuestion_NeverAConnectedAppsAndNeverOneThatSaysNothing()
    {
        var ran = new List<string>();
        ITool Tool(string name, bool without) => new HandlerTool(
            ToolDefinition.Create(name, "Changes a thing.", [], RiskLevel.SideEffect, runsWithoutAsking: without),
            (call, _, _, _) =>
            {
                ran.Add(call.ToolName);
                return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
            });
        var (executor, asked) = Refusing(Tool("flip_switch", true), Tool("change_thing", false), Tool("mcp_someapp_flip", true));

        var own = await executor.ExecuteAsync(Call("flip_switch"), Context);
        var ordinary = await executor.ExecuteAsync(Call("change_thing"), Context);
        var connected = await executor.ExecuteAsync(Call("mcp_someapp_flip"), Context);

        Assert.Equal(ToolResultStatus.Succeeded, own.Status);
        Assert.Equal((ToolResultStatus.Declined, ToolResultStatus.Declined), (ordinary.Status, connected.Status));
        Assert.Equal(["flip_switch"], ran);
        Assert.Equal(2, asked.Asked);
    }

    // ---- the display the Clock window opens on ----

    private static readonly DisplayInfo Left = new(@"\\.\DISPLAY2", 2, -2560, 0, 2560, 1440, false);
    private static readonly DisplayInfo Main = new(@"\\.\DISPLAY1", 1, 0, 0, 1920, 1080, true);
    private static readonly DisplayInfo Right = new(@"\\.\DISPLAY3", 3, 1920, 0, 1920, 1080, false);

    private sealed class FixedDisplays(params DisplayInfo[] displays) : IDisplays
    {
        public IReadOnlyList<DisplayInfo> List() => displays;
    }

    private sealed class Pointer : IDisplayPointer
    {
        public List<string> Marked { get; } = [];

        public int Cleared { get; private set; }

        public void PointOut(DisplayInfo display, string words) => Marked.Add(display.Id);

        public void Clear() => Cleared++;
    }

    private sealed class PlacedClock : IClockApp
    {
        public int Placed { get; private set; }

        public bool IsInstalled => true;

        public Task<ClockResult> SetAlarmAsync(ClockAlarm alarm, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> StartTimerAsync(TimeSpan duration, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> ControlTimerAsync(TimerAction action, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> ControlStopwatchAsync(StopwatchAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> StartFocusSessionAsync(int minutes, bool skipBreaks = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> ApplyPlaceAsync(CancellationToken cancellationToken = default)
        {
            Placed++;
            return Task.FromResult(ClockResult.Ok("The Clock window was moved there."));
        }
    }

    private static readonly ToolContext AboutTheClock = new(Guid.NewGuid(), "always move the alarm popup to the left monitor");

    [Theory]
    [InlineData("left", 2)]
    [InlineData("the left monitor", 2)]
    [InlineData("right", 3)]
    [InlineData("middle", 1)]
    [InlineData("main", 1)]
    [InlineData("monitor 3", 3)]
    [InlineData("display two", 2)]
    public void ADisplayIsFoundByHowPeopleSayIt(string said, int number) =>
        Assert.Equal(number, DisplayChoice.Choose([Main, Right, Left], said)?.Number);

    [Theory]
    [InlineData("the big one")]
    [InlineData("")]
    [InlineData("monitor 7")]
    [InlineData("other")] // Of three, "the other one" is no one display.
    public void WordsThatFitNoOneDisplayChooseNone(string said) =>
        Assert.Null(DisplayChoice.Choose([Main, Right, Left], said));

    [Fact]
    public void ADisplayMountedOverAnotherIsSaidToBeAboveIt_AndTheRowBesideTheMainOneIsLeftMiddleAndRight()
    {
        // Four displays: a tall one on the left, the main one, one on the right, and a small one over the right one.
        var tall = new DisplayInfo(@"\\.\DISPLAY1", 1, -1080, -649, 1080, 1920, false);
        var main = new DisplayInfo(@"\\.\DISPLAY3", 3, 0, 0, 2560, 1440, true);
        var right = new DisplayInfo(@"\\.\DISPLAY2", 2, 2560, 62, 1920, 1080, false);
        var over = new DisplayInfo(@"\\.\DISPLAY6", 6, 2717, -838, 1440, 900, false);
        DisplayInfo[] all = [tall, main, right, over];

        Assert.Equal(
            ["Display 1, on the left", "Display 3, in the middle (main display)", "Display 2, on the right", "Display 6, above Display 2"],
            all.Select(display => DisplayChoice.Describe(all, display)));
        Assert.Equal(
            [1, 3, 2, 6, 6, 3, 6],
            new[] { "left", "the middle one", "right monitor", "top", "the upper one", "main", "display 6" }.Select(said => DisplayChoice.Choose(all, said)!.Number));
        Assert.Null(DisplayChoice.Choose(all, "bottom"));

        // A laptop under a monitor: neither is left or right of the other.
        var laptop = new DisplayInfo(@"\\.\DISPLAY1", 1, 0, 0, 1920, 1200, true);
        var monitor = new DisplayInfo(@"\\.\DISPLAY2", 2, -320, -1440, 2560, 1440, false);
        Assert.Equal("Display 2, above the others", DisplayChoice.Describe([laptop, monitor], monitor));
        Assert.Equal("Display 1, below the others (main display)", DisplayChoice.Describe([laptop, monitor], laptop));
        Assert.Equal((2, 1), (DisplayChoice.Choose([laptop, monitor], "the top screen")!.Number, DisplayChoice.Choose([laptop, monitor], "bottom")!.Number));
        Assert.Null(DisplayChoice.Choose([laptop, monitor], "left"));
    }

    [Fact]
    public void OfTwoDisplaysTheOtherOneAndTheSecondOneAreTheOneThatIsNotMain()
    {
        var external = new DisplayInfo(@"\\.\DISPLAY5", 5, 1920, 0, 2560, 1440, false);

        Assert.Equal(5, DisplayChoice.Choose([Main, external], "my other screen")?.Number);
        Assert.Equal(5, DisplayChoice.Choose([Main, external], "the second monitor")?.Number);
        Assert.Equal("Display 5, on the right", DisplayChoice.Describe([Main, external], external));
        Assert.Equal("Display 1, on the left (main display)", DisplayChoice.Describe([Main, external], Main));
    }

    [Fact]
    public async Task TheUserIsShownTheDisplayAndAsked_AndOnAYesItIsRememberedAndAnOpenClockWindowIsMoved()
    {
        var memory = new InMemoryMemoryStore();
        var pointer = new Pointer();
        var clock = new PlacedClock();
        var asked = new FakeConfirmation(true);
        var executor = new ToolExecutor([new SetClockDisplayTool(clock, new FixedDisplays(Main, Right, Left), memory, pointer)], asked, new FakePermissions(true));

        var result = await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"display":"left"}"""), AboutTheClock);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        var question = Assert.Single(asked.Shown);
        Assert.Equal("Is this the right display for the Clock window?", question.Title);
        Assert.Equal("Display 2, on the left", question.Details.Single(detail => detail.Label == "Display").Value);
        Assert.Equal("Yes, use it", question.ApproveLabel);

        // Their answer is the point of the question, so it is never one they can be spared with "Always allow".
        Assert.Equal(ConfirmationKind.Other, question.Kind);
        Assert.Equal([Left.Id], pointer.Marked);
        Assert.Equal(1, pointer.Cleared);
        Assert.Equal(1, clock.Placed);

        var kept = Assert.Single(memory.Entries);
        Assert.Equal((MemoryKind.Preference, ClockPlace.Key), (kept.Kind, kept.Key));
        Assert.Equal("The Clock window (alarms and timers) opens on Display 2, on the left.", kept.Text);
        Assert.Equal(Left, ClockPlace.Read(memory, [Main, Right, Left]));
        Assert.Contains("opens on Display 2, on the left from now on", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnANoNothingIsRememberedAndNothingIsMoved()
    {
        var memory = new InMemoryMemoryStore();
        var clock = new PlacedClock();
        var asked = new FakeConfirmation(false);
        var executor = new ToolExecutor([new SetClockDisplayTool(clock, new FixedDisplays(Main, Left), memory, new Pointer())], asked, new FakePermissions(true));

        var result = await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, """{"display":"left"}"""), AboutTheClock);

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Empty(memory.Entries);
        Assert.Equal(0, clock.Placed);
    }

    [Theory]
    [InlineData("the big one", "It is not clear which display")]
    [InlineData("left", "has one display")]
    public async Task ADisplayThatCannotBeToldIsNotAskedAbout(string said, string expected)
    {
        var pointer = new Pointer();
        var asked = new FakeConfirmation(true);
        var displays = expected.Contains("one display", StringComparison.Ordinal) ? new FixedDisplays(Main) : new FixedDisplays(Main, Right, Left);
        var executor = new ToolExecutor([new SetClockDisplayTool(new PlacedClock(), displays, new InMemoryMemoryStore(), pointer)], asked, new FakePermissions(true));

        var result = await executor.ExecuteAsync(Call(SetClockDisplayTool.Name, JsonSerializer.Serialize(new { display = said })), AboutTheClock);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(expected, result.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(pointer.Marked);
    }

    [Fact]
    public async Task ADisplayThatWasUnpluggedIsFoundAgainByTheWordsItWasChosenWith()
    {
        var memory = new InMemoryMemoryStore();
        await ClockPlace.SaveAsync(memory, [Main, Left], Left, "the left monitor", DateTimeOffset.UtcNow);

        // The left display comes back under another name of Windows': it is still the one on the left.
        var again = new DisplayInfo(@"\\.\DISPLAY6", 6, -1920, 0, 1920, 1080, false);
        Assert.Equal(again, ClockPlace.Read(memory, [Main, again]));

        // With one display there is nowhere else to put it.
        Assert.Null(ClockPlace.Read(memory, [Main]));
    }

    // ---- a timer until a time of day ----

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("fixed", now.Offset, "fixed", "fixed");
    }

    private sealed class TimerClock : IClockApp
    {
        public List<TimeSpan> Timers { get; } = [];

        public bool IsInstalled => true;

        public Task<ClockResult> SetAlarmAsync(ClockAlarm alarm, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> StartTimerAsync(TimeSpan duration, string? name = null, CancellationToken cancellationToken = default)
        {
            Timers.Add(duration);
            return Task.FromResult(ClockResult.Ok("It is running."));
        }

        public Task<ClockResult> ControlTimerAsync(TimerAction action, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> ControlStopwatchAsync(StopwatchAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> StartFocusSessionAsync(int minutes, bool skipBreaks = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("21:18", 0, 43, 30)] // Later today: from 20:34:30 until 21:18.
    [InlineData("9:18 pm", 0, 43, 30)]
    [InlineData("20:00", 23, 25, 30)] // Already past today: until that time tomorrow.
    public async Task ATimerForATimeOfDayRunsFromNowUntilThen_WithTheLengthWorkedOutByThePcAndNotTheModel(string until, int hours, int minutes, int seconds)
    {
        var clock = new TimerClock();
        var time = new FixedClock(new DateTimeOffset(2026, 10, 7, 20, 34, 30, TimeSpan.FromHours(-4)));
        var asked = new FakeConfirmation(true);
        var executor = new ToolExecutor([new StartTimerTool(clock, time)], asked, new FakePermissions(true));

        var result = await executor.ExecuteAsync(
            Call(ClockTool.StartTimer, JsonSerializer.Serialize(new { until })), new ToolContext(Guid.NewGuid(), "set a timer for 9:18pm"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([new TimeSpan(hours, minutes, seconds)], clock.Timers);
        var question = Assert.Single(asked.Shown);
        Assert.StartsWith("Start a timer until ", question.Title, StringComparison.Ordinal);
        Assert.Contains(question.Details, detail => detail.Label == "Ends at");
        Assert.Contains(question.Details, detail => detail.Label == "Length");
    }

    [Fact]
    public async Task AnUntilThatIsNoTimeOfDayIsToldToTheModel_AndNoTimerIsStarted()
    {
        var clock = new TimerClock();
        var asked = new FakeConfirmation(true);
        var executor = new ToolExecutor([new StartTimerTool(clock, TimeProvider.System)], asked, new FakePermissions(true));

        var result = await executor.ExecuteAsync(Call(ClockTool.StartTimer, """{"until":"later"}"""), new ToolContext(Guid.NewGuid(), "set a timer"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(clock.Timers);
    }

    [Fact]
    public void TheToolsSayWhichIsForATimerAndWhichForAnAlarm()
    {
        var timer = new StartTimerTool(new TimerClock()).Definition.Description;
        var alarm = new SetAlarmTool(new TimerClock()).Definition.Description;

        Assert.Contains("never use set_alarm for a timer", timer, StringComparison.Ordinal);
        Assert.Contains("When the user says timer, use start_timer, even for a time of day", alarm, StringComparison.Ordinal);
    }
}
