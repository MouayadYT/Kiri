using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>What the model is offered, and when an app is connected to: tools are loaded lazily, by the request.</summary>
public sealed class ConnectedAppsLazyLoadingTests
{
    [Fact]
    public async Task WithNoAppInstalledNothingIsOfferedBeyondTheBuiltInToolsAndNothingIsConnected()
    {
        await using var apps = new ConnectedAppsFixture([]);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("Add milk to my Todoist list"));

        Assert.Equal(["calculate"], offered.Select(tool => tool.Name));
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task ARequestThatIsNotAboutAnAppNeverConnectsToIt()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);

        foreach (var request in new[] { "What is 2 plus 2", "hello", "summarize this document", "" })
        {
            var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context(request));
            Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        }

        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task ARequestThatNamesTheAppConnectsToItAndOffersOnlyWhatFitsTheRequest()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("Add a task to buy milk in Todoist"));

        Assert.Equal(1, apps.Clients.CreateCalls);
        var mcp = offered.Where(tool => ConnectedAppTools.IsConnectedAppTool(tool.Name)).Select(tool => tool.Name).ToList();
        Assert.Contains("mcp_todoist_create_task", mcp);
        Assert.DoesNotContain("mcp_todoist_delete_all_tasks", mcp);
        Assert.Contains("calculate", offered.Select(tool => tool.Name));
    }

    [Fact]
    public async Task NoToolThatCouldDestroyDataIsEverOffered()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("use todoist to delete all my tasks"));

        Assert.DoesNotContain(offered, tool => tool.Name.Contains("delete_all", StringComparison.Ordinal));
        Assert.DoesNotContain(offered, tool => tool.RiskLevel == RiskLevel.Destructive);
    }

    [Fact]
    public async Task ARequestThatMentionsToolWordsOfAnAppConnectsToItWithoutNamingIt()
    {
        var app = ConnectedAppsFixture.TodoistApp() with
        {
            Name = "Jotter",
            Id = "jotter",
            Capabilities = new IntegrationCapabilities { ToolNames = ["createTask", "listTasks"] },
        };
        await using var apps = new ConnectedAppsFixture([app]);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("create a task to call the bank"));

        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.Contains(offered, tool => tool.Name == "mcp_jotter_create_task");
    }

    [Fact]
    public async Task OnlyTheAppsTheRequestIsAboutAreConnectedTo()
    {
        var connectedTo = new List<string>();
        var notes = Sample.Remote("notes", "Notes") with { Permissions = new IntegrationPermissions() };
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp(), notes],
            clients: integration =>
            {
                connectedTo.Add(integration.Id);
                return ConnectedAppsFixture.Todoist();
            });

        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Equal(["todoist"], connectedTo);
    }

    [Fact]
    public async Task TheToolsOfSeveralAppsAreOfferedTogetherUpToTheLimits()
    {
        var one = Sample.Remote("alpha", "Alpha");
        var two = Sample.Remote("beta", "Beta");
        await using var apps = new ConnectedAppsFixture(
            [one, two],
            clients: integration =>
            {
                var client = new StubMcpClient();
                for (var number = 0; number < 12; number++)
                {
                    client.Tools.Add(Sample.Tool("search_item_" + number, "Searches items of kind " + number + "."));
                }

                return client;
            });

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("search items in alpha and beta"));

        var mcp = offered.Where(tool => ConnectedAppTools.IsConnectedAppTool(tool.Name)).ToList();
        Assert.Equal(2, apps.Clients.CreateCalls);

        // Five of each would be ten; no more than eight tools of all apps are offered for one request.
        Assert.Equal(apps.Options.MaxToolsOffered, mcp.Count);
        Assert.Equal(5, mcp.Count(tool => tool.Name.StartsWith("mcp_alpha_", StringComparison.Ordinal)));
        Assert.Equal(3, mcp.Count(tool => tool.Name.StartsWith("mcp_beta_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AtMostSomeAppsHaveTheirToolsLoadedForOneRequest()
    {
        var installed = Enumerable.Range(0, 5).Select(number => Sample.Remote("app" + (char)('a' + number), "Common " + number)).ToList();
        await using var apps = new ConnectedAppsFixture(installed, clients: _ => ConnectedAppsFixture.Todoist());

        await apps.OfferedAsync(ConnectedAppsFixture.Context("use common stuff"));

        Assert.Equal(apps.Options.MaxIntegrationsPerRequest, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task WhatIsOfferedIsKeptWithinABudgetOfCharacters()
    {
        var options = new McpLoadingOptions { MaxDefinitionCharacters = 1500, MaxToolsOffered = 8, MaxToolsPerIntegration = 8 };
        await using var apps = new ConnectedAppsFixture(
            [Sample.Remote("alpha", "Alpha")],
            clients: _ =>
            {
                var client = new StubMcpClient();
                for (var number = 0; number < 8; number++)
                {
                    client.Tools.Add(Sample.Tool("search_" + number, new string('d', 380) + " search"));
                }

                return client;
            },
            options: options);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("search with alpha"));

        var mcp = offered.Where(tool => ConnectedAppTools.IsConnectedAppTool(tool.Name)).ToList();
        Assert.NotEmpty(mcp);
        Assert.True(mcp.Sum(tool => tool.Description.Length + tool.InputSchemaJson.Length) <= options.MaxDefinitionCharacters);
        Assert.True(mcp.Count < 8);
    }

    [Fact]
    public async Task TheToolsOfAConversationAreItsOwn()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        var first = ConnectedAppsFixture.Context("add a task to todoist");
        var second = ConnectedAppsFixture.Context("what is 2 plus 2");

        await apps.Tools.PrepareToolsAsync(first);
        await apps.Tools.PrepareToolsAsync(second);

        Assert.Contains(apps.Tools.ToolsFor(first), tool => tool.Name == "mcp_todoist_create_task");
        Assert.DoesNotContain(apps.Tools.ToolsFor(second), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
    }

    [Fact]
    public async Task WhatWasOfferedForOneRequestIsReplacedByTheNextRequestOfTheConversation()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        var conversation = Guid.NewGuid();
        var asking = ConnectedAppsFixture.Context("add a task to todoist", conversation);
        var other = ConnectedAppsFixture.Context("what is 2 plus 2", conversation);

        await apps.Tools.PrepareToolsAsync(asking);
        Assert.Contains(apps.Tools.ToolsFor(asking), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));

        await apps.Tools.PrepareToolsAsync(other);

        Assert.DoesNotContain(apps.Tools.ToolsFor(other), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.DoesNotContain(apps.Tools.ToolsFor(asking), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
    }

    [Fact]
    public async Task AfterAnAppsToolWasCalled_TheNextRequestsOfTheConversationGoOnWithTheApp_ForAWhile()
    {
        var clock = new ManualTimeProvider();
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], clock: clock);
        var conversation = Guid.NewGuid();
        var asking = ConnectedAppsFixture.Context("add a task to todoist", conversation);
        await apps.Tools.PrepareToolsAsync(asking);

        // The model calls the app's tool, and the Assistant then asks the user which list.
        var result = await apps.Executor.ExecuteAsync(new ToolCall("c1", "mcp_todoist_create_task", """{"task_title":"install word"}"""), asking);
        Assert.Equal(ToolResultStatus.Succeeded, result.Status);

        // The answer says nothing about the app. Its tools are offered all the same, the one that was just used among them, so the model has it to call again.
        var answer = ConnectedAppsFixture.Context("always put it in the first one", conversation);
        Assert.Contains(await apps.OfferedAsync(answer), tool => tool.Name == "mcp_todoist_create_task");
        Assert.Equal(ToolResultStatus.Succeeded, (await apps.Executor.ExecuteAsync(
            new ToolCall("c2", "mcp_todoist_create_task", """{"task_title":"install word"}"""), answer)).Status);

        // Another conversation's request in the same words is about no app.
        Assert.DoesNotContain(
            await apps.OfferedAsync(ConnectedAppsFixture.Context("always put it in the first one")), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));

        // Much later the conversation has moved on, and is about an app again only when it names one.
        clock.Advance(apps.Options.FollowUpLifetime + TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(
            await apps.OfferedAsync(ConnectedAppsFixture.Context("and what about that", conversation)), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
    }

    [Fact]
    public async Task ToolsAreOnlyOfferedForTheRequestTheyWerePreparedFor()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        var conversation = Guid.NewGuid();
        await apps.Tools.PrepareToolsAsync(ConnectedAppsFixture.Context("add a task to todoist", conversation));

        Assert.DoesNotContain(apps.Tools.ToolsFor(ConnectedAppsFixture.Context("something else", conversation)), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.DoesNotContain(apps.Tools.ToolsFor(new ToolContext(conversation)), tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
    }

    [Fact]
    public async Task TheListOfToolsIsReadOnceWhileItIsFresh()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        await apps.OfferedAsync(ConnectedAppsFixture.Context("list my tasks in todoist"));
        await apps.OfferedAsync(ConnectedAppsFixture.Context("todoist please"));

        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.Equal(1, apps.Clients.Created[0].ConnectCalls);
        Assert.Equal(1, apps.Clients.Created[0].ListCalls);
    }

    [Fact]
    public async Task TheListIsReadAgainAfterItGrowsOld()
    {
        var clock = new ManualTimeProvider();
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], clock: clock);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        clock.Advance(apps.Options.CatalogLifetime + TimeSpan.FromSeconds(1));
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        // Moving the clock on also runs the idle check, which may end the first connection before the second request (a new one is then made):
        // what counts is how often the list was read, not on which connection.
        Assert.Equal(2, apps.Clients.Created.Sum(client => client.ListCalls));
    }

    [Fact]
    public async Task TheListIsReadAgainWhenTheServerSaysItChanged()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        apps.Clients.Created[0].Tools.Add(Sample.Tool("addLabel", "Adds a label to a task."));
        apps.Clients.Created[0].RaiseToolsChanged();
        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a label with todoist"));

        Assert.Equal(2, apps.Clients.Created[0].ListCalls);
        Assert.Contains(offered, tool => tool.Name == "mcp_todoist_add_label");
    }

    [Fact]
    public async Task SeveralRequestsAtOnceShareOneConnection()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"))));

        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.Equal(1, apps.Clients.Created[0].ListCalls);
    }

    [Fact]
    public async Task ARequestThatCannotWaitGoesOnWithoutTheToolsAndTheNextOneFindsThem()
    {
        var gate = new TaskCompletionSource();
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                var client = ConnectedAppsFixture.Todoist();
                client.BeforeConnect = () => gate.Task;
                return client;
            },
            options: new McpLoadingOptions { PrepareTimeout = TimeSpan.FromMilliseconds(150) });

        var first = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        Assert.DoesNotContain(first, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));

        gate.SetResult();
        await ConnectedAppsFixture.EventuallyAsync(() => apps.Clients.Created[0].ListCalls == 1);
        var second = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Contains(second, tool => tool.Name == "mcp_todoist_create_task");
        Assert.Equal(1, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task ARequestThatIsCancelledWhileWaitingStopsWaiting()
    {
        var gate = new TaskCompletionSource();
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                var client = ConnectedAppsFixture.Todoist();
                client.BeforeConnect = () => gate.Task;
                return client;
            });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => apps.Tools.PrepareToolsAsync(ConnectedAppsFixture.Context("add a task to todoist"), cancel.Token));

        gate.SetResult();
    }

    [Fact]
    public async Task AnAppThatCannotBeReachedIsLeftOutAndLeftAloneForAWhile()
    {
        var clock = new ManualTimeProvider();
        var attempts = 0;
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                attempts++;
                var client = ConnectedAppsFixture.Todoist();
                client.ConnectFailure = new McpException(McpFailure.ConnectFailed);
                return client;
            },
            clock: clock);

        var first = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        var second = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.DoesNotContain(first, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.DoesNotContain(second, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.Equal(1, attempts);

        clock.Advance(apps.Options.FailureBackoff + TimeSpan.FromSeconds(1));
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AnAppThatComesBackAfterAFailureIsOfferedAgain()
    {
        var clock = new ManualTimeProvider();
        var fail = true;
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                var client = ConnectedAppsFixture.Todoist();
                client.ConnectFailure = fail ? new McpException(McpFailure.ConnectFailed) : null;
                return client;
            },
            clock: clock);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        fail = false;
        clock.Advance(apps.Options.FailureBackoff + TimeSpan.FromSeconds(1));
        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Contains(offered, tool => tool.Name == "mcp_todoist_create_task");
    }

    [Fact]
    public async Task ADisabledAppIsNeverConnectedToAndOffersNothing()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp() with { Enabled = false }]);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnAppThatIsTurnedOffAfterItsToolsWereLoadedIsNoLongerOffered()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        await apps.Integrations.SetEnabledAsync("todoist", false);
        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
    }

    [Fact]
    public async Task AToolIsOfferedWithTheDescriptionAndSchemaTheModelCanUse()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        var create = offered.Single(tool => tool.Name == "mcp_todoist_create_task");
        Assert.Equal("[Todoist] Creates a task in the to-do list.", create.Description);
        using var schema = JsonDocument.Parse(create.InputSchemaJson);
        Assert.Equal(["task_title", "due_date"], schema.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal("task_title", Assert.Single(schema.RootElement.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal(RiskLevel.SideEffect, create.RiskLevel);
    }

    [Fact]
    public async Task TheRegistryFindsAToolItHasLoadedByName()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Equal("mcp_todoist_create_task", apps.Tools.Find("mcp_todoist_create_task")!.Name);
        Assert.Null(apps.Tools.Find("mcp_todoist_nothing"));
        Assert.Equal("calculate", apps.Tools.Find("calculate")!.Name);
    }

    [Fact]
    public async Task TheBuiltInCatalogDoesNotListConnectedAppsToolsAsRegistered()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Equal(["calculate"], apps.Tools.Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task WhatTheAppRecordsAboutTheConnectionIsKeptWithTheIntegration()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        var integration = (await apps.Integrations.GetAsync("todoist"))!;
        Assert.Equal(IntegrationHealthStatus.Healthy, integration.Health.Status);
        Assert.NotNull(integration.Health.CheckedAt);
        Assert.Equal(IntegrationAuthState.NotRequired, integration.Authentication.State);
        Assert.True(integration.Capabilities.Tools);
        Assert.Equal("2026-07-28", integration.Capabilities.ProtocolVersion);
        Assert.Equal(["createTask", "listTasks"], integration.Capabilities.ToolNames);
    }

    [Fact]
    public async Task ASignInThatWorksIsRecordedAsReady()
    {
        var app = ConnectedAppsFixture.TodoistApp() with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.BearerToken,
                State = IntegrationAuthState.Unknown,
                Secrets = [new IntegrationSecretBinding("Authorization", "mcp.todoist.token")],
            },
        };
        await using var apps = new ConnectedAppsFixture([app]);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Equal(IntegrationAuthState.Ready, (await apps.Integrations.GetAsync("todoist"))!.Authentication.State);
    }

    [Theory]
    [InlineData(McpFailure.AuthRequired, IntegrationHealthStatus.AuthRequired, IntegrationAuthState.NeedsSignIn)]
    [InlineData(McpFailure.Forbidden, IntegrationHealthStatus.AuthRequired, IntegrationAuthState.Rejected)]
    [InlineData(McpFailure.ConnectFailed, IntegrationHealthStatus.Unreachable, IntegrationAuthState.NotRequired)]
    [InlineData(McpFailure.LaunchFailed, IntegrationHealthStatus.Unreachable, IntegrationAuthState.NotRequired)]
    [InlineData(McpFailure.TimedOut, IntegrationHealthStatus.Unreachable, IntegrationAuthState.NotRequired)]
    [InlineData(McpFailure.Unsupported, IntegrationHealthStatus.Incompatible, IntegrationAuthState.NotRequired)]
    [InlineData(McpFailure.Protocol, IntegrationHealthStatus.Failed, IntegrationAuthState.NotRequired)]
    public async Task AFailureToConnectIsRecordedAsTheHealthAndTheSignInStateItMeans(McpFailure failure, IntegrationHealthStatus health, IntegrationAuthState auth)
    {
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                var client = ConnectedAppsFixture.Todoist();
                client.ConnectFailure = new McpException(failure, serverMessage: "private words from the server");
                return client;
            });

        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        var integration = (await apps.Integrations.GetAsync("todoist"))!;
        Assert.Equal(health, integration.Health.Status);
        Assert.Equal(failure, integration.Health.Failure);
        Assert.Equal(1, integration.Health.ConsecutiveFailures);
        Assert.Equal(auth, integration.Authentication.State);
        Assert.DoesNotContain("private words", JsonSerializer.Serialize(apps.Store.Saved, IntegrationJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerFoundToSpeakTheDeprecatedTransportIsReachedThatWayNextTime()
    {
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                var client = ConnectedAppsFixture.Todoist();
                client.TransportKind = McpTransportKind.LegacySse;
                client.Server = new McpServerInfo("Old", "1", "2024-11-05", new McpServerCapabilities(true, false, false, false));
                return client;
            });

        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        var integration = (await apps.Integrations.GetAsync("todoist"))!;
        Assert.Equal(McpTransportKind.LegacySse, integration.Transport.Kind);
        Assert.Equal("2024-11-05", integration.Capabilities.ProtocolVersion);
    }

    [Fact]
    public async Task ARecordThatChangedInAWayThatMattersIsNotConnectedToAsItWas()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        var first = apps.Clients.Created[0];

        await apps.Integrations.UpdateAsync("todoist", integration => integration with { Transport = integration.Transport with { Endpoint = "https://other.example.com/mcp" } });
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Equal(2, apps.Clients.CreateCalls);
        await ConnectedAppsFixture.EventuallyAsync(() => first.Disposed == 1);
    }

    [Fact]
    public async Task WhatTheAppRecordsItselfDoesNotMakeItReconnect()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.Equal(0, apps.Clients.Created[0].Disposed);
    }

    [Fact]
    public async Task AnIdleConnectionIsClosedAndAnotherIsMadeWhenItIsNeeded()
    {
        var clock = new ManualTimeProvider();
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], clock: clock);
        var context = ConnectedAppsFixture.Context("add a task to todoist");
        await apps.OfferedAsync(context);

        clock.Advance(apps.Options.IdleTimeout + apps.Options.ReapInterval + TimeSpan.FromSeconds(1));

        await ConnectedAppsFixture.EventuallyAsync(() => apps.Clients.Created[0].Disposed == 1);
        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");
        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(2, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task WhatIsInstalledCannotBeReadSoNothingIsLoadedAndTheRequestGoesOn()
    {
        var store = new MemoryIntegrationStore(ConnectedAppsFixture.TodoistApp()) { FailReads = true };
        await using var apps = new ConnectedAppsFixture([], store: store);

        // Not a failure of the request: the tools of the apps are left out, and the built-in ones remain.
        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        var direct = await apps.Manager.GetCatalogAsync("todoist", TimeSpan.FromSeconds(5), default);

        Assert.Equal(["calculate"], offered.Select(tool => tool.Name));
        Assert.Null(direct);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AToolIsNotCalledWhenWhatIsInstalledCannotBeReadSoItCannotBeKnownToBeAllowed()
    {
        var store = new MemoryIntegrationStore(ConnectedAppsFixture.TodoistApp());
        await using var apps = new ConnectedAppsFixture([], store: store);
        var context = ConnectedAppsFixture.Context("list my tasks in todoist");
        await apps.OfferedAsync(context);

        // The list was read once and is held in memory, so a store that fails afterwards changes nothing...
        store.FailReads = true;
        Assert.Equal(ToolResultStatus.Succeeded, (await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}")).Status);

        // ...but a registry that has never been able to read it fails closed.
        var unread = new InstalledIntegrationRegistry(new MemoryIntegrationStore { FailReads = true }, NullLogger<InstalledIntegrationRegistry>.Instance);
        var manager = new McpConnectionManager(unread, apps.Clients, apps.Settings, TimeProvider.System, apps.Options, NullLogger<McpConnectionManager>.Instance);
        var exception = await Assert.ThrowsAsync<McpException>(() => manager.CallAsync("todoist", Sample.Tool("listTasks"), Sample.Json("{}"), false, default));
        Assert.Equal(McpFailure.Blocked, exception.Failure);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task TwoAppsWithLongIdsThatBeginAlikeAreOfferedAndCalledUnderTheirOwnNames()
    {
        var one = Sample.Remote("averylongintegrationidnumberone", "Common App One");
        var two = Sample.Remote("averylongintegrationidnumbertwo", "Common App Two");
        await using var apps = new ConnectedAppsFixture(
            [one, two],
            clients: integration =>
            {
                var client = new StubMcpClient { OnCall = (tool, _, _) => Task.FromResult(Sample.Text("from " + integration.Id)) };
                client.Tools.Add(Sample.Tool("search_item", "Searches items.", readOnly: null));
                return client;
            });
        await apps.Integrations.UpdateAsync(one.Id, app => app with { Permissions = new IntegrationPermissions { ReadOnlyTools = ["search_item"] } });
        await apps.Integrations.UpdateAsync(two.Id, app => app with { Permissions = new IntegrationPermissions { ReadOnlyTools = ["search_item"] } });
        var context = ConnectedAppsFixture.Context("use common app one and common app two to search an item");

        var offered = (await apps.OfferedAsync(context)).Where(tool => ConnectedAppTools.IsConnectedAppTool(tool.Name)).ToList();

        Assert.Equal(2, offered.Count);
        Assert.Equal(2, offered.Select(tool => tool.Name).Distinct().Count());
        var results = new List<string>();
        foreach (var tool in offered)
        {
            var result = await apps.CallAsync(context, tool.Name, """{"text":"x"}""");
            using var output = JsonDocument.Parse(result.OutputJson);
            results.Add(output.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!);
        }

        Assert.Equal(["from " + one.Id, "from " + two.Id], results.Order());
    }

    [Fact]
    public async Task AContainerThatIsDisposedSynchronouslyEndsTheConnectionsToo()
    {
        var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        // A service provider's Dispose() refuses a service that can only be disposed asynchronously.
        ((IDisposable)apps.Manager).Dispose();

        Assert.Equal(1, apps.Clients.Created[0].Disposed);
    }

    [Fact]
    public async Task ADisposedManagerEndsEveryConnection()
    {
        var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        await apps.DisposeAsync();

        Assert.Equal(1, apps.Clients.Created[0].Disposed);
        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));
        Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
    }
}

/// <summary>Where an app may be reached: Local Only mode, and what leaves this PC.</summary>
public sealed class ConnectedAppsLocalOnlyTests
{
    [Fact]
    public async Task AnAppOverANetworkIsNotConnectedToWhileLocalOnlyIsOn()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], localOnly: true);

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnAppOverANetworkIsConnectedToOnceLocalOnlyIsOff()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], localOnly: true);
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        apps.Settings.Current = apps.Settings.Current with { Privacy = apps.Settings.Current.Privacy with { LocalOnly = false } };
        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        Assert.Contains(offered, tool => tool.Name == "mcp_todoist_create_task");
    }

    [Fact]
    public async Task AnAppOnThisPcIsConnectedToWhileLocalOnlyIsOn()
    {
        var local = Sample.Loopback("notes", "Notes");
        var program = Sample.Program("localapp", "Local App");
        await using var apps = new ConnectedAppsFixture([local, program], localOnly: true);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("use notes and local app"));

        Assert.Equal(2, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AProgramThatSaysItReachesOutItselfIsNotConnectedToWhileLocalOnlyIsOn()
    {
        var program = Sample.Program("localapp", "Local App") with { Permissions = new IntegrationPermissions { LeavesThisPc = true } };
        await using var apps = new ConnectedAppsFixture([program], localOnly: true);

        await apps.OfferedAsync(ConnectedAppsFixture.Context("use local app"));

        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task TurningLocalOnOnAfterAConnectionStopsCallsAtOnce()
    {
        await using var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()], localOnly: false);
        var context = ConnectedAppsFixture.Context("list my tasks in todoist");
        await apps.OfferedAsync(context);

        apps.Settings.Current = apps.Settings.Current with { Privacy = apps.Settings.Current.Privacy with { LocalOnly = true } };
        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.PermissionOff, code);
        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Theory]
    [InlineData("https://mcp.example.com/mcp", true)]
    [InlineData("http://localhost:3000/mcp", false)]
    [InlineData("http://127.0.0.1:9/mcp", false)]
    public void AnAddressOnThisPcDoesNotLeaveIt(string endpoint, bool leaves) =>
        Assert.Equal(leaves, McpConnectionManager.LeavesThisPc(Sample.Remote(endpoint: endpoint)));
}

/// <summary>A call of a connected app's tool goes through the one executor, under every rule that holds for a built-in tool.</summary>
public sealed class ConnectedAppsExecutionTests
{
    private static async Task<(ConnectedAppsFixture Apps, ToolContext Context)> PreparedAsync(
        string request = "add a task to todoist and list my tasks", bool confirm = true, IPermissionPolicy? permissions = null, Func<IntegrationPermissions, IntegrationPermissions>? permissionsOf = null)
    {
        var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp(permissionsOf)], confirm: confirm, permissions: permissions);
        var context = ConnectedAppsFixture.Context(request);
        await apps.OfferedAsync(context);
        return (apps, context);
    }

    [Fact]
    public async Task AToolTheUserVettedAsReadOnlyRunsWithoutBeingConfirmed()
    {
        var (apps, context) = await PreparedAsync(confirm: false);
        await using var cleanup = apps;

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", """{"status":"open"}""");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(0, apps.Confirmation.Asked);
        var call = Assert.Single(apps.Clients.Created[0].Calls);
        Assert.Equal("listTasks", call.Tool);
        Assert.Equal("""{"status":"open"}""", call.Arguments);
        using var output = JsonDocument.Parse(result.OutputJson);
        Assert.Equal("Todoist", output.RootElement.GetProperty("app").GetString());
        Assert.Equal("called listTasks", output.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("mcp_todoist_list_tasks", result.ToolName);
    }

    [Fact]
    public async Task AToolThatChangesSomethingRunsOnlyAfterTheUserConfirms()
    {
        var (apps, context) = await PreparedAsync(confirm: true);
        await using var cleanup = apps;

        var result = await apps.CallAsync(context, "mcp_todoist_create_task", """{"task_title":"buy milk","due_date":"tomorrow"}""");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(1, apps.Confirmation.Asked);
        var call = Assert.Single(apps.Clients.Created[0].Calls);

        // The server is given the arguments under the names it knows them by.
        Assert.Equal("createTask", call.Tool);
        Assert.Equal("""{"taskTitle":"buy milk","dueDate":"tomorrow"}""", call.Arguments);
    }

    [Fact]
    public async Task AToolThatChangesSomethingIsNotRunWhenTheUserDeclines()
    {
        var (apps, context) = await PreparedAsync(confirm: false);
        await using var cleanup = apps;

        var result = await apps.CallAsync(context, "mcp_todoist_create_task", """{"task_title":"buy milk"}""");

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(1, apps.Confirmation.Asked);
        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Fact]
    public async Task WithNoWayToAskTheUserAToolThatChangesSomethingIsNeverRun()
    {
        var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await using var cleanup = apps;
        var context = ConnectedAppsFixture.Context("add a task to todoist");
        await apps.OfferedAsync(context);
        var executor = new ToolExecutor([], permissions: null, policy: new FakePermissions(true), dynamicTools: apps.Source);

        var result = await executor.ExecuteAsync(new ToolCall("c1", "mcp_todoist_create_task", """{"task_title":"x"}"""), context);

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Fact]
    public async Task AServersHintThatAToolOnlyReadsDoesNotSkipTheConfirmationUnlessTheUserTrustsIt()
    {
        var app = ConnectedAppsFixture.TodoistApp() with { Permissions = new IntegrationPermissions() };
        await using var apps = new ConnectedAppsFixture(
            [app],
            clients: _ =>
            {
                var client = new StubMcpClient();
                client.Tools.Add(Sample.Tool("peek", "Peeks at a task.", readOnly: true));
                return client;
            },
            confirm: false);
        var context = ConnectedAppsFixture.Context("peek at a task in todoist");
        await apps.OfferedAsync(context);

        var result = await apps.CallAsync(context, "mcp_todoist_peek", """{"text":"x"}""");

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(1, apps.Confirmation.Asked);
    }

    [Fact]
    public async Task ANameThatWasNotOfferedIsNotAToolWhateverTheModelWrites()
    {
        var (apps, context) = await PreparedAsync("add a task to todoist");
        await using var cleanup = apps;

        foreach (var name in new[] { "mcp_todoist_delete_all_tasks", "mcp_todoist_nothing", "mcp_other_app_tool", "deleteAllTasks", "createTask", "mcp_todoist_createTask" })
        {
            var result = await apps.CallAsync(context, name, "{}");
            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
            Assert.Equal(ToolErrors.UnknownTool, code);
        }

        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Fact]
    public async Task AToolOfAnAppThatWasNotOfferedForTheRequestCannotBeCalledEvenIfItIsInstalledAndLoaded()
    {
        var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await using var cleanup = apps;
        await apps.OfferedAsync(ConnectedAppsFixture.Context("add a task to todoist"));

        // Another conversation, and another request of this one, were not offered the tool.
        var elsewhere = await apps.CallAsync(ConnectedAppsFixture.Context("what is 2 plus 2"), "mcp_todoist_create_task", """{"task_title":"x"}""");
        var calledDirectly = await apps.Executor.ExecuteAsync(new ToolCall("c1", "mcp_todoist_create_task", """{"task_title":"x"}"""));

        Assert.True(ToolErrors.TryRead(elsewhere.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.UnknownTool, code);
        Assert.True(ToolErrors.TryRead(calledDirectly.OutputJson, out var directCode, out _));
        Assert.Equal(ToolErrors.UnknownTool, directCode);
        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Fact]
    public async Task TheArgumentsAreCheckedAgainstTheSchemaBeforeTheAppIsAsked()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;

        var missing = await apps.CallAsync(context, "mcp_todoist_create_task", "{}");
        var wrongType = await apps.CallAsync(context, "mcp_todoist_create_task", """{"task_title":5}""");
        var unknown = await apps.CallAsync(context, "mcp_todoist_create_task", """{"task_title":"x","priority":1}""");
        var serverName = await apps.CallAsync(context, "mcp_todoist_create_task", """{"taskTitle":"x"}""");
        var badEnum = await apps.CallAsync(context, "mcp_todoist_list_tasks", """{"status":"maybe"}""");

        foreach (var result in new[] { missing, wrongType, unknown, serverName, badEnum })
        {
            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
            Assert.Equal(ToolErrors.InvalidArguments, code);
        }

        Assert.Empty(apps.Clients.Created[0].Calls);
        Assert.Equal(0, apps.Confirmation.Asked);
    }

    [Fact]
    public async Task ThePermissionTheIntegrationNeedsIsCheckedBeforeAnythingIsAsked()
    {
        var permissions = new DenyingPermissions(PermissionCapability.Calendar);
        var (apps, context) = await PreparedAsync(permissions: permissions, permissionsOf: current => current with { RequiredCapability = PermissionCapability.Calendar });
        await using var cleanup = apps;

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out var message));
        Assert.Equal(ToolErrors.PermissionOff, code);
        Assert.Contains("Calendar", message, StringComparison.Ordinal);
        Assert.Empty(apps.Clients.Created[0].Calls);
        Assert.Equal(0, apps.Confirmation.Asked);
    }

    [Fact]
    public async Task APermissionThatIsOnLetsTheToolRun()
    {
        var permissions = new DenyingPermissions(PermissionCapability.Messaging);
        var (apps, context) = await PreparedAsync(permissions: permissions, permissionsOf: current => current with { RequiredCapability = PermissionCapability.Calendar });
        await using var cleanup = apps;

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Contains(PermissionCapability.Calendar, permissions.Asked);
    }

    [Fact]
    public async Task AnIntegrationThatIsTurnedOffBetweenTheOfferAndTheCallIsNotCalled()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;

        await apps.Integrations.SetEnabledAsync("todoist", false);
        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.PermissionOff, code);
        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Fact]
    public async Task AnIntegrationThatIsRemovedBetweenTheOfferAndTheCallIsNotCalled()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;

        await apps.Integrations.RemoveAsync("todoist");
        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Empty(apps.Clients.Created[0].Calls);
    }

    [Fact]
    public async Task AToolThatSaysItFailedIsAFailedResultWithItsWords()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;
        apps.Clients.Created[0].OnCall = (_, _, _) => Task.FromResult(new McpToolResult(true, [new McpContentBlock(McpContentKind.Text, "The date is in the past.", null, null, null)], null));

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out _, out var message));
        Assert.Contains("The date is in the past.", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(McpFailure.AuthRequired, ToolErrors.Failed, "sign in")]
    [InlineData(McpFailure.Forbidden, ToolErrors.Failed, "sign in")]
    [InlineData(McpFailure.TimedOut, ToolErrors.TimedOut, "did not answer in time")]
    [InlineData(McpFailure.ConnectFailed, ToolErrors.Failed, "could not be reached")]
    [InlineData(McpFailure.Protocol, ToolErrors.Failed, "could not be reached")]
    [InlineData(McpFailure.TooLarge, ToolErrors.ResultTooLarge, "more than can be given")]
    [InlineData(McpFailure.Unsupported, ToolErrors.Failed, "cannot do")]
    public async Task AFailureOfTheCallIsAFailedResultThatSaysWhatToDo(McpFailure failure, string expectedCode, string expectedWords)
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;
        apps.Clients.Created[0].OnCall = (_, _, _) => throw new McpException(failure, serverMessage: "the private words of the server");

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out var message));
        Assert.Equal(expectedCode, code);
        Assert.Contains(expectedWords, message, StringComparison.Ordinal);
        Assert.DoesNotContain("private words", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorTheServerReportsIsPassedOnCleanedSoTheModelCanCorrectTheCall()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;
        apps.Clients.Created[0].OnCall = (_, _, _) => throw new McpException(McpFailure.Server, rpcCode: -32602, serverMessage: "Invalid params: <b>status</b> is required");

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.True(ToolErrors.TryRead(result.OutputJson, out _, out var message));
        Assert.Contains("Invalid params", message, StringComparison.Ordinal);
        Assert.Contains("is required", message, StringComparison.Ordinal);
        Assert.DoesNotContain('<', message);
    }

    [Fact]
    public async Task ACallThatTheCallerCancelsEndsAtOnce()
    {
        var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await using var cleanup = apps;
        var context = ConnectedAppsFixture.Context("list my tasks in todoist");
        await apps.OfferedAsync(context);
        apps.Clients.Created[0].OnCall = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Sample.Text("never");
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apps.Executor.ExecuteAsync(new ToolCall("c1", "mcp_todoist_list_tasks", "{}"), context, cancel.Token));
    }

    [Fact]
    public async Task AResultTooLargeToCarryIsCutByTheMapperBeforeTheExecutorSeesIt()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;
        apps.Clients.Created[0].OnCall = (_, _, _) => Task.FromResult(Sample.Text(new string('z', 500_000)));

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.True(result.OutputJson.Length < ToolExecutor.MaxResultLength);
        using var output = JsonDocument.Parse(result.OutputJson);
        Assert.True(output.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task TheExecutorRunsEachCallItIsGiven()
    {
        // Repeating a call within one answer is refused by the orchestrator (it keeps a key for each call), not by the executor.
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;

        await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");
        await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(2, apps.Clients.Created[0].Calls.Count);
    }

    [Fact]
    public async Task ACallAfterTheServerForgotItsSessionConnectsAgainAndIsMadeOnce()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;
        apps.Clients.Created[0].OnCall = (_, _, _) => throw new McpException(McpFailure.SessionExpired, httpStatus: 404);

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        // The first connection was dropped and a second made, on which the call was made again and worked.
        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(2, apps.Clients.CreateCalls);
        Assert.Single(apps.Clients.Created[0].Calls);
        Assert.Single(apps.Clients.Created[1].Calls);
        Assert.Equal(1, apps.Clients.Created[0].Disposed);
    }

    [Fact]
    public async Task ACallIsRetriedAfterALostSessionOnlyOnce()
    {
        var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                var client = ConnectedAppsFixture.Todoist();
                client.OnCall = (_, _, _) => throw new McpException(McpFailure.SessionExpired, httpStatus: 404);
                return client;
            });
        await using var cleanup = apps;
        var context = ConnectedAppsFixture.Context("list my tasks in todoist");
        await apps.OfferedAsync(context);

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(2, apps.Clients.CreateCalls);
        Assert.Single(apps.Clients.Created[0].Calls);
        Assert.Single(apps.Clients.Created[1].Calls);
    }

    [Fact]
    public async Task AConnectionThatEndedWhileACallThatChangesSomethingWasMadeIsNotRepeatedAndTheNextCallConnectsAgain()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;
        apps.Clients.Created[0].OnCall = (_, _, _) => throw new McpException(McpFailure.Closed);

        var first = await apps.CallAsync(context, "mcp_todoist_create_task", """{"task_title":"buy milk"}""");
        var second = await apps.CallAsync(context, "mcp_todoist_create_task", """{"task_title":"buy bread"}""");

        // The first may have been done before the program stopped, so it is not made again behind the user's back.
        Assert.Equal(ToolResultStatus.Failed, first.Status);
        Assert.Single(apps.Clients.Created[0].Calls);
        Assert.Equal(ToolResultStatus.Succeeded, second.Status);
        Assert.Equal(2, apps.Clients.CreateCalls);
        Assert.Equal(1, apps.Clients.Created[0].Disposed);
        Assert.Equal(IntegrationHealthStatus.Healthy, (await apps.Integrations.GetAsync("todoist"))!.Health.Status);
    }

    [Fact]
    public async Task AProgramThatStoppedDuringACallThatOnlyReadsIsStartedAgainAndTheCallIsMadeOnceMore()
    {
        var (apps, context) = await PreparedAsync();
        await using var cleanup = apps;
        apps.Clients.Created[0].OnCall = (_, _, _) => throw new McpException(McpFailure.Closed);

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(2, apps.Clients.CreateCalls);
        Assert.Single(apps.Clients.Created[0].Calls);
        Assert.Single(apps.Clients.Created[1].Calls);
        Assert.Equal(1, apps.Clients.Created[0].Disposed);
    }

    [Fact]
    public async Task AProgramThatKeepsStoppingIsGivenUpOnAfterOneRetryAndTheFailureIsSaid()
    {
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                var client = ConnectedAppsFixture.Todoist();
                client.OnCall = (_, _, _) => throw new McpException(McpFailure.Closed);
                return client;
            });
        var context = ConnectedAppsFixture.Context("add a task to todoist and list my tasks");
        await apps.OfferedAsync(context);

        var result = await apps.CallAsync(context, "mcp_todoist_list_tasks", "{}");

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.True(ToolErrors.TryRead(result.OutputJson, out _, out var message));
        Assert.Contains("could not be reached", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallThatCannotConnectIsAFailureRecordedInTheHealth()
    {
        await using var apps = new ConnectedAppsFixture(
            [ConnectedAppsFixture.TodoistApp()],
            clients: _ =>
            {
                var client = ConnectedAppsFixture.Todoist();
                client.ConnectFailure = new McpException(McpFailure.ConnectFailed);
                return client;
            });

        var exception = await Assert.ThrowsAsync<McpException>(() => apps.Manager.CallAsync("todoist", Sample.Tool("listTasks"), Sample.Json("{}"), false, default));

        Assert.Equal(McpFailure.ConnectFailed, exception.Failure);
        var health = (await apps.Integrations.GetAsync("todoist"))!.Health;
        Assert.Equal(IntegrationHealthStatus.Unreachable, health.Status);
        Assert.Equal(McpFailure.ConnectFailed, health.Failure);
    }

    [Fact]
    public async Task ALoggerThatSeesEverythingNeverSeesPrivateContent()
    {
        var logger = new CapturingLoggerFactory();
        var apps = new ConnectedAppsFixture([ConnectedAppsFixture.TodoistApp()]);
        await using var cleanup = apps;
        var manager = new McpConnectionManager(apps.Integrations, apps.Clients, apps.Settings, TimeProvider.System, apps.Options, logger.CreateLogger<McpConnectionManager>());
        var source = new McpToolSource(apps.Integrations, manager, LexicalMcpToolSelector.Instance, apps.Options, TimeProvider.System, logger.CreateLogger<McpToolSource>());
        const string Secret = "buy my secret milk";
        var context = ConnectedAppsFixture.Context("add a task to todoist: " + Secret);

        await source.PrepareAsync(context, default);

        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("secret milk", StringComparison.Ordinal) || line.Contains("mcp.example.com", StringComparison.Ordinal));
        await manager.DisposeAsync();
    }
}

/// <summary>A logger factory that keeps what is logged, rendered the way the privacy filter lets it through.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public List<string> Lines { get; } = [];

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Capturing(Lines);

    public ILogger<T> CreateLogger<T>() => new Capturing<T>(Lines);

    public void Dispose()
    {
    }

    private class Capturing(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // What the privacy filter would let through: values are passed through its rule, a string only under the names it allows.
            var rendered = new System.Text.StringBuilder(formatter(state, exception));
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var (name, value) in values)
                {
                    rendered.Append(' ').Append(name).Append('=').Append(Assistant.Core.Diagnostics.LogPrivacy.Sanitize(name, value));
                }
            }

            lock (lines)
            {
                lines.Add(rendered.ToString());
            }
        }
    }

    private sealed class Capturing<T>(List<string> lines) : Capturing(lines), ILogger<T>;
}
