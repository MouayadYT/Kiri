using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.MicrosoftTodo;

/// <summary>Why a call to Microsoft To Do did not do what was asked, in words for the user. It never holds a token or an address.</summary>
internal sealed class TodoException(string message, bool signInProblem = false) : Exception(message)
{
    /// <summary>Whether the cause is the sign-in (it ran out, or was refused): the program ends after saying so, so the next request starts it with a new one.</summary>
    public bool SignInProblem { get; } = signInProblem;
}

/// <summary>A list of tasks.</summary>
internal sealed record TodoList(string Id, string Name, bool IsDefault);

/// <summary>A task.</summary>
internal sealed record TodoTask(string Id, string Title, bool Done, string? Due, string? Importance);

/// <summary>
/// The part of Microsoft Graph that Microsoft To Do is: its lists and the tasks in them (<c>/me/todo/lists</c>). It does only what the user asked of it (list, create and complete),
/// and deleting is not among it. The access token is on the <see cref="HttpClient"/> it is given, and nothing here writes it anywhere.
/// </summary>
internal sealed class TodoGraph(HttpClient http)
{
    /// <summary>Where Microsoft Graph is.</summary>
    public static readonly Uri Base = new("https://graph.microsoft.com/v1.0/");

    private const int MaxTitle = 255;
    private const int MaxNotes = 10_000;

    /// <summary>Makes the client for a token: Graph's address and the token as a bearer.</summary>
    public static HttpClient Client(string token, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = Base;
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Every list, the default one marked.</summary>
    public async Task<IReadOnlyList<TodoList>> ListsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("me/todo/lists?$top=100", cancellationToken).ConfigureAwait(false);
        var lists = new List<TodoList>();
        foreach (var item in Values(document))
        {
            if (Text(item, "id") is { } id)
            {
                lists.Add(new TodoList(id, Text(item, "displayName") ?? "Untitled list", Text(item, "wellknownListName") == "defaultList"));
            }
        }

        return lists;
    }

    /// <summary>The list called <paramref name="name"/> (without regard to case), or the default list when none is named.</summary>
    public async Task<TodoList> ResolveListAsync(string? name, CancellationToken cancellationToken)
    {
        var lists = await ListsAsync(cancellationToken).ConfigureAwait(false);
        if (lists.Count == 0)
        {
            throw new TodoException("There are no task lists in Microsoft To Do.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return lists.FirstOrDefault(list => list.IsDefault) ?? lists[0];
        }

        var wanted = name.Trim();
        var found = lists.Where(list => string.Equals(list.Name, wanted, StringComparison.OrdinalIgnoreCase) || list.Id == wanted).ToList();
        return found.Count == 1
            ? found[0]
            : found.Count > 1
                ? throw new TodoException($"More than one list is called \"{Plain(wanted)}\". Say which.")
                : throw new TodoException($"There is no list called \"{Plain(wanted)}\". The lists are: {string.Join(", ", lists.Select(list => "\"" + Plain(list.Name) + "\""))}.");
    }

    /// <summary>The tasks in a list: the ones still to do, or the finished ones, or all.</summary>
    public async Task<IReadOnlyList<TodoTask>> TasksAsync(TodoList list, string status, int limit, CancellationToken cancellationToken)
    {
        var filter = status switch
        {
            "completed" => "&$filter=" + Uri.EscapeDataString("status eq 'completed'"),
            "all" => string.Empty,
            _ => "&$filter=" + Uri.EscapeDataString("status ne 'completed'"),
        };
        using var document = await GetAsync($"me/todo/lists/{Uri.EscapeDataString(list.Id)}/tasks?$top={Math.Clamp(limit, 1, 100)}{filter}", cancellationToken).ConfigureAwait(false);
        return [.. Values(document).Select(TaskOf).OfType<TodoTask>()];
    }

    /// <summary>Adds a task to a list.</summary>
    /// <param name="list">The list.</param>
    /// <param name="title">What the task is.</param>
    /// <param name="due">The day it is due, as <c>yyyy-MM-dd</c>, or <see langword="null"/>.</param>
    /// <param name="notes">More about it, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<TodoTask> CreateAsync(TodoList list, string title, string? due, string? notes, CancellationToken cancellationToken)
    {
        var clean = (title ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new TodoException("The task needs a title.");
        }

        if (clean.Length > MaxTitle)
        {
            throw new TodoException($"The title is longer than {MaxTitle} characters.");
        }

        var body = new JsonObject { ["title"] = clean };
        if (!string.IsNullOrWhiteSpace(notes))
        {
            body["body"] = new JsonObject { ["content"] = notes.Length > MaxNotes ? notes[..MaxNotes] : notes, ["contentType"] = "text" };
        }

        if (!string.IsNullOrWhiteSpace(due))
        {
            if (!DateOnly.TryParseExact(due.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
            {
                throw new TodoException("The due date must be a day written as 2026-10-05.");
            }

            body["dueDateTime"] = new JsonObject { ["dateTime"] = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "T00:00:00", ["timeZone"] = TimeZoneId() };
        }

        using var document = await SendAsync(HttpMethod.Post, $"me/todo/lists/{Uri.EscapeDataString(list.Id)}/tasks", body, cancellationToken).ConfigureAwait(false);
        return TaskOf(document.RootElement) ?? throw new TodoException("Microsoft To Do did not say what it made.");
    }

    /// <summary>Marks the one task called <paramref name="title"/> in the list as done.</summary>
    public async Task<TodoTask> CompleteAsync(TodoList list, string title, CancellationToken cancellationToken)
    {
        var wanted = (title ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            throw new TodoException("Say which task to complete.");
        }

        var open = await TasksAsync(list, "notCompleted", 100, cancellationToken).ConfigureAwait(false);
        var found = open.Where(task => string.Equals(task.Title, wanted, StringComparison.OrdinalIgnoreCase) || task.Id == wanted).ToList();
        if (found.Count == 0)
        {
            // A part of a title is accepted when it names one task.
            found = [.. open.Where(task => task.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        }

        if (found.Count == 0)
        {
            throw new TodoException($"No task in \"{Plain(list.Name)}\" is called \"{Plain(wanted)}\".");
        }

        if (found.Count > 1)
        {
            throw new TodoException($"More than one task fits \"{Plain(wanted)}\": {string.Join(", ", found.Take(5).Select(task => "\"" + Plain(task.Title) + "\""))}. Say which.");
        }

        using var document = await SendAsync(
            new HttpMethod("PATCH"), $"me/todo/lists/{Uri.EscapeDataString(list.Id)}/tasks/{Uri.EscapeDataString(found[0].Id)}", new JsonObject { ["status"] = "completed" }, cancellationToken)
            .ConfigureAwait(false);
        return TaskOf(document.RootElement) ?? found[0] with { Done = true };
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken) => await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new TodoException("Microsoft To Do did not answer. Check the connection and try again.");
        }
        catch (JsonException)
        {
            throw new TodoException("Microsoft To Do gave an answer I could not read.");
        }
    }

    private static TodoException Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new TodoException("Microsoft refused the sign-in, or it has run out. Ask again and the Assistant will use a new one, or sign in again in Settings.", signInProblem: true),
        HttpStatusCode.Forbidden => new TodoException("Microsoft did not allow this. The account may not have Microsoft To Do, or the Assistant was not given permission to use it when you signed in."),
        HttpStatusCode.NotFound => new TodoException("Microsoft To Do could not find that list or task."),
        HttpStatusCode.TooManyRequests => new TodoException("Microsoft To Do asked to slow down. Try again in a minute."),
        >= HttpStatusCode.InternalServerError => new TodoException("Microsoft To Do is not working just now. Try again later."),
        _ => new TodoException("Microsoft To Do could not do that."),
    };

    private static IEnumerable<JsonElement> Values(JsonDocument document) =>
        document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static TodoTask? TaskOf(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Text(item, "id") is not { } id)
        {
            return null;
        }

        string? due = item.TryGetProperty("dueDateTime", out var dueTime) && dueTime.ValueKind == JsonValueKind.Object ? Text(dueTime, "dateTime") : null;
        return new TodoTask(id, Text(item, "title") ?? "Untitled task", Text(item, "status") == "completed", due is { Length: >= 10 } ? due[..10] : null, Text(item, "importance"));
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string TimeZoneId() => TimeZoneInfo.Local.Id;

    // A name in a sentence of ours: one line, no quotation mark that could end it early.
    private static string Plain(string text) => new string([.. text.Where(character => !char.IsControl(character) && character != '"')]).Trim();
}
