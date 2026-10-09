using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Calendar;

namespace Assistant.Core.Tools;

/// <summary>
/// The names of the calendar tools (PROJECT_SPEC §4.8, step 111) and what they return to the model, as JSON. The events are data from a calendar, written by whoever made them
/// (an invitation can come from anyone), so every text in them is cleaned to one line and cut to a length before the model sees it, and the result says where it is from. A calendar whose
/// events are made up for trying the app says so, so that the model does not pass them off as the user's own.
/// </summary>
public static class CalendarToolResults
{
    /// <summary>The name of the tool that reads the events between two times.</summary>
    public const string GetEvents = "get_calendar_events";

    /// <summary>The name of the tool that looks for events by what they say.</summary>
    public const string SearchEvents = "search_calendar_events";

    /// <summary>The most events one call gives back, whatever the model asks for.</summary>
    public const int MaxEvents = 50;

    /// <summary>The number of events a call gives back when the model does not say.</summary>
    public const int DefaultEvents = 25;

    private const int MaxTitleLength = 120;
    private const int MaxLocationLength = 120;
    private const int MaxNotesLength = 400;
    private const string ExamNote =
        "Events marked exam are the Assistant's own check of an event's title, place and notes: likely is probably an exam, possible may be one. It is a hint, not a fact: use the event's own words.";
    private const string SampleNote = "These events are made up to try the Assistant. They are not the user's own calendar, so say that they are samples.";

    // A title reads as it is written, not with its quotes or plus signs escaped, in what the model sees.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Whether <paramref name="name"/> is the name of one of the calendar tools.</summary>
    public static bool IsCalendarTool(string? name) => name is GetEvents or SearchEvents;

    /// <summary>
    /// Whether <paramref name="tool"/> is a tool of a connected app (its name starts with <see cref="ConnectedAppTools.Prefix"/>) that reads a calendar (step 116): its name or its description
    /// says event, events, calendar or agenda. The built-in calendar tools are known by name (<see cref="IsCalendarTool"/>). It is only a reading of words, to decide whether the model is
    /// given the help with dates that a calendar tool needs; the tool itself is whatever the app made it.
    /// </summary>
    /// <param name="tool">The tool as it is offered.</param>
    public static bool IsConnectedCalendarReader(Assistant.Core.Domain.ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!ConnectedAppTools.IsConnectedAppTool(tool.Name))
        {
            return false;
        }

        var word = new StringBuilder();
        foreach (var character in tool.Name + " " + tool.Description + " ")
        {
            if (char.IsLetter(character))
            {
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (word.Length > 0 && word.ToString() is "event" or "events" or "calendar" or "calendars" or "agenda")
            {
                return true;
            }

            word.Clear();
        }

        return false;
    }

    /// <summary>The JSON of the events a calendar gave for the time from <paramref name="start"/> to <paramref name="end"/>.</summary>
    /// <param name="calendar">The calendar's name, as the user knows it.</param>
    /// <param name="isSample">Whether the events are made up.</param>
    /// <param name="start">The beginning of the time asked about.</param>
    /// <param name="end">The end of it, not included.</param>
    /// <param name="zone">The user's time zone, which the times are told in.</param>
    /// <param name="list">What the calendar gave.</param>
    public static string Events(string calendar, bool isSample, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone, CalendarEventList list)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(list);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            var events = list.Events.Take(MaxEvents).ToList();
            writer.WriteStartObject();
            writer.WriteString("source", "calendar");
            writer.WriteString("calendar", Line(calendar, MaxTitleLength));
            writer.WriteBoolean("sample", isSample);
            writer.WriteString("from", CalendarDates.Format(start, zone));
            writer.WriteString("to", CalendarDates.Format(end, zone));
            writer.WriteNumber("count", events.Count);
            writer.WriteBoolean("complete", list.IsComplete && list.Events.Count <= MaxEvents);
            writer.WriteStartArray("events");
            foreach (var item in events)
            {
                writer.WriteStartObject();
                writer.WriteString("title", Line(item.Title, MaxTitleLength));
                if (item.IsAllDay)
                {
                    writer.WriteBoolean("all_day", true);

                    // The last day is the day before the end, which is not included.
                    writer.WriteString("start", CalendarDates.FormatDate(item.Start, zone));
                    writer.WriteString("end", CalendarDates.FormatDate(item.End > item.Start ? item.End.AddDays(-1) : item.End, zone));
                }
                else
                {
                    writer.WriteString("start", CalendarDates.Format(item.Start, zone));
                    writer.WriteString("end", CalendarDates.Format(item.End, zone));
                }

                if (Line(item.Location, MaxLocationLength) is { Length: > 0 } location)
                {
                    writer.WriteString("location", location);
                }

                if (Line(item.Notes, MaxNotesLength) is { Length: > 0 } notes)
                {
                    writer.WriteString("notes", notes);
                }

                if (Line(item.CalendarName, MaxTitleLength) is { Length: > 0 } name && !string.Equals(name, calendar, StringComparison.Ordinal))
                {
                    writer.WriteString("calendar", name);
                }

                // The Assistant's own check of the event's words (step 116): a hint for the model, from fixed rules, never a fact about the event.
                if (ExamDetector.Assess(item) is { IsCandidate: true } exam)
                {
                    writer.WriteString("exam", exam.Likelihood == ExamLikelihood.Likely ? "likely" : "possible");
                    writer.WriteString("exam_reason", exam.Reason);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            // What the model must not miss: that the events are made up, that there are none, or that these are only the first.
            var said = new List<string>();
            if (isSample)
            {
                said.Add(SampleNote);
            }

            if (events.Count == 0)
            {
                said.Add("There are no events in that time.");
            }
            else if (!list.IsComplete || list.Events.Count > MaxEvents)
            {
                said.Add("There are more events than these, which are only the first. Ask for a shorter time to see the rest.");
            }

            if (events.Any(item => ExamDetector.Assess(item).IsCandidate))
            {
                said.Add(ExamNote);
            }

            if (said.Count > 0)
            {
                writer.WriteString("note", string.Join(' ', said));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>One line of text from a calendar: control characters and angle brackets taken out, white space made single, cut to <paramref name="maxLength"/>.</summary>
    internal static string Line(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var lastWasSpace = true;
        foreach (var character in text)
        {
            var spaceLike = char.IsWhiteSpace(character) || char.IsControl(character) || character is '<' or '>';
            if (spaceLike)
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
            if (builder.Length > maxLength)
            {
                break;
            }
        }

        var line = builder.ToString().TrimEnd();
        return line.Length > maxLength ? line[..(maxLength - 1)].TrimEnd() + "…" : line;
    }
}
