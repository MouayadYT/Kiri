using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Assistant.ModelHost.FakeEngine;

/// <summary>
/// The little of llama-server's HTTP API the model host uses, on the UNIX socket it was told to listen on:
/// <c>GET /health</c>, and <c>POST /v1/chat/completions</c> answered as a stream of server-sent events the way the real
/// server writes them (a role chunk, text chunks, a finish chunk that repeats the model's path, a usage chunk and
/// <c>[DONE]</c>), as its <see cref="FakeChatReply"/> says. Each connection serves one request and closes.
/// </summary>
internal sealed class FakeChatServer
{
    private readonly Socket _listener;
    private readonly string _scenarioPath;
    private readonly FakeChatReply _reply;
    private int _requests;

    private FakeChatServer(Socket listener, string scenarioPath, FakeChatReply reply)
    {
        _listener = listener;
        _scenarioPath = scenarioPath;
        _reply = reply;
    }

    /// <summary>Starts listening on <paramref name="socketPath"/> and serving in the background.</summary>
    public static FakeChatServer Listen(string socketPath, string scenarioPath, FakeChatReply reply)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(16);
        var server = new FakeChatServer(listener, scenarioPath, reply);
        _ = Task.Run(server.AcceptAsync);
        return server;
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            var client = await _listener.AcceptAsync();
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(Socket client)
    {
        await using var stream = new NetworkStream(client, ownsSocket: true);
        var request = await ReadRequestAsync(stream);
        if (request is null)
        {
            return;
        }

        var (method, path, body) = request.Value;
        if (method == "GET" && path == "/health")
        {
            await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", """{"status":"ok"}""");
        }
        else if (method == "POST" && path == "/v1/chat/completions")
        {
            await ChatAsync(stream, body);
        }
        else
        {
            await WriteResponseAsync(stream, 404, "application/json; charset=utf-8", """{"error":{"code":404,"message":"File Not Found","type":"not_found_error"}}""");
        }
    }

    private async Task ChatAsync(NetworkStream stream, byte[] body)
    {
        var number = Interlocked.Increment(ref _requests);
        await File.WriteAllBytesAsync(FakeChatReply.RequestPath(_scenarioPath, number), body);
        switch (_reply.Error)
        {
            case "context":
                await WriteResponseAsync(stream, 400, "application/json; charset=utf-8",
                    """{"error":{"code":400,"message":"request (15041 tokens) exceeds the available context size (2048 tokens), try increasing it","type":"exceed_context_size_error","n_prompt_tokens":15041,"n_ctx":2048}}""");
                return;
            case "server":
                await WriteResponseAsync(stream, 500, "application/json; charset=utf-8",
                    """{"error":{"code":500,"message":"PRIVATE-PROMPT-7f3c: summarize my tax letter","type":"server_error"}}""");
                return;
        }

        try
        {
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
            await Task.Delay(_reply.FirstPieceDelayMs);
            await WriteEventAsync(stream, "data", Chunk(new { role = "assistant", content = (string?)null }, null));
            for (var i = 0; i < _reply.Pieces.Count; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(_reply.PieceDelayMs);
                }

                await WriteEventAsync(stream, "data", Chunk(new { content = _reply.Pieces[i], reasoning_content = "PRIVATE-REASONING-51aa" }, null));
                if (_reply.CrashMidAnswer)
                {
                    await stream.FlushAsync();
                    Environment.Exit(3);
                }
            }

            if (_reply.BreakOff)
            {
                return;
            }

            if (_reply.Error == "stream")
            {
                await WriteEventAsync(stream, "error", JsonSerializer.Serialize(new { code = 500, message = "PRIVATE-ANSWER-4d2e", type = "server_error" }));
                await WriteAsync(stream, "0\r\n\r\n");
                return;
            }

            for (var i = 0; i < _reply.ToolCalls.Count; i++)
            {
                var call = _reply.ToolCalls[i];
                var half = call.Arguments.Length / 2;
                await WriteEventAsync(stream, "data", Chunk(new { tool_calls = new object[] { new { index = i, id = $"call_{i}", type = "function", function = new { name = call.Name, arguments = call.Arguments[..half] } } } }, null));
                await WriteEventAsync(stream, "data", Chunk(new { tool_calls = new object[] { new { index = i, function = new { arguments = call.Arguments[half..] } } } }, null));
            }

            await WriteEventAsync(stream, "data", Chunk(new { }, _reply.FinishReason));
            await WriteEventAsync(stream, "data", JsonSerializer.Serialize(new
            {
                choices = Array.Empty<object>(),
                model = _scenarioPath,
                @object = "chat.completion.chunk",
                usage = new { completion_tokens = _reply.Pieces.Count, prompt_tokens = _reply.PromptTokens, total_tokens = _reply.Pieces.Count + _reply.PromptTokens },
            }));
            await WriteEventAsync(stream, "data", "[DONE]");
            await WriteAsync(stream, "0\r\n\r\n");
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // The client went away before the answer ended, as the host does when a generation is stopped.
            await File.WriteAllTextAsync(FakeChatReply.DisconnectedPath(_scenarioPath, number), "");
        }
    }

    // A chunk as the real server writes it, repeating the model's path as it does.
    private string Chunk(object delta, string? finishReason) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { finish_reason = finishReason, index = 0, delta } },
        created = 1790732806,
        id = "chatcmpl-fake",
        model = _scenarioPath,
        system_fingerprint = "b11146-fake",
        @object = "chat.completion.chunk",
    });

    // One server-sent event in one HTTP chunk.
    private static Task WriteEventAsync(Stream stream, string field, string value)
    {
        var payload = Encoding.UTF8.GetBytes($"{field}: {value}\n\n");
        return WriteAsync(stream, $"{payload.Length:x}\r\n{Encoding.UTF8.GetString(payload)}\r\n");
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Internal Server Error" };
        await WriteAsync(stream, $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n{body}");
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        await stream.FlushAsync();
    }

    // The request line, and the body as long as Content-Length says.
    private static async Task<(string Method, string Path, byte[] Body)?> ReadRequestAsync(Stream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(head))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                return null;
            }

            head.Add(one[0]);
        }

        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var length = lines
            .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(line => int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(body.AsMemory(read));
            if (count == 0)
            {
                return null;
            }

            read += count;
        }

        return (requestLine[0], requestLine[1], body);
    }

    private static bool EndsWithBlankLine(List<byte> head) =>
        head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n';
}
