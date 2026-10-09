using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The permissions as the tools and services enforce them (PROJECT_SPEC §4.9, step 119): a permission is checked in code before a tool runs, a permission set to ask every time is asked
/// about for each call, and what the user lets one connected app do (read, change, network, account) is held by the connection and the tools it offers, not by a switch on a page.
/// </summary>
public sealed class PermissionEnforcementTests
{
    private static ToolCall Call(string name, string arguments = "{}") => new("c1", name, arguments);

    private static SettingsPermissionPolicy PolicyFor(PermissionSettings permissions) =>
        new(new FixedSettings { Current = new AppSettings { Permissions = permissions } });

    private static PermissionSettings Asking(params PermissionCapability[] capabilities) =>
        capabilities.Aggregate(new PermissionSettings(), (settings, capability) => settings.WithMode(capability, PermissionMode.AskEveryTime));

    // A read-only tool that needs the Calendar permission and says whether the policy allowed it from inside the call, as a service the tool uses would see it.
    private static (HandlerTool Tool, List<bool> InsideAllowed) CalendarTool(IPermissionPolicy policy, RiskLevel risk = RiskLevel.ReadOnly)
    {
        var inside = new List<bool>();
        var tool = new HandlerTool(
            ToolDefinition.Create("read_events", "Reads.", [], risk, PermissionCapability.Calendar),
            async (call, _, _, token) =>
            {
                inside.Add((await policy.CheckAsync(PermissionCapability.Calendar, token)).IsAllowed);
                return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}");
            });
        return (tool, inside);
    }

    private static string Code(ToolResult result)
    {
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _), result.OutputJson);
        return code;
    }

    // ---- the executor and a permission set to ask each time -----------------------------------------------------------------

    [Fact]
    public async Task AToolThatNeedsAPermissionSetToAskIsAskedAboutAndRunsUnderThatOneYes()
    {
        var policy = PolicyFor(Asking(PermissionCapability.Calendar));
        var (tool, inside) = CalendarTool(policy);
        var confirmation = new FakeConfirmation(approve: true);

        var result = await new ToolExecutor([tool], confirmation, policy).ExecuteAsync(Call("read_events"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([true], inside);
        var question = Assert.Single(confirmation.Shown);
        Assert.Equal(ConfirmationKind.Access, question.Kind);
        Assert.Equal(PermissionCatalog.Get(PermissionCapability.Calendar).AskQuestion, question.Title);
        Assert.Equal("Allow once", question.ApproveLabel);
        Assert.Contains(question.Details, line => line.Label == "Needed for" && line.Value == "read_events");

        // The yes ended with the call: nothing stays allowed, and the next call is asked about again.
        Assert.False(PermissionApprovals.IsApproved(PermissionCapability.Calendar));
        Assert.False((await policy.CheckAsync(PermissionCapability.Calendar)).IsAllowed);
        await new ToolExecutor([tool], confirmation, policy).ExecuteAsync(Call("read_events"));
        Assert.Equal(2, confirmation.Asked);
    }

    [Fact]
    public async Task ANoToAPermissionThatAsksMeansTheToolDoesNotRunAndTheModelIsToldSo()
    {
        var policy = PolicyFor(Asking(PermissionCapability.Calendar));
        var (tool, inside) = CalendarTool(policy);

        var result = await new ToolExecutor([tool], new FakeConfirmation(approve: false), policy).ExecuteAsync(Call("read_events"));

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(ToolErrors.Declined, Code(result));
        Assert.Contains("did not allow Calendar", result.OutputJson, StringComparison.Ordinal);
        Assert.Empty(inside);
    }

    [Theory]
    [InlineData(ConfirmationDecision.NoAnswer, ToolErrors.NoAnswer)]
    [InlineData(ConfirmationDecision.CouldNotAsk, ToolErrors.CouldNotAsk)]
    public async Task AQuestionThatWasNotAnsweredOrCouldNotBeShownIsNotAYes(ConfirmationDecision decision, string code)
    {
        var policy = PolicyFor(Asking(PermissionCapability.Calendar));
        var (tool, inside) = CalendarTool(policy);

        var result = await new ToolExecutor([tool], new FakeConfirmation(approve: true) { Decision = decision }, policy).ExecuteAsync(Call("read_events"));

        Assert.Equal(code, Code(result));
        Assert.Empty(inside);
    }

    [Fact]
    public async Task WithNoOneToAskAPermissionSetToAskRefusesTheCall()
    {
        var policy = PolicyFor(Asking(PermissionCapability.Calendar));
        var (tool, inside) = CalendarTool(policy);

        var result = await new ToolExecutor([tool], permissions: null, policy: policy).ExecuteAsync(Call("read_events"));

        Assert.Equal(ToolErrors.CouldNotAsk, Code(result));
        Assert.Empty(inside);
    }

    [Fact]
    public async Task AllowedAsksNothingAndOffOrNotAvailableRefusesWithoutAsking()
    {
        var confirmation = new FakeConfirmation(approve: true);

        var allowedPolicy = PolicyFor(new PermissionSettings { Calendar = true });
        var (allowedTool, allowedInside) = CalendarTool(allowedPolicy);
        var allowed = await new ToolExecutor([allowedTool], confirmation, allowedPolicy).ExecuteAsync(Call("read_events"));

        var offPolicy = PolicyFor(new PermissionSettings { Calendar = false });
        var (offTool, offInside) = CalendarTool(offPolicy);
        var off = await new ToolExecutor([offTool], confirmation, offPolicy).ExecuteAsync(Call("read_events"));

        Assert.Equal(ToolResultStatus.Succeeded, allowed.Status);
        Assert.Equal([true], allowedInside);
        Assert.Equal(ToolErrors.PermissionOff, Code(off));
        Assert.Contains("turned off", off.OutputJson, StringComparison.Ordinal);
        Assert.Empty(offInside);
        Assert.Equal(0, confirmation.Asked);
    }

    [Fact]
    public async Task ACallThatChangesSomethingIsAskedAboutTheAccessFirstAndThenAboutWhatItWouldDo()
    {
        var policy = PolicyFor(Asking(PermissionCapability.Messaging));
        var ran = 0;
        var tool = new HandlerTool(
            ToolDefinition.Create("send_it", "Sends.", [], RiskLevel.SideEffect, PermissionCapability.Messaging),
            (call, _, _, _) =>
            {
                ran++;
                return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
            });
        var confirmation = new FakeConfirmation(approve: true);

        var result = await new ToolExecutor([tool], confirmation, policy).ExecuteAsync(Call("send_it"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(1, ran);
        Assert.Equal([ConfirmationKind.Access, ConfirmationKind.Other], confirmation.Shown.Select(shown => shown.Kind));

        // A no to the access question ends it: the call is never described to the user.
        var declined = new FakeConfirmation(approve: false);
        var refused = await new ToolExecutor([tool], declined, policy).ExecuteAsync(Call("send_it"));
        Assert.Equal(ToolErrors.Declined, Code(refused));
        Assert.Equal(1, declined.Asked);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task APermissionIsCheckedBeforeTheArgumentsAreLookedAtEvenWhenItAsks()
    {
        var policy = PolicyFor(Asking(PermissionCapability.Calendar));
        var tool = new HandlerTool(
            ToolDefinition.Create("read_events", "Reads.", [new ToolParameter("which", ToolParameterType.String, "Which.")], RiskLevel.ReadOnly, PermissionCapability.Calendar),
            (call, _, _, _) => Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}")));

        var result = await new ToolExecutor([tool], new FakeConfirmation(approve: false), policy).ExecuteAsync(Call("read_events", "garbage"));

        Assert.Equal(ToolErrors.Declined, Code(result));
    }

    [Fact]
    public async Task ADestructiveToolIsRefusedWhateverThePermissionsSayAndNothingIsAsked()
    {
        var everything = Enum.GetValues<PermissionCapability>().Aggregate(new PermissionSettings(), (settings, capability) => settings.With(capability, true));
        var confirmation = new FakeConfirmation(approve: true);
        var wipe = new FakeTool("wipe", RiskLevel.Destructive);

        var result = await new ToolExecutor([wipe], confirmation, PolicyFor(everything)).ExecuteAsync(Call("wipe"));

        Assert.Equal(ToolErrors.NotAllowed, Code(result));
        Assert.Equal(0, wipe.Runs);
        Assert.Equal(0, confirmation.Asked);
    }

    // ---- which permission a connected app's tool needs --------------------------------------------------------------------

    [Theory]
    [InlineData("list_events", PermissionCapability.Calendar)]
    [InlineData("getCalendarEvents", PermissionCapability.Calendar)]
    [InlineData("search_appointments", PermissionCapability.Calendar)]
    [InlineData("create_meeting", PermissionCapability.Calendar)]
    [InlineData("send_message", PermissionCapability.Messaging)]
    [InlineData("search_chats", PermissionCapability.Messaging)]
    [InlineData("listEmails", PermissionCapability.Messaging)]
    [InlineData("send_event_message", PermissionCapability.Messaging)]
    [InlineData("whatsapp_send", PermissionCapability.Messaging)]
    public void AToolAboutACalendarOrMessagesNeedsThePermissionThatCoversIt(string name, PermissionCapability expected) =>
        Assert.Equal(expected, McpToolPermission.Infer(name, null));

    [Theory]
    [InlineData("create_task")]
    [InlineData("listTasks")]
    [InlineData("search_notes")]
    [InlineData("get_pull_request")]
    [InlineData("eventually_consistent")]
    public void AToolAboutSomethingElseNeedsNoPermissionOfItsOwn(string name) => Assert.Null(McpToolPermission.Infer(name, null));

    [Fact]
    public void TheTitleCountsAndTheDescriptionDoesNot()
    {
        Assert.Equal(PermissionCapability.Calendar, McpToolPermission.Infer("lookup", "Find calendar items"));

        // A description that mentions messages is the app's own words about itself and decides nothing.
        var tool = Sample.Tool("create_task", "Creates a task and can send a message to the team about it.");
        Assert.Null(McpToolPermission.For(Sample.Remote(), tool));
    }

    [Fact]
    public void TheIntegrationsOwnRequirementComesBeforeTheOneInferredFromAName()
    {
        var integration = Sample.Remote() with { Permissions = new IntegrationPermissions { RequiredCapability = PermissionCapability.Files } };

        Assert.Equal(PermissionCapability.Files, McpToolPermission.For(integration, Sample.Tool("list_events")));
        Assert.Equal(PermissionCapability.Calendar, McpToolPermission.For(Sample.Remote(), Sample.Tool("list_events")));
    }

    [Fact]
    public void TheCatalogOfAnAppCarriesTheInferredPermissionOnEachTool()
    {
        var catalog = McpToolCatalogBuilder.Build(
            Sample.Remote("gcal", "Gcal"),
            [Sample.Tool("list_events"), Sample.Tool("send_message"), Sample.Tool("create_task")],
            new NoInvoker(),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(
            [PermissionCapability.Calendar, PermissionCapability.Messaging, null],
            catalog.Tools.Select(tool => tool.Definition.RequiredPermission));
    }

    [Fact]
    public async Task ACalendarAppIsNotReadWhileCalendarIsOffAndIsAskedAboutWhenItAsks()
    {
        var calendar = Sample.Remote("gcal", "Gcal") with { Permissions = new IntegrationPermissions { ReadOnlyTools = ["list_events"] } };
        var client = new StubMcpClient();
        client.Tools.Add(Sample.Tool("list_events", "Lists events.", """{"type":"object"}"""));

        // Off: the call is refused before the app is reached.
        await using (var off = new ConnectedAppsFixture([calendar], clients: _ => client, permissions: PolicyFor(new PermissionSettings { Calendar = false })))
        {
            var context = ConnectedAppsFixture.Context("list the events in gcal");
            await off.OfferedAsync(context);
            var refused = await off.CallAsync(context, "mcp_gcal_list_events", "{}");

            Assert.Equal(ToolErrors.PermissionOff, Code(refused));
            Assert.Empty(client.Calls);
        }

        // Asking: the user is asked, and only a yes reaches the app.
        await using (var asking = new ConnectedAppsFixture([calendar], clients: _ => client, permissions: PolicyFor(Asking(PermissionCapability.Calendar))))
        {
            var context = ConnectedAppsFixture.Context("list the events in gcal");
            await asking.OfferedAsync(context);
            var answered = await asking.CallAsync(context, "mcp_gcal_list_events", "{}");

            Assert.Equal(ToolResultStatus.Succeeded, answered.Status);
            Assert.Equal(ConfirmationKind.Access, Assert.Single(asking.Confirmation.Shown).Kind);
            Assert.Single(client.Calls);
        }
    }

    // ---- what the user lets one connected app do ---------------------------------------------------------------------------

    [Fact]
    public void WhenReadingIsOffNoToolThatOnlyReadsIsOfferedAndTheOthersAreUnchanged()
    {
        var reads = new IntegrationPermissions { ReadOnlyTools = ["list"], AllowReads = false };
        var read = Sample.Tool("list");
        var change = Sample.Tool("create");

        Assert.Null(McpToolPolicy.RiskOf(reads, read));
        Assert.Equal(RiskLevel.SideEffect, McpToolPolicy.RiskOf(reads, change));
        Assert.Equal(RiskLevel.ReadOnly, McpToolPolicy.RiskOf(reads with { AllowReads = true }, read));
    }

    [Fact]
    public void WhenChangingIsOffOnlyWhatIsKnownToReadIsOfferedAndWithBothOffNothingIs()
    {
        var both = new IntegrationPermissions { ReadOnlyTools = ["list"], AllowSideEffects = false };

        Assert.Equal(RiskLevel.ReadOnly, McpToolPolicy.RiskOf(both, Sample.Tool("list")));
        Assert.Null(McpToolPolicy.RiskOf(both, Sample.Tool("create")));
        Assert.Null(McpToolPolicy.RiskOf(both with { AllowReads = false }, Sample.Tool("list")));
    }

    [Fact]
    public async Task AnAppWhoseNetworkAccessIsOffIsNotConnectedToAndLocalOnlyIsNotNeededToSayNo()
    {
        var app = ConnectedAppsFixture.TodoistApp(current => current with { AllowNetwork = false });
        await using var apps = new ConnectedAppsFixture([app], localOnly: false);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task TurningNetworkAccessOffAfterAConnectionStopsCallsAtOnce()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], localOnly: false);
        var context = ConnectedAppsFixture.Context("list my tasks in todoist");
        await apps.OfferedAsync(context);

        await apps.Integrations.UpdateAsync("todoist", app => app with { Permissions = app.Permissions with { AllowNetwork = false } });
        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolErrors.PermissionOff, Code(result));
        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Fact]
    public async Task NetworkAccessOffChangesNothingForAProgramThatIsNotRecordedAsReachingOut()
    {
        var program = Sample.Program("localapp", "Local App") with { Permissions = new IntegrationPermissions { AllowNetwork = false } };
        await using var apps = new ConnectedAppsFixture([program], localOnly: true);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("use local app"));

        Assert.Equal(1, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task NetworkAccessOffStopsAProgramThatIsRecordedAsReachingOut()
    {
        var program = Sample.Program("localapp", "Local App") with { Permissions = new IntegrationPermissions { LeavesThisPc = true, AllowNetwork = false } };
        await using var apps = new ConnectedAppsFixture([program], localOnly: false);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("use local app"));

        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    private static InstalledIntegration SignedIn(bool allowAccount) => ConnectedAppsFixture.TodoistApp() with
    {
        Authentication = new IntegrationAuthentication
        {
            Kind = IntegrationAuthKind.BearerToken,
            State = IntegrationAuthState.Ready,
            Secrets = [new IntegrationSecretBinding("Authorization", "todoist.token")],
        },
        Permissions = ConnectedAppsFixture.TodoistApp().Permissions with { AllowAccountAccess = allowAccount },
    };

    [Fact]
    public async Task AnAppThatSignsInIsNotConnectedToWhileItsAccountIsTakenAway()
    {
        await using var off = new ConnectedAppsFixture([SignedIn(allowAccount: false)], localOnly: false);
        await using var on = new ConnectedAppsFixture([SignedIn(allowAccount: true)], localOnly: false);

        var refused = await off.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        var served = await on.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.DoesNotContain(refused, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.Equal(0, off.Clients.CreateCalls);
        Assert.Contains(served, tool => tool.Name == "mcp_todoist_create_task");
    }

    [Fact]
    public async Task TheAccountChoiceChangesNothingForAnAppThatNeedsNoSignIn()
    {
        var app = ConnectedAppsFixture.TodoistApp(current => current with { AllowAccountAccess = false });
        await using var apps = new ConnectedAppsFixture([app], localOnly: false);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Contains(offered, tool => tool.Name == "mcp_todoist_create_task");
    }

    [Fact]
    public async Task TheAppsToolsFollowWhatTheUserAllowsAtOnce()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], localOnly: false);
        var context = ConnectedAppsFixture.Context("list my tasks in todoist");
        var before = await apps.OfferedAsync(context);

        await apps.Integrations.UpdateAsync("todoist", app => app with { Permissions = app.Permissions with { AllowReads = false } });
        var withoutReads = await apps.OfferedAsync(ConnectedAppsFixture.Context("list my tasks in todoist"));
        await apps.Integrations.UpdateAsync("todoist", app => app with { Permissions = app.Permissions with { AllowReads = true, AllowSideEffects = false } });
        var withoutChanges = await apps.OfferedAsync(ConnectedAppsFixture.Context("list my tasks in todoist"));

        Assert.Contains(before, tool => tool.Name == "mcp_todoist_list_tasks");
        Assert.Contains(before, tool => tool.Name == "mcp_todoist_create_task");
        Assert.DoesNotContain(withoutReads, tool => tool.Name == "mcp_todoist_list_tasks");
        Assert.Contains(withoutReads, tool => tool.Name == "mcp_todoist_create_task");
        Assert.Contains(withoutChanges, tool => tool.Name == "mcp_todoist_list_tasks");
        Assert.DoesNotContain(withoutChanges, tool => tool.Name == "mcp_todoist_create_task");
    }

    [Fact]
    public void WhatIsNotSetByTheUserDefaultsToAllowedSoAnIntegrationInstalledBeforeIsUnchanged()
    {
        var defaults = IntegrationPermissions.Default;

        Assert.True(defaults.AllowReads);
        Assert.True(defaults.AllowSideEffects);
        Assert.True(defaults.AllowNetwork);
        Assert.True(defaults.AllowAccountAccess);
        Assert.True(defaults.AllowUpdates);
        Assert.Empty(IntegrationRules.Problems(Sample.Remote() with { Permissions = defaults with { AllowReads = false, AllowNetwork = false, AllowAccountAccess = false, AllowUpdates = false } }));
    }

    private sealed class NoInvoker : IMcpToolInvoker
    {
        public Task<McpToolResult> CallAsync(
            string integrationId, McpToolDescriptor tool, System.Text.Json.JsonElement arguments, bool safeToRepeat, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
