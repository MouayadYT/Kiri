using System.Runtime.CompilerServices;
using System.Text;

namespace Assistant.Tools.Mcp;

/// <summary>One event of a Server-Sent Events stream.</summary>
/// <param name="Type">The event's type (<c>message</c> when the server named none).</param>
/// <param name="Data">Its data: the lines of data joined by line feeds.</param>
internal readonly record struct SseEvent(string Type, string Data);

/// <summary>
/// Reads a Server-Sent Events stream (the way MCP servers stream answers over HTTP): lines of <c>field: value</c>, comments that begin with a colon
/// (keep-alives), and an event ended by an empty line. An event's data is never allowed to grow past the limit, so a server that streams on
/// without ending an event cannot make the Assistant use more and more memory. An event that is cut off by the end of the stream is given as far as it came.
/// </summary>
internal static class SseReader
{
    /// <summary>The events of <paramref name="stream"/>, as they arrive, until it ends.</summary>
    /// <exception cref="McpException">An event is longer than <paramref name="maxEventBytes"/> (<see cref="McpFailure.TooLarge"/>).</exception>
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream, int maxEventBytes, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = new BoundedLineReader(stream, maxEventBytes);
        var data = new StringBuilder();
        var hasData = false;
        var type = "message";
        var first = true;
        var size = 0L;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } bytes)
        {
            var line = Encoding.UTF8.GetString(bytes);
            if (first)
            {
                first = false;
                line = line.TrimStart((char)0xFEFF);
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new SseEvent(type, data.ToString());
                }

                data.Clear();
                hasData = false;
                type = "message";
                size = 0;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    type = value.Length == 0 ? "message" : value;
                    break;
                case "data":
                    size += bytes.Length;
                    if (size > maxEventBytes)
                    {
                        throw new McpException(McpFailure.TooLarge);
                    }

                    if (hasData)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    hasData = true;
                    break;
            }
        }

        if (hasData)
        {
            yield return new SseEvent(type, data.ToString());
        }
    }
}
