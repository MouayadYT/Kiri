using System.Text.Json;
using Assistant.Core.Calendar;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Tools;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Calendar;

/// <summary>
/// The two calendar tools (PROJECT_SPEC §4.8, step 111): <c>get_calendar_events</c>, the events between a start and an end, and <c>search_calendar_events</c>, the events with some words
/// in them, optionally between a start and an end. They only read (<see cref="RiskLevel.ReadOnly"/>) and need the Calendar permission, which today cannot be allowed (no real calendar
/// can be read), so in the app they never run. They are a capability and not a calendar: what they read is the <see cref="ICalendarProvider"/> the app has, so a calendar service is added or
/// replaced without the tools or the model changing. They are offered only while a provider is there and the request seems to be about a calendar (<see cref="CalendarRequests"/>: a small
/// model's window is not spent on tools it does not need), and not for a request that a connected app's own tools serve: a calendar service that has an MCP integration is read through the
/// generic tool system, which comes first, and the two are never offered side by side.
/// </summary>
/// <param name="provider">The calendar to read; without one the tool is never offered.</param>
/// <param name="clock">What says now, and the user's time zone, for the dates the model is told and the ones it writes.</param>
/// <param name="connectedApps">Where the tools of connected apps come from, to see whether one of them already reads a calendar for the request.</param>
/// <param name="permissions">Says whether the Calendar permission allows a read; the tool checks it itself (step 119), as the executor that runs it does. Without it, only the executor does.</param>
public abstract class CalendarEventTool(ICalendarProvider? provider, TimeProvider clock, IDynamicToolSource? connectedApps, IPermissionPolicy? permissions = null) : ITool
{
    private static readonly IntegrationCapability ReadsEvents = new(CapabilityAction.Read, "event");

    /// <summary>The ISO 8601 formats the model is told.</summary>
    protected const string DateHelp = "an ISO 8601 date such as 2026-10-03, or date and time such as 2026-10-03T09:00, in the user's time zone";

    /// <inheritdoc/>
    public abstract ToolDefinition Definition { get; }

    /// <summary>Whether the model must give both a start and an end.</summary>
    protected abstract bool BothDatesRequired { get; }

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return provider is not null && CalendarRequests.IsAbout(context.Request) && !ServedByAConnectedApp(context);
    }

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => IsOffered(context);

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        // The model may call a registered tool that was not offered, so there may be no calendar to read.
        if (provider is null)
        {
            return Failed(call, "No calendar is connected, so there is nothing to read. Tell the user.");
        }

        // The calendar is read only while the Calendar permission allows it (or the user has just said yes to this use, when it is set to ask): checked here, in the tool.
        if (permissions is not null && await permissions.CheckAsync(PermissionCapability.Calendar, cancellationToken).ConfigureAwait(false) is { IsAllowed: false } decision)
        {
            return Failed(call, PermissionTexts.ForModel(decision));
        }

        var zone = clock.LocalTimeZone;
        if (!CalendarDates.TryResolve(Text(arguments, "start"), Text(arguments, "end"), clock.GetUtcNow(), zone, BothDatesRequired, out var window, out var problem))
        {
            return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, problem!, ToolUsage.Describe(Definition));
        }

        var most = ReadMost(arguments);
        CalendarEventList list;
        try
        {
            list = await ReadAsync(provider, arguments, window.Start, window.End, most, cancellationToken).ConfigureAwait(false) ?? CalendarEventList.Empty;
        }
        catch (CalendarProviderException failure)
        {
            return Failed(call, failure.Failure == CalendarFailure.SignInNeeded
                ? "The calendar needs the user to sign in, and the Assistant cannot sign in to a calendar yet. Tell the user."
                : "The calendar could not be read right now. Tell the user, or try again.");
        }
        catch (EmptySearchException)
        {
            return ToolErrors.Result(
                call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, "There are no words to search for. Give the words the event has in it.", ToolUsage.Describe(Definition));
        }

        return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, CalendarToolResults.Events(provider.Name, provider.IsSample, window.Start, window.End, zone, list));
    }

    /// <summary>Asks <paramref name="source"/> for the events the call is for.</summary>
    /// <exception cref="EmptySearchException">The call is a search with no words to search for.</exception>
    protected abstract Task<CalendarEventList?> ReadAsync(
        ICalendarProvider source, JsonElement arguments, DateTimeOffset start, DateTimeOffset end, int most, CancellationToken cancellationToken);

    /// <summary>The text of the argument <paramref name="name"/>, or <see langword="null"/> when it was not given.</summary>
    protected static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // How many events to give back: what the model asked for, within the most a call gives.
    private static int ReadMost(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("max_results", out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, 1, CalendarToolResults.MaxEvents)
            : CalendarToolResults.DefaultEvents;

    // A connected app that reads events for this request is the way to read the calendar: the generic tool system comes first, and the built-in tools stand aside.
    private bool ServedByAConnectedApp(ToolContext context) =>
        connectedApps?.Offered(context).Any(tool => CapabilityMatcher.Match(
            ReadsEvents, [new ToolFacts(tool.Definition.Name, null, tool.Definition.Description)]).Count > 0) == true;

    private static ToolResult Failed(ToolCall call, string message) => ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);

    /// <summary>A search was asked for with nothing to search for.</summary>
    protected sealed class EmptySearchException : Exception;
}

/// <summary><c>get_calendar_events</c>: the events between a start and an end.</summary>
public sealed class GetCalendarEventsTool(ICalendarProvider? provider, TimeProvider clock, IDynamicToolSource? connectedApps = null, IPermissionPolicy? permissions = null)
    : CalendarEventTool(provider, clock, connectedApps, permissions)
{
    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        CalendarToolResults.GetEvents,
        "Read the events in the user's calendar between a start and an end. Use it when the user asks what is on their calendar, what they have on a day or in a week, " +
        "or when something is. Both dates are ISO 8601; the end is not included, so for the whole of one day give that day as the start and the next day as the end. " +
        "It returns the events soonest first, with their start, end, place and notes. Never make up an event: what it returns is the calendar.",
        [
            new ToolParameter("start", ToolParameterType.String, "The start of the time to look at: " + DateHelp + ".", MaxLength: 40),
            new ToolParameter("end", ToolParameterType.String, "The end of the time to look at, not included: " + DateHelp + ". At most a year after the start.", MaxLength: 40),
            new ToolParameter(
                "max_results", ToolParameterType.Integer, "The most events to give back, from 1 to " + CalendarToolResults.MaxEvents + ". Leave it out for the usual number.",
                Required: false, Minimum: 1, Maximum: CalendarToolResults.MaxEvents),
        ],
        RiskLevel.ReadOnly,
        PermissionCapability.Calendar,
        TimeSpan.FromSeconds(30));

    /// <inheritdoc/>
    protected override bool BothDatesRequired => true;

    /// <inheritdoc/>
    protected override async Task<CalendarEventList?> ReadAsync(
        ICalendarProvider source, JsonElement arguments, DateTimeOffset start, DateTimeOffset end, int most, CancellationToken cancellationToken) =>
        await source.GetEventsAsync(new CalendarEventQuery(start, end, most), cancellationToken).ConfigureAwait(false);
}

/// <summary><c>search_calendar_events</c>: the events with some words in them, optionally between a start and an end.</summary>
public sealed class SearchCalendarEventsTool(ICalendarProvider? provider, TimeProvider clock, IDynamicToolSource? connectedApps = null, IPermissionPolicy? permissions = null)
    : CalendarEventTool(provider, clock, connectedApps, permissions)
{
    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        CalendarToolResults.SearchEvents,
        "Look for events in the user's calendar by what they say: words in the title, the place or the notes. Use it when the user asks when something is, or whether they " +
        "have a meeting, an appointment or a birthday about something. Every word must be in the event. It looks from the start to the end when they are given (ISO 8601, the end not " +
        "included) and otherwise from today for a year ahead. It returns the events soonest first. Never make up an event: what it returns is the calendar.",
        [
            new ToolParameter("query", ToolParameterType.String, "The words to look for, for example dentist or lunch with Sam.", MaxLength: 200),
            new ToolParameter("start", ToolParameterType.String, "Only events from this time on: " + DateHelp + ". Leave it out to start today.", Required: false, MaxLength: 40),
            new ToolParameter("end", ToolParameterType.String, "Only events before this time: " + DateHelp + ". Leave it out for a year after the start.", Required: false, MaxLength: 40),
            new ToolParameter(
                "max_results", ToolParameterType.Integer, "The most events to give back, from 1 to " + CalendarToolResults.MaxEvents + ". Leave it out for the usual number.",
                Required: false, Minimum: 1, Maximum: CalendarToolResults.MaxEvents),
        ],
        RiskLevel.ReadOnly,
        PermissionCapability.Calendar,
        TimeSpan.FromSeconds(30));

    /// <inheritdoc/>
    protected override bool BothDatesRequired => false;

    /// <inheritdoc/>
    protected override async Task<CalendarEventList?> ReadAsync(
        ICalendarProvider source, JsonElement arguments, DateTimeOffset start, DateTimeOffset end, int most, CancellationToken cancellationToken)
    {
        var words = Text(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(words) || !words.Any(char.IsLetterOrDigit))
        {
            throw new EmptySearchException();
        }

        return await source.SearchEventsAsync(new CalendarSearchQuery(words, start, end, most), cancellationToken).ConfigureAwait(false);
    }
}
