using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// "Always allow" (PROJECT_SPEC §4.8, step 115): the user may answer a question about a timer, the volume or an application once for every time
/// after, so that it is not asked again and again. A message to a person is asked about every time whatever was answered or saved, and what is kept
/// is one action as it was: a tool that has changed is asked about again.
/// </summary>
public sealed class StandingApprovalTests
{
    private static readonly ToolDefinition StartTimer = new("start_timer", "Starts a timer.", """{"type":"object"}""", RiskLevel.SideEffect);
    private static readonly ToolDefinition SendMessage = new("send_message", "Sends a message.", """{"type":"object"}""", RiskLevel.SideEffect);

    // A connected app's tool that sends a message carries the Messaging permission, by what its name says (McpToolPermission).
    private static readonly ToolDefinition BeeperSend =
        new("mcp_beeper_send_message", "Sends a message.", """{"type":"object"}""", RiskLevel.SideEffect) { RequiredPermission = PermissionCapability.Messaging };

    private static readonly ToolDefinition AddTask = new("mcp_todo_create_task", "Adds a task.", """{"type":"object"}""", RiskLevel.SideEffect);

    private static ToolConfirmation Question(ConfirmationKind kind) => new(kind, "Do it?", [new ConfirmationDetail("What", "this")]);

    private static ToolCall CallOf(ToolDefinition tool) => new("call-1", tool.Name, "{}");

    private static AppEventBus NewBus() => new(NullLogger<AppEventBus>.Instance);

    private static ConfirmationBroker Broker(IAppEventBus bus, ISettingsService? settings) =>
        new(bus, TimeProvider.System, NullLogger<ConfirmationBroker>.Instance, TimeSpan.FromSeconds(30), settings);

    // Shows every question of a conversation and answers it as the test says, counting them.
    private sealed class Surface : IDisposable
    {
        private readonly object _owner = new();
        private readonly IDisposable _subscription;

        public Surface(AppEventBus bus, Guid conversation, Func<ToolConfirmationRequested, bool> answer)
        {
            _subscription = bus.Subscribe<object, ToolConfirmationRequested>(
                _owner,
                (_, request, _) =>
                {
                    if (request.ConversationId == conversation)
                    {
                        Asked.Add(request);
                        request.MarkShown();
                        answer(request);
                    }

                    return Task.CompletedTask;
                });
        }

        public List<ToolConfirmationRequested> Asked { get; } = [];

        public void Dispose() => _subscription.Dispose();
    }

    [Theory]
    [InlineData(ConfirmationKind.ChangeSystem, true)]
    [InlineData(ConfirmationKind.Launch, true)]
    [InlineData(ConfirmationKind.ConnectedApp, true)]
    [InlineData(ConfirmationKind.SendMessage, false)]
    [InlineData(ConfirmationKind.ChangeFiles, false)]
    [InlineData(ConfirmationKind.Upload, false)]
    [InlineData(ConfirmationKind.Capture, false)]
    [InlineData(ConfirmationKind.Access, false)]
    [InlineData(ConfirmationKind.Other, false)]
    public void OnlyWhatChangesASettingOpensSomethingOrChangesAConnectedAppMayBeKept(ConfirmationKind kind, bool mayBeKept) =>
        Assert.Equal(mayBeKept, StandingApprovals.MayBeKept(StartTimer, Question(kind)));

    [Fact]
    public void AMessageIsNeverKept_NotTheAssistantsOwn_AndNotAConnectedAppsWhateverKindItsQuestionIs()
    {
        Assert.False(StandingApprovals.MayBeKept(SendMessage, Question(ConfirmationKind.SendMessage)));
        Assert.False(StandingApprovals.MayBeKept(BeeperSend, Question(ConfirmationKind.ConnectedApp)));
        Assert.True(StandingApprovals.MayBeKept(AddTask, Question(ConfirmationKind.ConnectedApp)));

        // Nor a tool that only reads, which is never asked about, or one that destroys, which is never run.
        Assert.False(StandingApprovals.MayBeKept(StartTimer with { RiskLevel = RiskLevel.ReadOnly }, Question(ConfirmationKind.ChangeSystem)));
        Assert.False(StandingApprovals.MayBeKept(StartTimer with { RiskLevel = RiskLevel.Destructive }, Question(ConfirmationKind.ChangeSystem)));
    }

    [Fact]
    public async Task AlwaysAllowIsAYesToThisCall_AndToTheSameActionWithoutAQuestionAfterwards()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        var settings = new FixedSettings();
        using var surface = new Surface(bus, conversation, request => request.ApproveAlways());
        var broker = Broker(bus, settings);

        var first = await broker.ConfirmToolCallAsync(StartTimer, CallOf(StartTimer), new ToolContext(conversation), Question(ConfirmationKind.ChangeSystem));

        // The question offered it, the call is made, and the action is kept.
        Assert.Equal(ConfirmationDecision.Approved, first);
        Assert.True(Assert.Single(surface.Asked).CanAlwaysAllow);
        Assert.Equal([StandingApprovals.KeyOf(StartTimer)], settings.Current.Permissions.AlwaysAllowed);

        // The next call of it is made without anyone being asked, in any conversation, shown or not.
        var second = await broker.ConfirmToolCallAsync(StartTimer, CallOf(StartTimer), new ToolContext(Guid.NewGuid()), Question(ConfirmationKind.ChangeSystem));
        Assert.Equal(ConfirmationDecision.Approved, second);
        Assert.Single(surface.Asked);
    }

    [Fact]
    public async Task AnOrdinaryYesKeepsNothing_AndTheNextCallIsAskedAbout()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        var settings = new FixedSettings();
        using var surface = new Surface(bus, conversation, request => request.Approve());
        var broker = Broker(bus, settings);

        await broker.ConfirmToolCallAsync(StartTimer, CallOf(StartTimer), new ToolContext(conversation), Question(ConfirmationKind.ChangeSystem));
        await broker.ConfirmToolCallAsync(StartTimer, CallOf(StartTimer), new ToolContext(conversation), Question(ConfirmationKind.ChangeSystem));

        Assert.Empty(settings.Current.Permissions.AlwaysAllowed);
        Assert.Equal(2, surface.Asked.Count);
    }

    [Fact]
    public async Task AMessageIsAskedAboutEveryTime_WhateverIsAnsweredAndWhateverTheSettingsHold()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();

        // Settings that say, by hand or by a fault, that sending is always allowed.
        var settings = new FixedSettings(new AppSettings
        {
            Permissions = new PermissionSettings { AlwaysAllowed = [StandingApprovals.KeyOf(SendMessage), StandingApprovals.KeyOf(BeeperSend)] },
        });
        using var surface = new Surface(bus, conversation, request => request.ApproveAlways() || request.Approve());
        var broker = Broker(bus, settings);

        foreach (var (tool, kind) in new[] { (SendMessage, ConfirmationKind.SendMessage), (BeeperSend, ConfirmationKind.ConnectedApp) })
        {
            Assert.Equal(ConfirmationDecision.Approved, await broker.ConfirmToolCallAsync(tool, CallOf(tool), new ToolContext(conversation), Question(kind)));
            Assert.Equal(ConfirmationDecision.Approved, await broker.ConfirmToolCallAsync(tool, CallOf(tool), new ToolContext(conversation), Question(kind)));
        }

        // Each of the four calls was a question, none of them offered "always", and "always" was not an answer to any.
        Assert.Equal(4, surface.Asked.Count);
        Assert.All(surface.Asked, request =>
        {
            Assert.False(request.CanAlwaysAllow);
            Assert.False(request.IsAlways);
        });
    }

    [Fact]
    public async Task AToolThatHasChangedSinceIsAskedAboutAgain()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        var settings = new FixedSettings(new AppSettings { Permissions = new PermissionSettings { AlwaysAllowed = [StandingApprovals.KeyOf(AddTask)] } });
        using var surface = new Surface(bus, conversation, request => request.Decline());
        var broker = Broker(bus, settings);

        // As it was when the user said so: no question.
        Assert.Equal(
            ConfirmationDecision.Approved,
            await broker.ConfirmToolCallAsync(AddTask, CallOf(AddTask), new ToolContext(conversation), Question(ConfirmationKind.ConnectedApp)));
        Assert.Empty(surface.Asked);

        // The app now says the same tool does something else, or takes something else: that is not what was allowed.
        foreach (var changed in new[]
                 {
                     AddTask with { Description = "Deletes every task." },
                     AddTask with { InputSchemaJson = """{"type":"object","properties":{"everything":{"type":"boolean"}}}""" },
                 })
        {
            Assert.Equal(
                ConfirmationDecision.Declined,
                await broker.ConfirmToolCallAsync(changed, CallOf(changed), new ToolContext(conversation), Question(ConfirmationKind.ConnectedApp)));
        }

        Assert.Equal(2, surface.Asked.Count);
    }

    [Fact]
    public async Task WithoutSettingsToKeepItIn_NoQuestionOffersIt()
    {
        var bus = NewBus();
        var conversation = Guid.NewGuid();
        using var surface = new Surface(bus, conversation, request => request.ApproveAlways() || request.Approve());

        var decision = await Broker(bus, settings: null).ConfirmToolCallAsync(
            StartTimer, CallOf(StartTimer), new ToolContext(conversation), Question(ConfirmationKind.ChangeSystem));

        Assert.Equal(ConfirmationDecision.Approved, decision);
        Assert.False(Assert.Single(surface.Asked).CanAlwaysAllow);
    }

    [Fact]
    public void KeepingAnActionReplacesItsEarlierForm_AndAskingAgainForgetsIt()
    {
        var permissions = StandingApprovals.Keep(new PermissionSettings(), StartTimer);
        var changed = StartTimer with { Description = "Starts a timer and keeps it on top." };
        permissions = StandingApprovals.Keep(permissions, changed);
        permissions = StandingApprovals.Keep(permissions, AddTask);

        Assert.Equal([StandingApprovals.KeyOf(changed), StandingApprovals.KeyOf(AddTask)], permissions.AlwaysAllowed);
        Assert.False(StandingApprovals.IsKept(permissions, StartTimer));
        Assert.True(StandingApprovals.IsKept(permissions, changed));

        permissions = StandingApprovals.Forget(permissions, "start_timer");
        Assert.Equal([StandingApprovals.KeyOf(AddTask)], permissions.AlwaysAllowed);
        Assert.False(StandingApprovals.IsKept(permissions, changed));
    }

    [Theory]
    [InlineData("start_timer:0123456789abcdef", true)]
    [InlineData("mcp_todo.create-task:ffffffffffffffff", true)]
    [InlineData("start_timer", false)]
    [InlineData("start_timer:0123", false)]
    [InlineData("start_timer:0123456789ABCDEF", false)]
    [InlineData("start timer:0123456789abcdef", false)]
    [InlineData(":0123456789abcdef", false)]
    [InlineData("", false)]
    public void OnlyAToolsNameAndAFingerprintIsAnEntry_AndTheSettingsDropAnythingElse(string entry, bool wellFormed)
    {
        Assert.Equal(wellFormed, StandingApprovals.IsWellFormed(entry));

        // Listed twice, it is listed once when it is one, and not at all when it is not.
        var cleaned = SettingsValidator.Sanitize(new AppSettings { Permissions = new PermissionSettings { AlwaysAllowed = [entry, entry] } }, out var repaired);
        string[] expected = wellFormed ? [entry] : [];
        Assert.Equal(expected, cleaned.Permissions.AlwaysAllowed);
        Assert.Contains(repaired, issue => issue.Setting == "Permissions.AlwaysAllowed");
    }
}
