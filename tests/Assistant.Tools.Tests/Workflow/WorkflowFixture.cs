using System.Text.Json;
using Assistant.Core.Agent;
using Assistant.Core.Budgeting;
using Assistant.Core.Calendar;
using Assistant.Core.Confirmation;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.Orchestration;
using Assistant.Core.People;
using Assistant.Tools.Calendar;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Messaging;
using Assistant.Tools.Messaging.ConnectedApps;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// What the calendar-to-brother workflow is run on (PROJECT_SPEC §4.8, step 116): the real orchestrator, agent loop, tool registry, executor, MCP connection manager and tool source,
/// the real calendar, messaging and person tools and the messaging provider over connected apps, with stub servers in place of the apps, a scripted model and a confirmation the
/// test answers.
/// </summary>
internal sealed class WorkflowFixture : IAsyncDisposable
{
    public const string CanonicalRequest = "Check my calendar for exams in the next two weeks and message my brother to remind him.";

    public const string Reminder = "Reminder: Physics final exam on 2026-10-07 at 09:00 and Calculus midterm on 2026-10-11 at 13:00.";

    public static readonly DateTimeOffset Start = AgentLoopConnectedAppsTests.Start;

    public WorkflowFixture(
        bool calendarApp = true,
        bool messagesApp = true,
        bool approve = true,
        ConfirmationDecision? decision = null,
        IEnumerable<Person>? people = null,
        ICalendarProvider? calendarProvider = null,
        IMessagingProvider? messagingProvider = null,
        bool messagingAllowed = true,
        string? eventsJson = null,
        string? chatsJson = null,
        bool messagesIsSample = true,
        IMcpClientFactory? realClients = null,
        IReadOnlyList<InstalledIntegration>? installed = null)
    {
        Clock = new AgentLoopConnectedAppsTests.Clock(Start);
        Calendar = CalendarClient(eventsJson ?? EventsJson);
        Messages = MessagesClient(chatsJson ?? ChatsJson);
        var integrations = new List<InstalledIntegration>();
        if (calendarApp)
        {
            integrations.Add(CalendarIntegration());
        }

        if (messagesApp)
        {
            integrations.Add(MessagesIntegration(messagesIsSample));
        }

        Store = new MemoryIntegrationStore([.. installed ?? integrations]);
        Integrations = new InstalledIntegrationRegistry(Store, NullLogger<InstalledIntegrationRegistry>.Instance);
        Clients = realClients ?? new StubClientFactory(integration => integration.Id == "samplecalendar" ? Calendar : Messages);
        Options = new McpLoadingOptions();
        Permissions = new FakePermissions(true);
        var messagingPermissions = new FakePermissions(messagingAllowed);
        Manager = new McpConnectionManager(
            Integrations, Clients, TestSettings.LocalOnly(false), Clock, Options, NullLogger<McpConnectionManager>.Instance, cache: null);
        Provider = new McpMessagingProvider(Integrations, Manager, messagingPermissions, NullLogger<McpMessagingProvider>.Instance);
        Source = new McpToolSource(Integrations, Manager, LexicalMcpToolSelector.Instance, Options, Clock, NullLogger<McpToolSource>.Instance, [Provider]);

        PersonStore = new InMemoryPersonStore(Clock);
        foreach (var person in people ?? [Brother("Omar")])
        {
            PersonStore.SaveAsync(person).GetAwaiter().GetResult();
        }

        var resolver = new PersonResolver(PersonStore);
        IMessagingProvider? messaging = messagingProvider ?? Provider;
        ITool[] tools =
        [
            new GetCalendarEventsTool(calendarProvider, Clock, Source),
            new SearchCalendarEventsTool(calendarProvider, Clock, Source),
            new DraftMessageTool(messaging, resolver, messagingPermissions),
            new SendMessageTool(messaging, resolver, messagingPermissions),
        ];
        Confirmation = new FakeConfirmation(approve) { Decision = decision };
        Tools = new ToolRegistry(tools, Source);
        Executor = new ToolExecutor(tools, Confirmation, Permissions, Source);
        Traces = new AgentTraceStore();
    }

    public AgentLoopConnectedAppsTests.Clock Clock { get; }

    public StubMcpClient Calendar { get; }

    public StubMcpClient Messages { get; }

    public MemoryIntegrationStore Store { get; }

    public InstalledIntegrationRegistry Integrations { get; }

    public IMcpClientFactory Clients { get; }

    public McpLoadingOptions Options { get; }

    public IPermissionPolicy Permissions { get; }

    public McpConnectionManager Manager { get; }

    public McpMessagingProvider Provider { get; }

    public McpToolSource Source { get; }

    public InMemoryPersonStore PersonStore { get; }

    public FakeConfirmation Confirmation { get; }

    public ToolRegistry Tools { get; }

    public ToolExecutor Executor { get; }

    public AgentTraceStore Traces { get; }

    /// <summary>The events of a sample calendar, as a calendar app's server answers: two exams, a study session for a third, and three that are not.</summary>
    public const string EventsJson =
        """
        {"calendar":"Sample Calendar","sample":true,"events":[
          {"title":"Dentist appointment (sample)","start":"2026-10-03T09:30:00","end":"2026-10-03T10:30:00","location":"Smile Clinic"},
          {"title":"Physics final exam (sample)","start":"2026-10-07T09:00:00","end":"2026-10-07T10:00:00","location":"Hall B"},
          {"title":"Study session for the chemistry midterm (sample)","start":"2026-10-08T15:00:00","end":"2026-10-08T16:00:00"},
          {"title":"Calculus midterm (sample)","start":"2026-10-11T13:00:00","end":"2026-10-11T14:00:00","location":"Room 204"},
          {"title":"Dinner with Anna (sample)","start":"2026-10-13T18:30:00","end":"2026-10-13T19:30:00"}]}
        """;

    /// <summary>A calendar with no exam in it.</summary>
    public const string NoExamsJson =
        """{"calendar":"Sample Calendar","events":[{"title":"Dentist appointment","start":"2026-10-03T09:30:00"},{"title":"Dinner with Anna","start":"2026-10-13T18:30:00"}]}""";

    /// <summary>The chats of a messaging app: Omar's own, and a group that Omar is in.</summary>
    public const string ChatsJson =
        """
        {"chats":[
          {"id":"chat-omar","title":"Omar","network":"Messages","type":"single","participants":[{"name":"Omar","phone":"+1 555 0100"}]},
          {"id":"chat-family","title":"Family (group)","network":"Messages","type":"group","participants":[{"phone":"+1 555 0100"},{"phone":"+1 555 0123"},{"phone":"+1 555 0177"}]}]}
        """;

    public static Person Brother(string name, string phone = "+1 555 0100", string service = "Messages") =>
        Person.Create(name, Start) with { Relationships = ["Brother"], Identifiers = [new PersonIdentifier(PersonIdentifierKind.Phone, phone, service)] };

    public static InstalledIntegration CalendarIntegration() =>
        Sample.Remote("samplecalendar", "Sample Calendar") with
        {
            IsSample = true,
            Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["list_events", "search_events"], RefreshedAt = Start },
            Health = new IntegrationHealth { Status = IntegrationHealthStatus.Healthy },
            Permissions = new IntegrationPermissions { ReadOnlyTools = ["list_events", "search_events"] },
        };

    public static InstalledIntegration MessagesIntegration(bool isSample = true) =>
        Sample.Remote("samplemessages", "Sample Messages") with
        {
            IsSample = isSample,
            Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["search_chats", "send_message"], RefreshedAt = Start },
            Health = new IntegrationHealth { Status = IntegrationHealthStatus.Healthy },
            Permissions = new IntegrationPermissions { ReadOnlyTools = ["search_chats"] },
        };

    public static StubMcpClient CalendarClient(string json)
    {
        var client = new StubMcpClient();
        client.Tools.Add(Sample.Tool(
            "list_events", "Lists the events in the sample calendar between a start and an end.",
            """{"type":"object","properties":{"start":{"type":"string","description":"An ISO 8601 date."},"end":{"type":"string","description":"An ISO 8601 date, not included."}},"required":["start","end"]}""",
            readOnly: true));
        client.Tools.Add(Sample.Tool(
            "search_events", "Looks for events in the sample calendar by words.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Words."}},"required":["query"]}""", readOnly: true));
        client.OnCall = (_, _, _) => Task.FromResult(Sample.Text(json));
        return client;
    }

    public static StubMcpClient MessagesClient(string chatsJson)
    {
        var client = new StubMcpClient();
        client.Tools.Add(Sample.Tool(
            "search_chats", "Looks for chats by a name, a number or a username.",
            """{"type":"object","properties":{"query":{"type":"string","description":"A name, a number or a username."}},"required":["query"]}""", readOnly: true));
        client.Tools.Add(Sample.Tool(
            "send_message", "Sends a text to a chat.",
            """{"type":"object","properties":{"chat_id":{"type":"string","description":"The chat."},"text":{"type":"string","description":"The text."}},"required":["chat_id","text"]}"""));
        client.OnCall = (tool, _, _) => Task.FromResult(Sample.Text(tool.Name == "search_chats" ? chatsJson : """{"status":"sent"}"""));
        return client;
    }

    public AssistantOrchestrator Orchestrator(AgentLoopConnectedAppsTests.ScriptedModel model, AgentLimits? limits = null) =>
        new(
            model, new FixedSettings(), new PromptBuilder(new ContextService(new ContextBudgeter(new HeuristicTokenEstimator())), Clock),
            new AgentLoopConnectedAppsTests.NoImages(), Clock, NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: Tools, toolExecutor: Executor, traceSink: Traces, agentLimits: limits);

    public async Task<(List<AssistantResponseChunk> Chunks, ConversationSession Session)> RunAsync(
        AgentLoopConnectedAppsTests.ScriptedModel model, string? request = null, AgentLimits? limits = null)
    {
        var session = ConversationSession.Start(Clock);
        var chunks = new List<AssistantResponseChunk>();
        await foreach (var chunk in Orchestrator(model, limits).AskAsync(session, request ?? CanonicalRequest))
        {
            chunks.Add(chunk);
        }

        return (chunks, session);
    }

    /// <summary>The scripted model does the whole job in the order it is told: read the calendar, draft the message, send it, say what happened.</summary>
    public static AgentLoopConnectedAppsTests.ScriptedModel WholeJob(string text = Reminder, string finalWords = "I found two exams and sent the reminder to Omar.") =>
        new(
            [AgentLoopConnectedAppsTests.Call("mcp_samplecalendar_list_events", """{"start":"2026-10-02","end":"2026-10-16"}""", "c1")],
            [AgentLoopConnectedAppsTests.Call("draft_message", Json(new { recipient = "my brother", text }), "c2")],
            [AgentLoopConnectedAppsTests.Call("send_message", Json(new { recipient = "my brother", text }), "c3")],
            [AgentLoopConnectedAppsTests.Words(finalWords)]);

    public static string Json(object value) => JsonSerializer.Serialize(value);

    public static List<ToolResult> ResultsOf(IEnumerable<AssistantResponseChunk> chunks) =>
        [.. chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.ToolResult).Select(chunk => chunk.ToolResult!)];

    public ValueTask DisposeAsync() => Manager.DisposeAsync();
}
