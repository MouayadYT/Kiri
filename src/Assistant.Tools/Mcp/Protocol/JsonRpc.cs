using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.Tools.Mcp;

/// <summary>What kind of JSON-RPC message it is.</summary>
internal enum JsonRpcKind
{
    /// <summary>A request: it has an id and a method, and wants an answer.</summary>
    Request,

    /// <summary>A notification: a method and no id.</summary>
    Notification,

    /// <summary>The successful answer to a request.</summary>
    Response,

    /// <summary>The failed answer to a request.</summary>
    Error,
}

/// <summary>A JSON-RPC error object.</summary>
/// <param name="Code">The code.</param>
/// <param name="Message">What the server said, at most <see cref="JsonRpcMessage.MaxErrorMessageLength"/> characters. Third-party text: never logged.</param>
/// <param name="Data">The error's data, when it gave some.</param>
internal sealed record JsonRpcError(int Code, string? Message, JsonElement? Data);

/// <summary>One message of an MCP conversation, read. Its elements are copies, so they stay valid after the text they were read from is gone.</summary>
internal sealed class JsonRpcMessage
{
    /// <summary>The most characters of an error's message that are kept.</summary>
    public const int MaxErrorMessageLength = 500;

    /// <summary>What kind of message it is.</summary>
    public required JsonRpcKind Kind { get; init; }

    /// <summary>The message's id, exactly as it was written (to be echoed in an answer); <see langword="null"/> for a notification.</summary>
    public JsonElement? Id { get; init; }

    /// <summary>The method, for a request or a notification.</summary>
    public string? Method { get; init; }

    /// <summary>The parameters, for a request or a notification; <see cref="JsonValueKind.Undefined"/> when there are none.</summary>
    public JsonElement Params { get; init; }

    /// <summary>The result, for a response.</summary>
    public JsonElement Result { get; init; }

    /// <summary>The error, for an error response.</summary>
    public JsonRpcError? Error { get; init; }

    /// <summary>The HTTP status the message came with, when it came over HTTP and was not a success; <see langword="null"/> otherwise.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>The id in a form that can be compared with another: a number as its digits, a string as itself. <see langword="null"/> when there is no id.</summary>
    public string? IdKey => Id is { } id ? KeyOf(id) : null;

    /// <summary>The same message, noted as having come with HTTP status <paramref name="status"/>.</summary>
    public JsonRpcMessage WithHttpStatus(int status) => new()
    {
        Kind = Kind,
        Id = Id,
        Method = Method,
        Params = Params,
        Result = Result,
        Error = Error,
        HttpStatus = status,
    };

    /// <summary>The comparable form of an id.</summary>
    public static string? KeyOf(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.GetRawText(),
        JsonValueKind.String => id.GetString(),
        _ => null,
    };

    /// <summary>Reads the messages in <paramref name="utf8"/>: one message, or an array of them (a batch, which older servers may send).</summary>
    /// <returns>The messages that could be read; an empty list when the text is not JSON-RPC.</returns>
    public static IReadOnlyList<JsonRpcMessage> Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            var messages = new List<JsonRpcMessage>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    if (TryRead(element) is { } read)
                    {
                        messages.Add(read);
                    }
                }
            }
            else if (TryRead(root) is { } single)
            {
                messages.Add(single);
            }

            return messages;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonRpcMessage? TryRead(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hasId = element.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var method = element.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString()
            : null;
        if (method is not null)
        {
            return new JsonRpcMessage
            {
                Kind = hasId ? JsonRpcKind.Request : JsonRpcKind.Notification,
                Id = hasId ? id.Clone() : null,
                Method = method,
                Params = element.TryGetProperty("params", out var parameters) ? parameters.Clone() : default,
            };
        }

        if (element.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var number)
                ? number
                : -32603;
            var text = error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
            if (text is { Length: > MaxErrorMessageLength })
            {
                text = text[..MaxErrorMessageLength];
            }

            return new JsonRpcMessage
            {
                Kind = JsonRpcKind.Error,
                Id = hasId ? id.Clone() : null,
                Error = new JsonRpcError(code, text, error.TryGetProperty("data", out var data) ? data.Clone() : null),
            };
        }

        if (hasId && element.TryGetProperty("result", out var result))
        {
            return new JsonRpcMessage { Kind = JsonRpcKind.Response, Id = id.Clone(), Result = result.Clone() };
        }

        return null;
    }
}

/// <summary>A message the Assistant sends, already written as JSON, with what a transport needs to know about it.</summary>
/// <param name="Id">The request's id; <see langword="null"/> for a notification.</param>
/// <param name="Method">The method.</param>
/// <param name="Json">The message as UTF-8 JSON, on one line.</param>
/// <param name="Headers">For HTTP, the headers this message goes with besides the usual ones (<c>Mcp-Method</c> and the like); other transports ignore them.</param>
internal sealed record OutgoingMessage(long? Id, string Method, byte[] Json, IReadOnlyList<KeyValuePair<string, string>>? Headers = null);

/// <summary>Writes the messages the Assistant sends.</summary>
internal static class JsonRpc
{
    /// <summary>Writes a request. <paramref name="parameters"/> is taken over: its <c>_meta</c> may be added to by the caller beforehand.</summary>
    public static OutgoingMessage Request(
        long id, string method, JsonObject? parameters, IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return new OutgoingMessage(id, method, Serialize(message), headers);
    }

    /// <summary>Writes a notification.</summary>
    public static OutgoingMessage Notification(string method, JsonObject? parameters = null)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return new OutgoingMessage(null, method, Serialize(message));
    }

    /// <summary>Writes the answer to a request the server made: an empty result (<c>ping</c>).</summary>
    public static byte[] EmptyResult(JsonElement id) =>
        Serialize(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = new JsonObject() });

    /// <summary>Writes the answer to a request the server made that the Assistant does not serve (method not found).</summary>
    public static byte[] MethodNotFound(JsonElement id) =>
        Serialize(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["error"] = new JsonObject { ["code"] = McpProtocol.MethodNotFound, ["message"] = "Method not found" },
        });

    /// <summary>The answer to a request the server made, as the Assistant gives it: <c>ping</c> is answered, nothing else is served.</summary>
    public static byte[]? AnswerServerRequest(JsonRpcMessage request)
    {
        if (request.Kind != JsonRpcKind.Request || request.Id is not { } id)
        {
            return null;
        }

        return request.Method == McpProtocol.Ping ? EmptyResult(id) : MethodNotFound(id);
    }

    private static byte[] Serialize(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node);
}
