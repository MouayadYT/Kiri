using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Calendar;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Calendar;

/// <summary>
/// The Assistant's own check of which events a connected calendar app returned look like exams (PROJECT_SPEC §4.8, step 116). A calendar service's answer is whatever its server makes it,
/// so this reads the shapes that calendars commonly use (JSON, as text or as structured content, with the events in an array: each an object with a title under <c>summary</c>,
/// <c>title</c>, <c>name</c> or <c>subject</c> and a start) and, for an app that answers in plain lines, each line as an event; and it runs <see cref="ExamDetector"/> on each event's title,
/// place and notes. The result is put beside what the app returned as <c>exam_check</c>, so that a small model that would have to judge every event itself is given the Assistant's own
/// reading, clearly marked as such. It is a hint and not a fact about the events, and it decides nothing: what is then done with the events is shown to the user first. What is not
/// understood adds nothing, and what the app said is never changed or left out.
/// </summary>
internal static class ExamHints
{
    private const int MaxEventsChecked = 200;
    private const int MaxListed = 10;
    private const int MaxDepth = 5;

    private static readonly string[] TitleKeys = ["summary", "title", "name", "subject", "event", "event_name", "eventname"];
    private static readonly string[] StartKeys = ["start", "starttime", "start_time", "startdate", "start_date", "begin", "date", "when", "from", "dtstart", "datetime"];
    private static readonly string[] PlaceKeys = ["location", "where", "place", "room", "venue"];
    private static readonly string[] NoteKeys = ["description", "notes", "note", "body", "details"];

    /// <summary>The <c>exam_check</c> for what the app returned, or <see langword="null"/> when no event could be read from it.</summary>
    public static JsonObject? Check(McpToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsError)
        {
            return null;
        }

        var events = new List<EventText>();
        if (result.StructuredContent is { } structured)
        {
            Collect(structured, events, 0);
        }

        foreach (var block in result.Content)
        {
            if (events.Count >= MaxEventsChecked || block.Kind is not (McpContentKind.Text or McpContentKind.Resource) || string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            if (LooksJson(block.Text))
            {
                try
                {
                    using var document = JsonDocument.Parse(block.Text);
                    Collect(document.RootElement, events, 0);
                }
                catch (JsonException)
                {
                    // Text that is not JSON after all is read as lines below.
                    AddLines(block.Text, events);
                }
            }
            else
            {
                AddLines(block.Text, events);
            }
        }

        if (events.Count == 0)
        {
            return null;
        }

        var likely = new JsonArray();
        var possible = new JsonArray();
        foreach (var item in events.Take(MaxEventsChecked))
        {
            var assessment = ExamDetector.Assess(item.Title, item.Place, item.Notes);
            var target = assessment.Likelihood switch
            {
                ExamLikelihood.Likely => likely,
                ExamLikelihood.Possible => possible,
                _ => null,
            };
            if (target is not null && target.Count < MaxListed)
            {
                target.Add(new JsonObject { ["event"] = item.Display, ["why"] = assessment.Reason });
            }
        }

        return new JsonObject
        {
            ["by"] = "the Assistant's own check of each event's title, place and notes: a hint, not a fact",
            ["events_checked"] = Math.Min(events.Count, MaxEventsChecked),
            ["likely"] = likely,
            ["possible"] = possible,
        };
    }

    private static bool LooksJson(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 1 && trimmed[0] is '{' or '[';
    }

    // Plain lines: each non-empty line is an event, as its own words say.
    private static void AddLines(string text, List<EventText> events)
    {
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = Clean(raw, 300);
            if (line.Length >= 3 && events.Count < MaxEventsChecked)
            {
                events.Add(new EventText(line, null, null, Clean(line, 160)));
            }
        }
    }

    private static void Collect(JsonElement element, List<EventText> events, int depth)
    {
        if (depth > MaxDepth || events.Count >= MaxEventsChecked)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, events, depth + 1);
                }

                break;

            case JsonValueKind.Object:
                if (EventOf(element) is { } found)
                {
                    events.Add(found);
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, events, depth + 1);
                }

                break;
        }
    }

    // An object with a title and a start is an event.
    private static EventText? EventOf(JsonElement item)
    {
        var title = TextOf(item, TitleKeys);
        if (title is null || !HasKey(item, StartKeys))
        {
            return null;
        }

        var start = StartOf(item);
        var place = TextOf(item, PlaceKeys);
        var notes = TextOf(item, NoteKeys);
        var display = Clean(title, 120) + (start is null ? string.Empty : " (" + Clean(start, 40) + ")");
        return new EventText(Clean(title, 300), place is null ? null : Clean(place, 300), notes is null ? null : Clean(notes, 600), display);
    }

    private static string? StartOf(JsonElement item)
    {
        foreach (var key in StartKeys)
        {
            if (!TryGet(item, key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            // Google's way: {"dateTime": "...", "timeZone": "..."} or {"date": "..."}.
            if (value.ValueKind == JsonValueKind.Object)
            {
                return TextOf(value, ["datetime", "date", "start"]);
            }
        }

        return null;
    }

    private static bool HasKey(JsonElement item, string[] keys) => keys.Any(key => TryGet(item, key, out _));

    private static string? TextOf(JsonElement item, string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGet(item, key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    // A property by name, whatever its case.
    private static bool TryGet(JsonElement item, string key, out JsonElement value)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // One line of text from the app, cut to a length: control characters and angle brackets are taken out.
    private static string Clean(string text, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        var lastWasSpace = true;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '<' or '>')
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
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private sealed record EventText(string Title, string? Place, string? Notes, string Display);
}
