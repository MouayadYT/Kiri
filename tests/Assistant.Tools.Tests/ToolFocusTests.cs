using Assistant.Core.Clock;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.People;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.Tools;
using Assistant.Tools.Audio;
using Assistant.Tools.Clock;
using Assistant.Tools.Memory;
using Assistant.Tools.Messaging;
using Assistant.Tools.Quiet;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// Which tools a request is about (kept first when not all can be offered), the small switches that run without a question offered only when they are
/// named, a word that is a slip of the keyboard away, and a yes to a draft that is send_message's alone.
/// </summary>
public sealed class ToolFocusTests
{
    private sealed class NoClock : IClockApp
    {
        public bool IsInstalled => true;

        public Task<ClockResult> SetAlarmAsync(ClockAlarm alarm, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> StartTimerAsync(TimeSpan duration, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> ControlTimerAsync(TimerAction action, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> ControlStopwatchAsync(StopwatchAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClockResult> StartFocusSessionAsync(int minutes, bool skipBreaks = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Speakers : ISystemActions
    {
        public bool OpenWindowsSettings() => false;

        public bool LockWorkstation() => false;

        public bool SetMuted(bool muted) => true;

        public bool SetVolume(int percent) => true;

        public int? ChangeVolume(int percent) => null;

        public VolumeState? GetVolume() => new(50, false);

        public string? GetFolder(SystemFolder folder) => null;
    }

    private static HandlerTool Plain(string name) => new(
        ToolDefinition.Create(name, "Does a thing.", [], RiskLevel.ReadOnly),
        (call, _, _, _) => Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}")));

    // ---- what a request is about ----

    [Fact]
    public void TheToolsARequestIsAboutAreTheOnesItNames_AndTheOnesThatAreAlwaysThereAreNot()
    {
        var speakers = new Speakers();
        var registry = new ToolRegistry(
        [
            Plain("search_files"), Plain("open_application"), VolumeTools.Mute(speakers), VolumeTools.Unmute(speakers), new StartTimerTool(new NoClock()),
        ]);
        var conversation = Guid.NewGuid();

        var timer = new ToolContext(conversation, "set a timer for ten minutes");
        Assert.Equal(["start_timer"], registry.FocusedFor(timer));
        Assert.Equal(["search_files", "open_application", "start_timer"], registry.ToolsFor(timer).Select(tool => tool.Name));

        var mute = new ToolContext(Guid.NewGuid(), "mute the sound");
        Assert.Equal(["mute", "unmute"], registry.FocusedFor(mute).Order());
    }

    // ---- the switches that run without a question ----

    [Theory]
    [InlineData("mute", true)]
    [InlineData("turn the volume down", true)]
    [InlineData("unmute my speakers", true)]
    [InlineData("hi", false)] // In 0.1.142 the model unmuted the user's speakers in reply to "hi", while sending a message.
    [InlineData("send a message to my brother", false)]
    [InlineData("search my files for the report", false)]
    public void TheSoundsToolsAreOfferedOnlyForARequestAboutTheSound(string request, bool offered)
    {
        var speakers = new Speakers();
        ITool[] tools = [VolumeTools.GetVolume(speakers), VolumeTools.SetVolume(speakers), VolumeTools.Mute(speakers), VolumeTools.Unmute(speakers)];

        Assert.All(tools, tool => Assert.Equal(offered, tool.IsOffered(new ToolContext(Guid.NewGuid(), request))));
    }

    [Theory]
    [InlineData("turn on do not disturb", true)]
    [InlineData("silence my notifications", true)]
    [InlineData("yes", false)]
    [InlineData("set a timer for 4:20", false)]
    public void DoNotDisturbIsOfferedOnlyForARequestAboutNotificationsOrQuiet(string request, bool offered) =>
        Assert.Equal(offered, DoNotDisturbTools.Set(new Speakers()).IsOffered(new ToolContext(Guid.NewGuid(), request)));

    [Fact]
    public void ATopicStaysForTheNextRequestsOfTheConversation_AndOutsideATurnEverythingIsThere()
    {
        var unmute = VolumeTools.Unmute(new Speakers());
        var conversation = Guid.NewGuid();

        Assert.True(unmute.IsOffered(new ToolContext(conversation, "mute the speakers")));
        Assert.True(unmute.IsOffered(new ToolContext(conversation, "now turn it back on")));
        Assert.False(unmute.IsOffered(new ToolContext(Guid.NewGuid(), "now turn it back on")));

        // A call made outside a model's turn has no request to be about, as the catalog of every tool has none.
        Assert.True(unmute.IsOffered(new ToolContext(conversation)));
        Assert.True(MemoryTools.Remember(new Assistant.Core.Memory.InMemoryMemoryStore(), TimeProvider.System).IsOffered(new ToolContext(Guid.NewGuid())));
    }

    // ---- slips of the keyboard ----

    [Theory]
    [InlineData("tiemr", "timer", true)] // Two letters swapped.
    [InlineData("timmer", "timer", true)] // One letter more.
    [InlineData("timr", "timer", true)] // One less.
    [InlineData("alram", "alarm", true)]
    [InlineData("alarn", "alarm", false)] // A different letter is how real words differ.
    [InlineData("times", "timer", false)]
    [InlineData("found", "sound", false)]
    [InlineData("lock", "clock", false)] // The first letter is never the slip.
    [InlineData("timers", "timer", false)] // A letter more or less at the end is another word (time, timers): plurals are listed as they are.
    [InlineData("time", "timer", false)]
    [InlineData("timer", "timer", true)]
    [InlineData("stopwatch", "stopwatches", false)]
    public void AWordOneSlipAwayIsTheWordMeant(string word, string known, bool slip) => Assert.Equal(slip, RequestWords.IsSlip(word, known));

    [Theory]
    [InlineData("set a tiemr for 11:00 am", true)] // The request that opened the Calculator.
    [InlineData("set an alram for 7", true)]
    [InlineData("start the stopwach", true)]
    [InlineData("mute", false)]
    [InlineData("how many times did I open it", false)]
    [InlineData("block this number", false)]
    [InlineData("open the calculator", false)]
    public void TheClocksToolsAreOfferedForAWordOneSlipAway(string request, bool offered) =>
        Assert.Equal(offered, new StartTimerTool(new NoClock()).IsOffered(new ToolContext(Guid.NewGuid(), request)));

    // ---- a yes to a draft ----

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(ToolRegistry Registry, ToolExecutor Executor)> MessagingAsync()
    {
        var people = new InMemoryPersonStore();
        await people.SaveAsync(Person.Create("Marcus", Now) with
        {
            Relationships = ["Brother"],
            Identifiers = [new(PersonIdentifierKind.Username, "@marcus:beeper.com", "Beeper")],
        });
        var provider = new MockMessagingProvider();
        ITool[] tools = [Plain("search_files"), new DraftMessageTool(provider, new PersonResolver(people)), new SendMessageTool(provider, new PersonResolver(people))];
        return (new ToolRegistry(tools), new ToolExecutor(tools, new FakeConfirmation(true), new FakePermissions(true)));
    }

    private static async Task<IReadOnlyList<string>> OfferedAsync(ToolRegistry registry, ToolContext context)
    {
        await registry.PrepareToolsAsync(context);
        return [.. registry.ToolsFor(context).Select(tool => tool.Name)];
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes, send it")]
    [InlineData("ok go ahead")]
    [InlineData("sure")]
    public async Task AYesToADraftShownInWordsIsSendMessagesAlone(string yes)
    {
        var (registry, executor) = await MessagingAsync();
        var conversation = Guid.NewGuid();

        var asking = new ToolContext(conversation, "draft a message to my brother saying hi, don't send it yet");
        Assert.Contains("draft_message", await OfferedAsync(registry, asking));
        var draft = await executor.ExecuteAsync(new ToolCall("c1", "draft_message", """{"recipient":"my brother","text":"hi"}"""), asking);
        Assert.Equal(ToolResultStatus.Succeeded, draft.Status);

        Assert.Equal(["send_message"], await OfferedAsync(registry, new ToolContext(conversation, yes)));
    }

    [Fact]
    public async Task AnythingButAYesLetsTheDraftGo_AndAYesWithNoDraftIsNotClaimed()
    {
        var (registry, executor) = await MessagingAsync();
        var conversation = Guid.NewGuid();
        var asking = new ToolContext(conversation, "draft a message to my brother saying hi");
        await OfferedAsync(registry, asking);
        await executor.ExecuteAsync(new ToolCall("c1", "draft_message", """{"recipient":"my brother","text":"hi"}"""), asking);

        // The user changes their mind: the draft no longer waits, and a later yes is not taken for it.
        Assert.Equal(["search_files", "draft_message", "send_message"], await OfferedAsync(registry, new ToolContext(conversation, "actually make it bye")));
        Assert.Equal(["search_files", "draft_message", "send_message"], await OfferedAsync(registry, new ToolContext(conversation, "yes")));

        // A yes in a conversation with no draft is just a yes.
        Assert.Equal(["search_files"], await OfferedAsync(registry, new ToolContext(Guid.NewGuid(), "yes")));
    }

    [Fact]
    public async Task AMessageThatWasSentLeavesNothingWaitingForAYes()
    {
        var (registry, executor) = await MessagingAsync();
        var conversation = Guid.NewGuid();
        var asking = new ToolContext(conversation, "draft a message to my brother saying hi");
        await OfferedAsync(registry, asking);
        await executor.ExecuteAsync(new ToolCall("c1", "draft_message", """{"recipient":"my brother","text":"hi"}"""), asking);

        var yes = new ToolContext(conversation, "yes");
        Assert.Equal(["send_message"], await OfferedAsync(registry, yes));
        Assert.Equal(ToolResultStatus.Succeeded, (await executor.ExecuteAsync(new ToolCall("c2", "send_message", """{"recipient":"my brother","text":"hi"}"""), yes)).Status);

        Assert.Contains("search_files", await OfferedAsync(registry, new ToolContext(conversation, "yes")));
    }

    [Fact]
    public void TheModelIsToldToSendStraightToTheQuestion_AndToDraftOnlyWhenAskedTo()
    {
        var send = new SendMessageTool(new MockMessagingProvider(), null).Definition.Description;
        var draft = new DraftMessageTool(new MockMessagingProvider(), null).Definition.Description;

        Assert.Contains("do not ask them in words first", send, StringComparison.Ordinal);
        Assert.Contains("only when the user asks for a draft", draft, StringComparison.Ordinal);
        Assert.Contains("call send_message instead", draft, StringComparison.Ordinal);
    }
}
