using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Assistant.Tools.Mcp.Auth;

/// <summary>
/// The small web server that takes the user's browser back to the Assistant at the end of a sign-in (RFC 8252: a redirect to a port on this PC, which only this
/// PC can reach). It listens on <c>127.0.0.1</c> alone, on a port the system picks, for one request, and answers it with a short page that says the sign-in is
/// done and the window can be closed. It reads only the first line of the request, never more than a few kilobytes, and ignores anything that is not the callback.
/// </summary>
internal sealed class LoopbackAuthorizationListener : IDisposable
{
    private const int MaxRequestBytes = 8 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private bool _rootPath;

    /// <summary>
    /// The address as it is sent to the service and sent again with the code: <c>http://localhost:1234</c> with no path when the return is to the root, which is how an app
    /// registered as <c>http://localhost</c> must be called (the port is ignored when matching a loopback address, the path is not), and otherwise the whole address.
    /// </summary>
    public string RedirectText { get; private set; } = string.Empty;

    /// <summary>Starts listening and returns the address the browser is sent back to, named <paramref name="host"/> (always this PC: the listener takes only the loopback address).</summary>
    /// <param name="host">The name the browser is sent back to.</param>
    /// <param name="rootPath">Return to the root (<c>http://localhost:1234</c>) and not to <c>/callback</c>, for a service whose registered address has no path.</param>
    public Uri Start(string host = "127.0.0.1", bool rootPath = false)
    {
        _listener.Start(1);
        _rootPath = rootPath;
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var address = new Uri($"http://{host}:{port}/callback");
        RedirectText = rootPath ? $"http://{host}:{port}" : address.AbsoluteUri;
        return address;
    }

    /// <summary>
    /// Waits for the browser's return and gives the query of the callback as it came: <c>code</c> and <c>state</c>, or <c>error</c>. A request for anything else (a
    /// browser asking for an icon) is answered and waited past.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(string successPage, string failurePage, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            client.ReceiveTimeout = 5000;
            await using var stream = client.GetStream();
            var line = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
            var query = ParseCallback(line, _rootPath);
            if (query is null)
            {
                await RespondAsync(stream, 404, "Not found", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var ok = query.ContainsKey("code") && !query.ContainsKey("error");
            await RespondAsync(stream, 200, ok ? successPage : failurePage, cancellationToken).ConfigureAwait(false);
            return query;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _listener.Stop();

    /// <summary>The query of a request line such as <c>GET /callback?code=a&amp;state=b HTTP/1.1</c>, or <see langword="null"/> when the line is not a request for the callback.</summary>
    internal static Dictionary<string, string>? ParseCallback(string? line, bool rootPath = false)
    {
        if (line is null)
        {
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "GET")
        {
            return null;
        }

        // A return to the root is only the callback when it carries an answer, so that a request for an icon or the bare address is waited past.
        var isCallback = rootPath
            ? parts[1].StartsWith("/?", StringComparison.Ordinal) && (parts[1].Contains("code=", StringComparison.Ordinal) || parts[1].Contains("error=", StringComparison.Ordinal))
            : parts[1].StartsWith("/callback", StringComparison.Ordinal);
        if (!isCallback)
        {
            return null;
        }

        var target = parts[1];
        var mark = target.IndexOf('?', StringComparison.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mark < 0)
        {
            return result;
        }

        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            if (key.Length is > 0 and <= 40 && value.Length <= 4096)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (end >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
            }
        }

        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string page, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Assistant</title>" +
            "<style>body{font:16px system-ui,sans-serif;background:#202020;color:#fff;display:grid;place-items:center;height:100vh;margin:0}</style></head>" +
            "<body><p>" + WebUtility.HtmlEncode(page) + "</p></body></html>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
