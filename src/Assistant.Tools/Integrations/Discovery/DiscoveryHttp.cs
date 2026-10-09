using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Assistant.Tools.Integrations;

/// <summary>Why a request to a place that lists integrations did not give an answer.</summary>
public enum DiscoveryFailure
{
    /// <summary>The address is not one the finder may fetch (not <c>https</c>, not one of the fixed hosts, or it has credentials or a fragment).</summary>
    NotAllowed = 0,

    /// <summary>It took too long.</summary>
    TimedOut = 1,

    /// <summary>The network failed.</summary>
    Network = 2,

    /// <summary>The host refused because too many requests were made.</summary>
    RateLimited = 3,

    /// <summary>The host answered with an error.</summary>
    ServerError = 4,

    /// <summary>The answer was not what was expected.</summary>
    Malformed = 5,
}

/// <summary>A request to a place that lists integrations failed. It says why with a code; it never carries the host's words.</summary>
public sealed class DiscoveryException(DiscoveryFailure failure) : Exception("Looking for an integration failed: " + failure)
{
    /// <summary>Why.</summary>
    public DiscoveryFailure Failure { get; } = failure;
}

/// <summary>What a place answered.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The body as text, cut at the limit asked for.</param>
internal sealed record DiscoveryResponse(int Status, string Body)
{
    /// <summary>Whether the answer is a success.</summary>
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>Fetches a page for the Integration Finder.</summary>
internal interface IDiscoveryHttp
{
    /// <summary>
    /// A GET of <paramref name="uri"/>. A 404 is an answer (not an error) so that a lookup by a guessed name can say there is none.
    /// </summary>
    /// <exception cref="DiscoveryException">The address is not allowed, the host could not be reached, was too slow, refused, or answered with an error other than 404.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DiscoveryResponse> GetAsync(Uri uri, string accept, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// The Integration Finder's only way onto the network (PROJECT_SPEC §3.4, §4.8, step 106). It fetches nothing but JSON and text from a fixed list of hosts (the official MCP
/// registry, the GitHub API, the npm registry and PyPI), over <c>https</c>, with GET only. It follows no redirect, keeps no cookie, sends no credential
/// and no header that names the user (the User-Agent names the Assistant and nothing else), reads at most a set number of bytes and never
/// downloads, saves or runs anything. An address outside the list is refused before anything is sent.
/// </summary>
internal sealed class DiscoveryHttp : IDiscoveryHttp, IDisposable
{
    /// <summary>The hosts the finder may fetch from.</summary>
    public static IReadOnlySet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "registry.modelcontextprotocol.io",
        "api.github.com",
        "registry.npmjs.org",
        "pypi.org",
    };

    private const string UserAgent = "Assistant-IntegrationFinder/1.0";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    /// <summary>Creates the client the app uses: no redirects, no cookies, no proxy credentials.</summary>
    public DiscoveryHttp()
        : this(
            new HttpClient(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            ownsClient: true)
    {
    }

    /// <summary>Creates it over a client a test controls. The host list, <c>https</c> and the byte limit still apply.</summary>
    public DiscoveryHttp(HttpClient client, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownsClient = ownsClient;
    }

    /// <inheritdoc/>
    public async Task<DiscoveryResponse> GetAsync(Uri uri, string accept, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsAllowed(uri))
        {
            throw new DiscoveryException(DiscoveryFailure.NotAllowed);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 403 or 429 && IsRateLimited(response))
            {
                throw new DiscoveryException(DiscoveryFailure.RateLimited);
            }

            if (status == 404)
            {
                return new DiscoveryResponse(404, string.Empty);
            }

            if (status is >= 300)
            {
                // A redirect is not followed, and an error is not read.
                throw new DiscoveryException(status == 429 ? DiscoveryFailure.RateLimited : DiscoveryFailure.ServerError);
            }

            return new DiscoveryResponse(status, await ReadAsync(response, maxBytes, cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException)
        {
            throw new DiscoveryException(DiscoveryFailure.Network);
        }
        catch (IOException)
        {
            throw new DiscoveryException(DiscoveryFailure.Network);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    /// <summary>Whether <paramref name="uri"/> is one the finder may fetch: <c>https</c>, one of the fixed hosts, the usual port, no credentials, no fragment.</summary>
    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && AllowedHosts.Contains(uri.Host);

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || response.Headers.RetryAfter is not null
        || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0";

    // The body as text, at most maxBytes of it (what is past the limit is not read).
    private static async Task<string> ReadAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Math.Min(maxBytes, 16 * 1024)];
        using var collected = new MemoryStream();
        while (collected.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - collected.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            collected.Write(buffer, 0, read);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(collected.GetBuffer(), 0, (int)collected.Length);
    }
}
