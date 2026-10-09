using System.Net;
using System.Net.Http.Headers;

namespace Assistant.Tools.Mcp;

/// <summary>What the two HTTP transports share: how a request is made, how an answer is read within a limit, and how a status becomes a failure.</summary>
internal static class McpHttp
{
    /// <summary>The longest <c>Mcp-Session-Id</c> that is accepted.</summary>
    private const int MaxSessionIdLength = 256;

    /// <summary>The handler the app's own HTTP clients use: no redirects (a server cannot send the Assistant, and its sign-in, somewhere else), no cookies.</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>A request to <paramref name="uri"/> with the headers the integration's sign-in gives. The headers' values are secrets and are never logged.</summary>
    public static HttpRequestMessage Request(HttpMethod method, Uri uri, IReadOnlyDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(method, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    /// <summary>Whether the answer's content is of <paramref name="mediaType"/> (<c>application/json</c>), whatever parameters follow it.</summary>
    public static bool IsMediaType(HttpResponseMessage response, string mediaType) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="value"/> can be a session id: visible ASCII only, and not long.</summary>
    public static bool IsValidSessionId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxSessionIdLength && value.All(character => character is >= '!' and <= '~');

    /// <summary>Reads the whole body, but never more than <paramref name="maxBytes"/>.</summary>
    /// <exception cref="McpException">The body is longer (<see cref="McpFailure.TooLarge"/>).</exception>
    public static async Task<byte[]> ReadBodyAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new McpException(McpFailure.TooLarge);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > maxBytes)
            {
                throw new McpException(McpFailure.TooLarge);
            }

            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }

    /// <summary>
    /// The failure an unsuccessful answer stands for. A 401 is a sign-in the Assistant lacks (and says whether the server pointed to its sign-in
    /// information), a 403 a refusal, anything else an HTTP error. A body is not read here: a JSON-RPC error in it is the caller's to look for first.
    /// </summary>
    public static McpException FailureFor(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var offersSignIn = response.Headers.WwwAuthenticate.Any(challenge => challenge.Parameter?.Contains("resource_metadata", StringComparison.OrdinalIgnoreCase) == true);
            return new McpException(McpFailure.AuthRequired, httpStatus: status) { OffersSignIn = offersSignIn };
        }

        return response.StatusCode == HttpStatusCode.Forbidden
            ? new McpException(McpFailure.Forbidden, httpStatus: status)
            : new McpException(McpFailure.HttpError, httpStatus: status);
    }

    /// <summary>The headers of a <see cref="HttpContent"/> carrying JSON.</summary>
    public static ByteArrayContent JsonContent(byte[] json)
    {
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }
}
