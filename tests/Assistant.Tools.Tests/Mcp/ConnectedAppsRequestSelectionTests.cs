using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>
/// Which tools of a connected app a request is given (PROJECT_SPEC §4.8, step 110): the few that do what the request asks, however few words they share with it, and never
/// the tools of every installed app. A request that was set aside while an integration was installed is loaded the same way as any other.
/// </summary>
public sealed class ConnectedAppsRequestSelectionTests
{
    private const string AddMilk = "Add 'buy milk' to Microsoft To Do";

    private static readonly InstalledIntegration MicrosoftToDo = Sample.Remote("mstodo", "Microsoft To Do");

    private static McpTool Tool(string name, string description) =>
        McpToolCatalogBuilder.Build(Sample.Remote(), [Sample.Tool(name, description)], new NoInvoker(), DateTimeOffset.UnixEpoch).Tools.Single();

    private static IReadOnlyList<string> Chosen(string request, InstalledIntegration app, int max, params McpTool[] tools) =>
        [.. LexicalMcpToolSelector.Instance.SelectTools(request, app, tools, max).Select(tool => tool.Descriptor.Name)];

    private sealed class NoInvoker : IMcpToolInvoker
    {
        public Task<McpToolResult> CallAsync(
            string integrationId, McpToolDescriptor tool, System.Text.Json.JsonElement arguments, bool safeToRepeat, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static McpTool[] ToDoTools() =>
    [
        Tool("list_task_lists", "Lists the task lists."),
        Tool("get_user_profile", "Gets the user's profile."),
        Tool("list_tasks", "Lists the tasks of a list."),
        Tool("update_task", "Changes a task."),
        Tool("create_task", "Puts a new task in a list."),
        Tool("complete_task", "Marks a task done."),
    ];

    [Fact]
    public void TheToolThatDoesWhatIsAskedComesFirstEvenWhenItsNameSharesNoWordWithTheRequest()
    {
        // "Add 'buy milk' to Microsoft To Do" never says create or task, yet it asks to create a task.
        Assert.Equal(["create_task"], Chosen(AddMilk, MicrosoftToDo, 5, ToDoTools()));
    }

    [Fact]
    public void ARequestToReadOrSearchChoosesTheToolsThatReadOrSearch()
    {
        Assert.Equal("list_tasks", Chosen("Show my tasks in Microsoft To Do", MicrosoftToDo, 5, ToDoTools())[0]);
        Assert.Equal("list_tasks", Chosen("Find the tasks about milk in Microsoft To Do", MicrosoftToDo, 5, ToDoTools())[0]);
    }

    [Fact]
    public void WithoutTheRequestAskingForAnActionInThatAppNothingChanges()
    {
        var tools = ToDoTools();

        // A question is not a request to do anything, and a request for another app does not boost this app's tools.
        Assert.Equal(["list_task_lists", "list_tasks", "update_task", "create_task", "complete_task"], Chosen("what tasks does Microsoft To Do keep", MicrosoftToDo, 5, tools));
        Assert.Empty(Chosen("Add 'buy milk' to Todoist", MicrosoftToDo, 5, tools));
    }

    [Fact]
    public void ARequestToDeleteNeverMakesADeletingToolMoreLikelyToBeOffered()
    {
        var tools = new[] { Tool("create_task", "Adds a task to a list."), Tool("remove_task", "Removes a task from a list."), Tool("delete_task", "Deletes a task.") };

        // The words decide, as before: no boost is given to the tool that deletes.
        var chosen = Chosen("Delete the task about milk in Microsoft To Do", MicrosoftToDo, 5, tools);

        Assert.Equal(["delete_task", "create_task", "remove_task"], chosen);
    }

    [Fact]
    public void TheMostThatIsAskedForIsNeverExceededWhateverNumberOfToolsDoTheThing()
    {
        var tools = Enumerable.Range(0, 12).Select(number => Tool("add_task_" + number, "Adds a task of kind " + number + ".")).ToArray();

        Assert.Equal(3, Chosen(AddMilk, MicrosoftToDo, 3, tools).Count);
        Assert.Equal(5, Chosen(AddMilk, MicrosoftToDo, 5, tools).Count);
    }

    // A to-do app with thirty tools, the one that is wanted far down the list, and an app that has nothing to do with the request.
    private static StubMcpClient ThirtyTools()
    {
        var client = new StubMcpClient();
        for (var number = 0; number < 29; number++)
        {
            client.Tools.Add(Sample.Tool("get_report_" + number, "Reads the report number " + number + "."));
        }

        client.Tools.Add(Sample.Tool("create_task", "Adds a task to a list.", """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}"""));
        return client;
    }

    [Fact]
    public async Task OnlyTheFewToolsThatFitTheRequestAreLoadedNotThoseOfEveryInstalledApp()
    {
        var notes = Sample.Remote("notes", "Jotter");
        await using var apps = new ConnectedAppsFixture(
            [MicrosoftToDo, notes],
            clients: integration => integration.Id == "mstodo" ? ThirtyTools() : ConnectedAppsFixture.Todoist());

        var context = ConnectedAppsFixture.Context(AddMilk);
        var offered = await apps.OfferedAsync(context);

        var mcp = offered.Where(tool => ConnectedAppTools.IsConnectedAppTool(tool.Name)).Select(tool => tool.Name).ToList();
        Assert.Equal(["mcp_mstodo_create_task"], mcp);
        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.DoesNotContain(mcp, name => name.StartsWith("mcp_notes", StringComparison.Ordinal));

        // The model can call what it was given, and nothing else of the thirty.
        var result = await apps.CallAsync(context, "mcp_mstodo_create_task", """{"title":"buy milk"}""");
        Assert.True(result.Status == ToolResultStatus.Succeeded, result.OutputJson);
    }

    [Theory]
    [InlineData("Add milk to MS To Do")]
    [InlineData("add 'call mom' to microsoft to-do")]
    [InlineData("Put milk on my Microsoft Todo")]
    public async Task AnAppIsTheOneARequestIsAboutHoweverItWritesItsNameAndItsToolIsOffered(string request)
    {
        // No word of "Microsoft To Do" is in the first request, yet the resolver reads it as that app, so its tool is loaded too.
        var app = Sample.Remote("microsofttodo", "Microsoft To Do");
        await using var apps = new ConnectedAppsFixture([app], clients: _ => ThirtyTools());

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context(request));

        Assert.Equal(["mcp_microsofttodo_create_task"], offered.Where(tool => ConnectedAppTools.IsConnectedAppTool(tool.Name)).Select(tool => tool.Name));
    }

    [Fact]
    public async Task ADeleteRequestNeverMakesAnAppMoreLikelyToBeLoadedBecauseOfWhatItAsks()
    {
        var app = Sample.Remote("microsofttodo", "Microsoft To Do");
        await using var apps = new ConnectedAppsFixture([app], clients: _ => ThirtyTools());

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("Delete the milk task from MS To Do"));

        Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task ARequestWithNothingToDoWithAnInstalledAppConnectsToNone()
    {
        await using var apps = new ConnectedAppsFixture([MicrosoftToDo], clients: _ => ThirtyTools());

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("What is the capital of France?"));

        Assert.DoesNotContain(offered, tool => ConnectedAppTools.IsConnectedAppTool(tool.Name));
        Assert.Equal(0, apps.Clients.CreateCalls);
    }
}
