using Assistant.Tools.Integrations;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// Which connected apps and tools a request that names no app is about (PROJECT_SPEC §4.8, step 116): "check my calendar" is about whichever installed app has a tool that reads events, however
/// it is called, and the tool that reads events comes first.
/// </summary>
public sealed class CapabilitySelectionTests
{
    private static InstalledIntegration Gcal() =>
        Sample.Remote("gcal", "Gcal") with
        {
            Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["create_event", "list_events", "search_events"], RefreshedAt = WorkflowFixture.Start },
            Permissions = new IntegrationPermissions { ReadOnlyTools = ["list_events", "search_events"] },
        };

    private static InstalledIntegration Notes() =>
        Sample.Remote("notes", "Notes") with { Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["list_notes", "add_note"], RefreshedAt = WorkflowFixture.Start } };

    private static StubMcpClient CalendarClient()
    {
        var client = new StubMcpClient();
        client.Tools.Add(Sample.Tool("create_event", "Creates an event.", """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}"""));
        client.Tools.Add(Sample.Tool("search_events", "Looks for events.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", readOnly: true));
        client.Tools.Add(Sample.Tool("list_events", "Lists the events.", """{"type":"object","properties":{"start":{"type":"string"},"end":{"type":"string"}}}""", readOnly: true));
        return client;
    }

    private static StubMcpClient NotesClient()
    {
        var client = new StubMcpClient();
        client.Tools.Add(Sample.Tool("list_notes", "Lists the notes."));
        client.Tools.Add(Sample.Tool("add_note", "Adds a note."));
        return client;
    }

    private static ConnectedAppsFixture Apps() =>
        new([Gcal(), Notes()], clients: integration => integration.Id == "gcal" ? CalendarClient() : NotesClient());

    [Fact]
    public async Task AnAppWithAToolThatReadsEventsIsTheOneACalendarRequestIsAboutWhateverItIsCalled()
    {
        await using var apps = Apps();

        var offered = (await apps.OfferedAsync(ConnectedAppsFixture.Context("Check my calendar for exams in the next two weeks"))).Select(tool => tool.Name).ToList();

        Assert.Contains("mcp_gcal_list_events", offered);
        Assert.DoesNotContain(offered, name => name.StartsWith("mcp_notes_", StringComparison.Ordinal));
        Assert.Equal(1, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task OnlyTheToolsThatReadEventsAreOfferedForARequestToReadThem()
    {
        await using var apps = Apps();

        var offered = (await apps.OfferedAsync(ConnectedAppsFixture.Context("What's on my calendar tomorrow?"))).Select(tool => tool.Name).ToList();

        var connected = offered.Where(name => name.StartsWith("mcp_", StringComparison.Ordinal)).ToList();
        Assert.Equal(["mcp_gcal_list_events", "mcp_gcal_search_events"], connected.Order());
    }

    [Fact]
    public async Task ARequestThatIsNotAboutACalendarLoadsNeitherApp()
    {
        await using var apps = Apps();

        var offered = await apps.OfferedAsync(ConnectedAppsFixture.Context("What is 12 times 3?"));

        Assert.DoesNotContain(offered, tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnAppIsNotLoadedForAnotherKindOfThingJustBecauseItHasTools()
    {
        await using var apps = Apps();

        var offered = (await apps.OfferedAsync(ConnectedAppsFixture.Context("Show my notes"))).Select(tool => tool.Name).ToList();

        Assert.Contains("mcp_notes_list_notes", offered);
        Assert.DoesNotContain(offered, name => name.StartsWith("mcp_gcal_", StringComparison.Ordinal));
    }
}
