using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.MicrosoftTodo;

// Microsoft To Do as an MCP server, started by the Assistant (never by the user) with the access token for Microsoft Graph in MSGRAPH_ACCESS_TOKEN. One JSON message per line on the
// standard input; one per line back on the standard output; nothing else is written anywhere. The token is read once and is never printed, logged or kept.
var token = Environment.GetEnvironmentVariable("MSGRAPH_ACCESS_TOKEN");
Environment.SetEnvironmentVariable("MSGRAPH_ACCESS_TOKEN", null);
using var http = string.IsNullOrWhiteSpace(token) ? null : TodoGraph.Client(token);
var server = new TodoServer(http is null ? null : new TodoGraph(http));

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonObject? request;
    try
    {
        request = JsonNode.Parse(line) as JsonObject;
    }
    catch (JsonException)
    {
        continue;
    }

    if (request is null)
    {
        continue;
    }

    var reply = await server.HandleAsync(request, CancellationToken.None);
    if (reply is not null)
    {
        await Console.Out.WriteLineAsync(reply.ToJsonString());
        await Console.Out.FlushAsync();
    }

    // The sign-in ran out: the program ends, so that the next request starts it again with a new one.
    if (server.EndAfterReply)
    {
        return 3;
    }
}

return 0;
