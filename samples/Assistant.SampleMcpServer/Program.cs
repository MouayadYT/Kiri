using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

// Made-up MCP servers for trying the Assistant's installation of integrations (PROJECT_SPEC section 4.8, steps 108 and 116). One program, three samples, chosen by its first
// argument: "Sample Notes" (no argument; step 108), "Sample Calendar" (`--app calendar`) and "Sample Messages" (`--app messages`; step 116). Each speaks the protocol over its standard
// input and output, one JSON message per line, offers a few tools and does nothing else: it opens no connection and keeps nothing, so installing and running it is safe. The calendar's
// events are made up around today's date, so that an exam is always in the next two weeks; the messaging app has two chats with one person, a made-up Omar, and a group, and "sends"
// to no one. They are the only kind of integration this repository ships, and only the demos offer them. The one file ever touched is the log a test asks for with the environment variable
// ASSISTANT_SAMPLE_LOG, which the Assistant never sets.
const string ProtocolVersion = "2025-06-18";

var relaxed = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var app = args.Length >= 2 && args[0] == "--app" ? args[1] : "notes";
var notes = new[] { "Buy milk (sample)", "Call Anna on Friday (sample)" };
string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonNode? message;
    try
    {
        message = JsonNode.Parse(line);
    }
    catch (JsonException)
    {
        continue;
    }

    if (message is not JsonObject request || request["method"]?.GetValue<string>() is not { } method)
    {
        continue;
    }

    // A notification has no id and is not answered.
    if (request["id"] is not { } id)
    {
        continue;
    }

    JsonNode? result = null;
    JsonObject? error = null;
    switch (method)
    {
        case "initialize":
            result = new JsonObject
            {
                ["protocolVersion"] = request["params"]?["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "sample-" + app, ["version"] = "1.0.0" },
            };
            break;
        case "ping":
            result = new JsonObject();
            break;
        case "tools/list":
            result = new JsonObject { ["tools"] = ToolsOf(app) };
            break;
        case "tools/call":
            var name = request["params"]?["name"]?.GetValue<string>();
            var arguments = request["params"]?["arguments"] as JsonObject;
            Log(app, name, arguments);
            var text = CallTool(app, name, arguments);
            if (text is null)
            {
                error = new JsonObject { ["code"] = -32602, ["message"] = "Unknown tool." };
            }
            else
            {
                result = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = false,
                };
            }

            break;
        default:
            error = new JsonObject { ["code"] = -32601, ["message"] = "Method not found." };
            break;
    }

    var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
    if (error is not null)
    {
        reply["error"] = error;
    }
    else
    {
        reply["result"] = result;
    }

    Console.Out.WriteLine(reply.ToJsonString());
    Console.Out.Flush();
}

JsonArray ToolsOf(string sample) => sample switch
{
    "calendar" => new JsonArray
    {
        Tool(
            "list_events",
            "Lists the events in the sample calendar between a start and an end. Both are ISO 8601 dates such as 2026-10-03, in the user's time zone, and the end is not included. The events are made up.",
            Schema(
                ("start", "The start of the time to look at: an ISO 8601 date such as 2026-10-03.", true),
                ("end", "The end of the time to look at, not included: an ISO 8601 date such as 2026-10-17.", true)),
            readOnly: true),
        Tool(
            "search_events",
            "Looks for events in the sample calendar that have some words in their title, place or notes, optionally between a start and an end (ISO 8601 dates; the end is not included). The events are made up.",
            Schema(
                ("query", "The words to look for.", true),
                ("start", "Only events from this date on: an ISO 8601 date such as 2026-10-03.", false),
                ("end", "Only events before this date: an ISO 8601 date such as 2026-10-17.", false)),
            readOnly: true),
    },
    "messages" => new JsonArray
    {
        Tool(
            "search_chats",
            "Looks for chats in the sample messaging app by a name, a phone number or a username of someone in them. The chats are made up.",
            Schema(("query", "A name, a phone number or a username.", true)),
            readOnly: true),
        Tool(
            "send_message",
            "Sends a text to a chat in the sample messaging app. The sample sends it to no one.",
            Schema(("chat_id", "The id of the chat, from search_chats.", true), ("text", "What the message says.", true)),
            readOnly: false),
    },
    _ => new JsonArray
    {
        new JsonObject
        {
            ["name"] = "list_notes",
            ["description"] = "Lists the sample notes. It reads nothing from the PC: the notes are made up.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "add_note",
            ["description"] = "Adds a note. The sample keeps nothing: it only says it would have.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string", ["description"] = "The note." } },
                ["required"] = new JsonArray("text"),
            },
        },
    },
};

JsonObject Tool(string name, string description, JsonObject schema, bool readOnly)
{
    var tool = new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    if (readOnly)
    {
        tool["annotations"] = new JsonObject { ["readOnlyHint"] = true };
    }

    return tool;
}

JsonObject Schema(params (string Name, string Description, bool Required)[] properties)
{
    var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
    var required = new JsonArray();
    foreach (var (name, description, isRequired) in properties)
    {
        schema["properties"]![name] = new JsonObject { ["type"] = "string", ["description"] = description };
        if (isRequired)
        {
            required.Add(name);
        }
    }

    schema["required"] = required;
    return schema;
}

string? CallTool(string sample, string? tool, JsonObject? arguments) => sample switch
{
    "calendar" => tool switch
    {
        "list_events" => CalendarEvents(Argument(arguments, "start"), Argument(arguments, "end"), null),
        "search_events" => CalendarEvents(Argument(arguments, "start"), Argument(arguments, "end"), Argument(arguments, "query")),
        _ => null,
    },
    "messages" => tool switch
    {
        "search_chats" => SearchChats(Argument(arguments, "query")),
        "send_message" => SendMessage(Argument(arguments, "chat_id"), Argument(arguments, "text")),
        _ => null,
    },
    _ => tool switch
    {
        "list_notes" => string.Join('\n', notes.Select((note, index) => $"{index + 1}. {note}")),
        "add_note" => "Added (the sample keeps nothing): " + (arguments?["text"]?.GetValue<string>() ?? string.Empty),
        _ => null,
    },
};

string? Argument(JsonObject? arguments, string name) => arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

// The events are made up around today, in this PC's time zone, so that the exams are always in the next two weeks.
string CalendarEvents(string? start, string? end, string? query)
{
    var today = DateTime.Now.Date;
    (int Days, string Time, string Title, string? Place, string? Notes)[] all =
    [
        (1, "09:30", "Dentist appointment (sample)", "Smile Clinic", null),
        (3, "12:30", "Team lunch (sample)", null, null),
        (5, "09:00", "Physics final exam (sample)", "Hall B", "Bring a calculator and your student card."),
        (6, "15:00", "Study session for the chemistry midterm (sample)", "Library", null),
        (9, "13:00", "Calculus midterm (sample)", "Room 204", null),
        (11, "18:30", "Dinner with Anna (sample)", null, null),
        (13, "11:00", "Biology quiz (sample)", "Lab 3", null),
        (20, "09:00", "History final exam (sample)", "Hall A", null),
    ];

    var from = ParseDate(start) ?? today;
    var to = ParseDate(end) ?? from.AddDays(366);
    var events = new JsonArray();
    foreach (var item in all)
    {
        var begins = today.AddDays(item.Days).Add(TimeSpan.Parse(item.Time, CultureInfo.InvariantCulture));
        var haystack = (item.Title + " " + item.Place + " " + item.Notes).ToLowerInvariant();
        if (begins < from || begins >= to || query is not null && !query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(haystack.Contains))
        {
            continue;
        }

        var entry = new JsonObject
        {
            ["title"] = item.Title,
            ["start"] = begins.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["end"] = begins.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        };
        if (item.Place is not null)
        {
            entry["location"] = item.Place;
        }

        if (item.Notes is not null)
        {
            entry["notes"] = item.Notes;
        }

        events.Add(entry);
    }

    return new JsonObject
    {
        ["calendar"] = "Sample Calendar",
        ["sample"] = true,
        ["note"] = "These events are made up for trying the Assistant. They are not the user's own calendar, so say that they are samples.",
        ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["events"] = events,
    }.ToJsonString(relaxed);
}

DateTime? ParseDate(string? text) =>
    DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null;

// Three made-up chats: Omar, one other person, and a group that Omar is in. A name, a number or a username matches the people in them.
string SearchChats(string? query)
{
    var wanted = Compact(query);
    (string Id, string Title, string Type, (string Name, string Phone)[] People)[] chats =
    [
        ("sample-chat-omar", "Omar", "single", [("Omar", "+1 555 0100")]),
        ("sample-chat-lina", "Lina", "single", [("Lina", "+1 555 0123")]),
        ("sample-chat-family", "Family (sample group)", "group", [("Omar", "+1 555 0100"), ("Lina", "+1 555 0123"), ("Sami", "+1 555 0177")]),
    ];

    var found = new JsonArray();
    foreach (var chat in chats)
    {
        var matches = wanted.Length >= 3 && (Compact(chat.Title).Contains(wanted, StringComparison.Ordinal)
            || chat.People.Any(person => Compact(person.Name).Contains(wanted, StringComparison.Ordinal) || Compact(person.Phone).Contains(wanted, StringComparison.Ordinal)));
        if (!matches)
        {
            continue;
        }

        var people = new JsonArray();
        foreach (var person in chat.People)
        {
            people.Add(new JsonObject { ["name"] = person.Name, ["phone"] = person.Phone });
        }

        found.Add(new JsonObject { ["id"] = chat.Id, ["title"] = chat.Title, ["network"] = "Messages", ["type"] = chat.Type, ["participants"] = people });
    }

    return new JsonObject { ["chats"] = found, ["note"] = "These chats are made up for trying the Assistant." }.ToJsonString(relaxed);
}

string SendMessage(string? chatId, string? text) =>
    new JsonObject
    {
        ["status"] = "sent",
        ["sample"] = true,
        ["chat_id"] = chatId,
        ["characters"] = text?.Length ?? 0,
        ["note"] = "The sample messaging app sent this to no one and kept nothing.",
    }.ToJsonString(relaxed);

string Compact(string? text) => new((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

// What a test asked to be kept of the calls, in the file its environment variable names: the name of the tool and what it was given. Without the variable nothing is written.
void Log(string sample, string? tool, JsonObject? arguments)
{
    var path = Environment.GetEnvironmentVariable("ASSISTANT_SAMPLE_LOG");
    if (string.IsNullOrEmpty(path))
    {
        return;
    }

    try
    {
        File.AppendAllText(path, new JsonObject { ["app"] = sample, ["tool"] = tool, ["arguments"] = arguments?.DeepClone() }.ToJsonString() + "\n");
    }
    catch (IOException)
    {
        // A log that cannot be written is not a reason for the sample to fail.
    }
}
