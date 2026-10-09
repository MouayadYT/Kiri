using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Calendar;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Permissions;
using Assistant.Core.Storage;
using Assistant.Tools.Calendar;
using Assistant.UI.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The calendar tools as the app puts the pieces together (PROJECT_SPEC §4.8, step 111): they are registered, but the app has no calendar, so they are never offered and nothing is read; a provider
/// registered in the container is what they read (and another one replaces it), the Calendar permission still decides whether they may run, and with it allowed the whole agent loop works against the
/// made-up calendar: the model is told today's date, asks for tomorrow with structured dates, and answers from what the calendar returned.
/// </summary>
public sealed class CalendarWiringTests : IDisposable
{
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");

    // Friday 2 October 2026, 14:05, two hours ahead of UTC.
    private static readonly DateTimeOffset Today = new(2026, 10, 2, 14, 5, 0, TimeSpan.FromHours(2));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-calendar-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
    }

    private sealed class FixedClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class AllowingPolicy : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(capability, PermissionDecisionReason.Granted));
    }

    // Reads the date it was told, asks the calendar for tomorrow, and says what the calendar answered.
    private sealed class CalendarModel : IModelService
    {
        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", 8192) { SupportsToolCalling = true });

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            if (request.Messages[^1].Role == MessageRole.Tool)
            {
                using var output = JsonDocument.Parse(request.Messages[^1].Text);
                var titles = output.RootElement.TryGetProperty("events", out var events)
                    ? string.Join("; ", events.EnumerateArray().Select(item => item.GetProperty("title").GetString()))
                    : output.RootElement.GetProperty("error").GetString();
                yield return AssistantResponseChunk.ForTextDelta("Tomorrow: " + titles);
            }
            else if (request.Tools.Any(tool => tool.Name == "get_calendar_events"))
            {
                // "Tomorrow" is worked out from the date the instructions give.
                Assert.Contains("Today is Friday 2026-10-02", request.Instructions, StringComparison.Ordinal);
                yield return AssistantResponseChunk.ForToolCall(new ToolCall("call-1", "get_calendar_events", """{"start":"2026-10-03","end":"2026-10-04"}"""));
            }
            else
            {
                yield return AssistantResponseChunk.ForTextDelta("I cannot see a calendar.");
            }
        }
    }

    private IHost Host(IModelService model, Action<IServiceCollection>? more = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(_root));
        builder.Services.AddSingleton(model);
        builder.Services.AddSingleton<TimeProvider>(new FixedClock(Today, Plus2));
        more?.Invoke(builder.Services);
        return builder.Build();
    }

    private static async Task<List<AssistantResponseChunk>> ReadAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    private static readonly string[] CalendarTools = ["get_calendar_events", "search_calendar_events"];

    [Fact]
    public void TheCalendarToolsAreRegisteredButTheAppHasNoCalendarSoTheyAreNeverOffered()
    {
        using var host = Host(new CalendarModel());
        var registry = host.Services.GetRequiredService<IToolRegistry>();

        Assert.All(CalendarTools, name => Assert.NotNull(registry.Find(name)));
        Assert.Null(host.Services.GetService<ICalendarProvider>());
        Assert.DoesNotContain(registry.ToolsFor(new ToolContext(Guid.NewGuid(), "What is on my calendar tomorrow?")), tool => CalendarTools.Contains(tool.Name));

        // A calendar of a connected app can be read (step 119), but only when the user allows it: the permission starts off.
        Assert.Equal(PermissionAvailability.Available, PermissionCatalog.Get(PermissionCapability.Calendar).Availability);
        Assert.False(new Assistant.Core.Settings.PermissionSettings().Calendar);
    }

    [Fact]
    public async Task AProviderInTheContainerMakesTheToolsOfferedButTheCalendarPermissionStillDecidesWhetherTheyRun()
    {
        var provider = new InMemoryCalendarProvider(isSample: false);
        using var host = Host(new CalendarModel(), services => services.AddSingleton<ICalendarProvider>(provider));
        var registry = host.Services.GetRequiredService<IToolRegistry>();
        var context = new ToolContext(Guid.NewGuid(), "What is on my calendar tomorrow?");

        Assert.Equal(CalendarTools, registry.ToolsFor(context).Where(tool => CalendarTools.Contains(tool.Name)).Select(tool => tool.Name));

        var result = await host.Services.GetRequiredService<IToolExecutor>().ExecuteAsync(
            new ToolCall("call-1", "get_calendar_events", """{"start":"2026-10-03","end":"2026-10-04"}"""), context);

        // The real policy: Calendar is off until the user allows it, so nothing was read.
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Calendar is turned off in Settings", result.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, provider.Reads);
    }

    [Fact]
    public async Task WithThePermissionAllowedTheModelIsToldTodayAsksForTomorrowAndAnswersFromTheMadeUpCalendar()
    {
        var clock = new FixedClock(Today, Plus2);
        var model = new CalendarModel();
        using var host = Host(
            model,
            services =>
            {
                services.AddSingleton<ICalendarProvider>(InMemoryCalendarProvider.WithSampleEvents(clock));
                services.AddSingleton<IPermissionPolicy>(new AllowingPolicy());
            });
        var orchestrator = host.Services.GetRequiredService<IAssistantOrchestrator>();
        var session = ConversationSession.Start(clock);

        var chunks = await ReadAsync(orchestrator.AskAsync(session, "What is on my calendar tomorrow?"));

        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[0].Tools, tool => tool.Name == "get_calendar_events");
        Assert.Contains(model.Requests[0].Tools, tool => tool.Name == "search_calendar_events");
        var result = Assert.Single(chunks, chunk => chunk.Type == AssistantResponseChunkType.ToolResult).ToolResult!;
        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.True(json.RootElement.GetProperty("sample").GetBoolean());
        Assert.Equal("2026-10-03T00:00:00+02:00", json.RootElement.GetProperty("from").GetString());
        Assert.Equal(
            "Tomorrow: Anna's birthday (sample); Dentist (sample)",
            string.Concat(chunks.Where(chunk => chunk.Type == AssistantResponseChunkType.TextDelta).Select(chunk => chunk.Text)));

        // A request that is not about the calendar is given neither its tools nor its date.
        var other = await ReadAsync(orchestrator.AskAsync(ConversationSession.Start(clock), "hello"));
        var asked = model.Requests[^1];
        Assert.DoesNotContain(asked.Tools, tool => CalendarTools.Contains(tool.Name));
        Assert.DoesNotContain("Today is", asked.Instructions, StringComparison.Ordinal);
        Assert.Equal("I cannot see a calendar.", Assert.Single(other).Text);
    }

    [Fact]
    public async Task ARealCalendarReplacesTheMadeUpOneByRegisteringAnotherProviderAndTheToolsReadIt()
    {
        var clock = new FixedClock(Today, Plus2);
        var other = new InMemoryCalendarProvider(
            [new CalendarEvent { Title = "Board meeting", Start = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.FromHours(2)), End = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.FromHours(2)) }],
            "Outlook",
            isSample: false);
        using var host = Host(
            new CalendarModel(),
            services =>
            {
                services.AddSingleton<ICalendarProvider>(InMemoryCalendarProvider.WithSampleEvents(clock));
                services.AddSingleton<ICalendarProvider>(other);
                services.AddSingleton<IPermissionPolicy>(new AllowingPolicy());
            });

        var result = await host.Services.GetRequiredService<IToolExecutor>().ExecuteAsync(
            new ToolCall("call-1", "get_calendar_events", """{"start":"2026-10-03","end":"2026-10-04"}"""), new ToolContext(Guid.NewGuid()));

        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.Equal("Outlook", json.RootElement.GetProperty("calendar").GetString());
        Assert.False(json.RootElement.GetProperty("sample").GetBoolean());
        Assert.Equal(["Board meeting"], json.RootElement.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("title").GetString()));
    }
}
