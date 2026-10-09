using System.Text;

namespace Assistant.Documents.Web;

/// <summary>
/// Finds the page in a web archive (<c>.mhtml</c>, <c>.mht</c>: what a browser's "save as single file" writes): a MIME message whose
/// parts are the page's HTML, its pictures and its styles. Only the page itself is taken: the first <c>text/html</c> part, else the
/// first <c>text/plain</c> one, found through nested multipart parts, with its transfer encoding (quoted-printable, base64, 7bit,
/// 8bit) and its character set undone. The other parts (pictures, scripts, styles) are never decoded, and nothing is run or fetched.
/// </summary>
internal static class MimeHtmlExtractor
{
    private const int MaxDepth = 6;
    private const int MaxParts = 2_000;

    /// <summary>
    /// The HTML of every page in the archive, in the order they come (the page itself first, then the frames it holds, whose text
    /// is often the part that matters), or, when it has none, its first plain text part as the only one. Empty when it has neither.
    /// </summary>
    public static (IReadOnlyList<string> Parts, bool IsHtml) Find(string raw, CancellationToken cancellationToken)
    {
        var parts = 0;
        string? plain = null;
        var html = new List<string>();
        Collect(raw, 0, raw.Length, 0, ref parts, ref plain, html, cancellationToken);
        if (html.Count > 0)
        {
            return (html, true);
        }

        return plain is not null ? ([plain], false) : ([], false);
    }

    // Looks in the part raw[start..end] (headers, blank line, body).
    private static void Collect(
        string raw, int start, int end, int depth, ref int parts, ref string? plain, List<string> html, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaxDepth || ++parts > MaxParts)
        {
            return;
        }

        var bodyStart = ReadHeaders(raw, start, end, out var headers);
        var contentType = headers.GetValueOrDefault("content-type") ?? "text/plain";
        var mediaType = contentType.Split(';')[0].Trim().ToLowerInvariant();

        if (mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            var boundary = Parameter(contentType, "boundary");
            if (boundary is null)
            {
                return;
            }

            foreach (var (partStart, partEnd) in SplitParts(raw, bodyStart, end, boundary))
            {
                Collect(raw, partStart, partEnd, depth + 1, ref parts, ref plain, html, cancellationToken);
            }

            return;
        }

        if (mediaType is not ("text/html" or "application/xhtml+xml" or "text/plain"))
        {
            return;
        }

        var text = DecodeBody(raw, bodyStart, end, headers, contentType);
        if (mediaType == "text/plain")
        {
            plain ??= text;
        }
        else
        {
            html.Add(text);
        }
    }

    // Reads the header lines from raw[start..], unfolding continued lines, and returns where the body begins.
    private static int ReadHeaders(string raw, int start, int end, out Dictionary<string, string> headers)
    {
        headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = start;
        string? name = null;
        while (at < end)
        {
            var lineEnd = raw.IndexOf('\n', at, end - at);
            var next = lineEnd < 0 ? end : lineEnd + 1;
            var line = raw[at..(lineEnd < 0 ? end : lineEnd)].TrimEnd('\r');
            if (line.Length == 0)
            {
                // A part may begin with its blank line when it has no headers.
                return next;
            }

            if ((line[0] is ' ' or '\t') && name is not null)
            {
                headers[name] += " " + line.Trim();
            }
            else
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    // Not a header: the part has none, and this is its body.
                    return at;
                }

                name = line[..colon].Trim().ToLowerInvariant();
                headers[name] = line[(colon + 1)..].Trim();
            }

            at = next;
        }

        return end;
    }

    // The parts between the boundary lines of a multipart body, as ranges of raw.
    private static IEnumerable<(int Start, int End)> SplitParts(string raw, int bodyStart, int end, string boundary)
    {
        var delimiter = "--" + boundary;
        var at = bodyStart;
        var partStart = -1;
        while (at < end)
        {
            var found = raw.IndexOf(delimiter, at, end - at, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            var atLineStart = found == bodyStart || raw[found - 1] == '\n';
            var after = found + delimiter.Length;
            if (!atLineStart)
            {
                at = after;
                continue;
            }

            if (partStart >= 0)
            {
                // The line break before a delimiter belongs to the delimiter.
                var partEnd = found;
                if (partEnd > partStart && raw[partEnd - 1] == '\n')
                {
                    partEnd--;
                }

                if (partEnd > partStart && raw[partEnd - 1] == '\r')
                {
                    partEnd--;
                }

                yield return (partStart, partEnd);
            }

            if (after + 1 < end && raw[after] == '-' && raw[after + 1] == '-')
            {
                yield break;
            }

            var lineEnd = raw.IndexOf('\n', after, end - after);
            partStart = lineEnd < 0 ? end : lineEnd + 1;
            at = partStart;
        }

        if (partStart >= 0 && partStart < end)
        {
            yield return (partStart, end);
        }
    }

    private static string DecodeBody(string raw, int start, int end, Dictionary<string, string> headers, string contentType)
    {
        var body = raw[start..end];
        var encoding = (headers.GetValueOrDefault("content-transfer-encoding") ?? "7bit").Trim().ToLowerInvariant();
        byte[] bytes = encoding switch
        {
            "quoted-printable" => DecodeQuotedPrintable(body),
            "base64" => DecodeBase64(body),
            _ => Encoding.Latin1.GetBytes(body),
        };

        return CharacterSet(Parameter(contentType, "charset")).GetString(bytes);
    }

    private static byte[] DecodeBase64(string body)
    {
        var clean = new StringBuilder(body.Length);
        foreach (var c in body)
        {
            if (!char.IsWhiteSpace(c))
            {
                clean.Append(c);
            }
        }

        try
        {
            return Convert.FromBase64String(clean.ToString());
        }
        catch (FormatException)
        {
            return [];
        }
    }

    // "=41" is a byte, "=" at the end of a line joins it to the next.
    private static byte[] DecodeQuotedPrintable(string body)
    {
        var bytes = new List<byte>(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '=')
            {
                bytes.Add((byte)c);
                continue;
            }

            if (i + 1 < body.Length && body[i + 1] == '\n')
            {
                i++;
            }
            else if (i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n')
            {
                i += 2;
            }
            else if (i + 2 < body.Length && Hex(body[i + 1]) is var high and >= 0 && Hex(body[i + 2]) is var low and >= 0)
            {
                bytes.Add((byte)((high << 4) | low));
                i += 2;
            }
            else
            {
                bytes.Add((byte)'=');
            }
        }

        return [.. bytes];
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };

    private static Encoding CharacterSet(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                // The Windows code pages (windows-1252 and the like) are in a provider of their own.
                var charset = name.Trim().Trim('"');
                return CodePagesEncodingProvider.Instance.GetEncoding(charset) ?? Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // An unknown character set is read as UTF-8, which is what nearly every page is.
            }
        }

        return new UTF8Encoding(false);
    }

    // The value of a header parameter: boundary="x" or charset=utf-8.
    private static string? Parameter(string header, string name)
    {
        var search = name + "=";
        var at = 0;
        while ((at = header.IndexOf(search, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (at == 0 || header[at - 1] is ';' or ' ' or '\t')
            {
                var valueStart = at + search.Length;
                if (valueStart < header.Length && header[valueStart] == '"')
                {
                    var close = header.IndexOf('"', valueStart + 1);
                    return close < 0 ? header[(valueStart + 1)..] : header[(valueStart + 1)..close];
                }

                var stop = header.IndexOf(';', valueStart);
                return (stop < 0 ? header[valueStart..] : header[valueStart..stop]).Trim();
            }

            at += search.Length;
        }

        return null;
    }
}
