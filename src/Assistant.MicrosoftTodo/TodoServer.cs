using System.Text.Json.Nodes;

namespace Assistant.MicrosoftTodo;

/// <summary>
/// The MCP side of Microsoft To Do (PROJECT_SPEC section 4.8): <c>initialize</c>, <c>ping</c>, <c>tools/list</c> and <c>tools/call</c> over JSON-RPC, and four tools: <c>list_task_lists</c>,
/// <c>list_tasks</c>, <c>create_task</c> and <c>complete_task</c>. The first two only read; the other two change what the user has, and the Assistant asks the user before each one.
/// Nothing deletes. An error is said in words in the tool's result, never with a token or an address in it.
/// </summary>
internal sealed class TodoServer(TodoGraph? graph)
{
    private const string ProtocolVersion = "2025-06-18";

    /// <summary>Whether the program should end after the reply it just made: the sign-in ran out, and a new start gets a new one.</summary>
    public bool EndAfterReply { get; private set; }

    /// <summary>Answers one message, or returns <see langword="null"/> for a notification, which is not answered.</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject request, CancellationToken cancellationToken)
    {
        if (request["method"]?.GetValue<string>() is not { } method || request["id"] is not { } id)
        {
            return null;
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
                    ["serverInfo"] = new JsonObject { ["name"] = "microsoft-todo", ["version"] = "1.0.0" },
                };
                break;
            case "ping":
                result = new JsonObject();
                break;
            case "tools/list":
                result = new JsonObject { ["tools"] = Tools() };
                break;
            case "tools/call":
                var name = request["params"]?["name"]?.GetValue<string>();
                var arguments = request["params"]?["arguments"] as JsonObject ?? [];
                if (name is not ("list_task_lists" or "list_tasks" or "create_task" or "complete_task"))
                {
                    error = new JsonObject { ["code"] = -32602, ["message"] = "Unknown tool." };
                    break;
                }

                result = await CallAsync(name, arguments, cancellationToken).ConfigureAwait(false);
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

        return reply;
    }

    private async Task<JsonObject> CallAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (graph is null)
        {
            return Reply("The Assistant is not signed in to Microsoft. Sign in to Microsoft To Do in Settings, under Integrations.", isError: true);
        }

        try
        {
            switch (name)
            {
                case "list_task_lists":
                    var lists = await graph.ListsAsync(cancellationToken).ConfigureAwait(false);
                    return Reply(new JsonObject { ["lists"] = new JsonArray([.. lists.Select(list => (JsonNode)new JsonObject { ["name"] = list.Name, ["default"] = list.IsDefault })]) });
                case "list_tasks":
                    var list = await graph.ResolveListAsync(Text(arguments, "list"), cancellationToken).ConfigureAwait(false);
                    var status = Text(arguments, "status") is "completed" or "all" ? Text(arguments, "status")! : "notCompleted";
                    var tasks = await graph.TasksAsync(list, status, Number(arguments, "limit", 25), cancellationToken).ConfigureAwait(false);
                    return Reply(new JsonObject { ["list"] = list.Name, ["tasks"] = new JsonArray([.. tasks.Select(Describe)]) });
                case "create_task":
                    var target = await graph.ResolveListAsync(Text(arguments, "list"), cancellationToken).ConfigureAwait(false);
                    var made = await graph.CreateAsync(target, Text(arguments, "title") ?? string.Empty, Text(arguments, "due"), Text(arguments, "notes"), cancellationToken).ConfigureAwait(false);
                    return Reply(new JsonObject { ["created"] = Describe(made), ["list"] = target.Name });
                default:
                    var where = await graph.ResolveListAsync(Text(arguments, "list"), cancellationToken).ConfigureAwait(false);
                    var done = await graph.CompleteAsync(where, Text(arguments, "task") ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    return Reply(new JsonObject { ["completed"] = Describe(done), ["list"] = where.Name });
            }
        }
        catch (TodoException exception)
        {
            EndAfterReply = exception.SignInProblem;
            return Reply(exception.Message, isError: true);
        }
    }

    private static JsonNode Describe(TodoTask task)
    {
        var item = new JsonObject { ["title"] = task.Title, ["done"] = task.Done };
        if (task.Due is not null)
        {
            item["due"] = task.Due;
        }

        if (task.Importance is { } importance and not "normal")
        {
            item["importance"] = importance;
        }

        return item;
    }

    private static JsonObject Reply(JsonNode data, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = data is JsonValue value && value.TryGetValue<string>(out var text) ? text : data.ToJsonString() }),
        ["isError"] = isError,
    };

    private static JsonObject Reply(string text, bool isError) => Reply(JsonValue.Create(text)!, isError);

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static int Number(JsonObject arguments, string name, int fallback) =>
        arguments[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    private static JsonArray Tools() => new(
        Tool("list_task_lists", "Lists the user's Microsoft To Do task lists, and says which is the default one.", [], readOnly: true),
        Tool(
            "list_tasks",
            "Lists the tasks in one of the user's Microsoft To Do lists (the default list when none is named): the ones still to do, or the finished ones, or all.",
            [("list", "The name of the list. Leave it out for the default list.", false), ("status", "notCompleted (the default), completed or all.", false), ("limit", "The most tasks to return, from 1 to 100; 25 by default.", false)],
            readOnly: true),
        Tool(
            "create_task",
            "Adds a task to one of the user's Microsoft To Do lists (the default list when none is named).",
            [("title", "What the task is, as the user said it.", true), ("list", "The name of the list. Leave it out for the default list.", false),
             ("due", "The day it is due, written as 2026-10-05.", false), ("notes", "More about the task.", false)],
            readOnly: false),
        Tool(
            "complete_task",
            "Marks a task in one of the user's Microsoft To Do lists as done (the default list when none is named). It is told by the task's title.",
            [("task", "The title of the task, or enough of it to tell it from the others.", true), ("list", "The name of the list. Leave it out for the default list.", false)],
            readOnly: false));

    private static JsonObject Tool(string name, string description, (string Name, string Help, bool Required)[] parameters, bool readOnly)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (parameter, help, isRequired) in parameters)
        {
            properties[parameter] = new JsonObject { ["type"] = parameter == "limit" ? "integer" : "string", ["description"] = help };
            if (isRequired)
            {
                required.Add(parameter);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = false },
        };
    }
}
