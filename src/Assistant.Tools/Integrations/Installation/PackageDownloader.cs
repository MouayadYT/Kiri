using System.Net;
using System.Security.Cryptography;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Where the installer may download from (PROJECT_SPEC §3.4, §4.8, step 108): a fixed list of hosts, <c>https</c> only, and only the redirects that
/// the hosts that need them make. It is set by the code that builds the installer: nothing a candidate, a registry or a download says can add a host.
/// </summary>
public sealed class PackageDownloadPolicy
{
    private static readonly string[] StandardHosts =
    [
        "registry.npmjs.org", "files.pythonhosted.org", "nodejs.org", "github.com",
    ];

    // Where GitHub sends a release asset to be downloaded from.
    private static readonly string[] StandardRedirectHosts =
    [
        "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com",
    ];

    private readonly HashSet<string> _hosts;
    private readonly HashSet<string> _redirectHosts;

    private PackageDownloadPolicy(IEnumerable<string> hosts, IEnumerable<string> redirectHosts, bool allowLoopback)
    {
        _hosts = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
        _redirectHosts = new HashSet<string>(redirectHosts, StringComparer.OrdinalIgnoreCase);
        AllowLoopback = allowLoopback;
    }

    /// <summary>The policy of the app: the npm registry, PyPI's file host, nodejs.org and GitHub's release downloads, over <c>https</c>.</summary>
    public static PackageDownloadPolicy Standard { get; } = new(StandardHosts, StandardRedirectHosts, allowLoopback: false);

    /// <summary>
    /// The standard policy, and also this PC's own loopback address over plain <c>http</c>, for a package served by a program on this PC (a test, or the
    /// sample integration of <c>demo integration</c>). Nothing leaves the PC. The app's own installer never uses it.
    /// </summary>
    public static PackageDownloadPolicy WithLoopback() => new(StandardHosts, StandardRedirectHosts, allowLoopback: true);

    /// <summary>Whether this PC's own loopback address may be downloaded from over <c>http</c>.</summary>
    public bool AllowLoopback { get; }

    /// <summary>The most redirects followed for one download.</summary>
    public int MaxRedirects => 3;

    /// <summary>Whether <paramref name="uri"/> may be downloaded from: it is an address on this PC when loopback is allowed, otherwise <c>https</c> on a listed host with the usual port, no credentials and no fragment.</summary>
    public bool IsAllowed(Uri uri) => IsAllowed(uri, redirect: false);

    /// <summary>Whether <paramref name="uri"/> may be downloaded from, or followed to when <paramref name="redirect"/>.</summary>
    public bool IsAllowed(Uri uri, bool redirect)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (AllowLoopback && IsLoopback(uri))
        {
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
        {
            return false;
        }

        if (redirect && _redirectHosts.Contains(uri.Host))
        {
            return true;
        }

        if (!_hosts.Contains(uri.Host))
        {
            return false;
        }

        // GitHub is only a place to download a release's files from, not to fetch anything else.
        return !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/releases/latest/download/", StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="uri"/> is on this PC: <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c>.</summary>
    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A file that was downloaded and checked.</summary>
/// <param name="Path">Where it was saved.</param>
/// <param name="SizeBytes">How large it is.</param>
public sealed record DownloadedFile(string Path, long SizeBytes);

/// <summary>Downloads a file, checks it against the hash it must have and saves it (PROJECT_SPEC §4.8, step 108).</summary>
public interface IPackageDownloader
{
    /// <summary>
    /// Downloads <paramref name="uri"/> to <paramref name="destination"/>, and saves it there only if it hashes to <paramref name="expected"/> and is no
    /// larger than <paramref name="maxBytes"/>. Anything else leaves nothing at the destination.
    /// </summary>
    /// <param name="uri">Where from; the policy decides whether it may be.</param>
    /// <param name="destination">The file to write.</param>
    /// <param name="expected">What it must hash to.</param>
    /// <param name="maxBytes">The most it may be.</param>
    /// <param name="progress">Told how many bytes have arrived.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="InstallException">The address is not allowed, the download failed, was too large, or hashes to something else.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DownloadedFile> DownloadAsync(
        Uri uri, string destination, ContentHash expected, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// The installer's only way to download (PROJECT_SPEC §3.4, §4.8, step 108). GET only, <c>https</c> and a fixed list of hosts (<see cref="PackageDownloadPolicy"/>), no
/// cookie and no credential, a User-Agent that names the Assistant and nothing else, redirects followed by hand and only to the hosts the policy lists, a
/// size limit that is enforced as the bytes arrive, and a hash that is checked before the file is put where it is used. A file that fails any of it
/// is deleted.
/// </summary>
public sealed class PackageDownloader : IPackageDownloader, IDisposable
{
    private const string UserAgent = "Assistant-Installer/1.0";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly PackageDownloadPolicy _policy;

    /// <summary>Creates the downloader the app uses: no automatic redirects, no cookies.</summary>
    public PackageDownloader(PackageDownloadPolicy? policy = null)
        : this(
            policy ?? PackageDownloadPolicy.Standard,
            new HttpClient(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            ownsClient: true)
    {
    }

    /// <summary>Creates it over a client a test controls. The policy, the size limit and the hash still apply.</summary>
    public PackageDownloader(PackageDownloadPolicy policy, HttpClient client, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(client);
        _policy = policy;
        _client = client;
        _ownsClient = ownsClient;
    }

    /// <inheritdoc/>
    public async Task<DownloadedFile> DownloadAsync(
        Uri uri, string destination, ContentHash expected, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.IsValid)
        {
            throw new InstallException(InstallFailure.NotAllowed, "There is no checksum to check the download against.");
        }

        if (!_policy.IsAllowed(uri))
        {
            throw new InstallException(InstallFailure.NotAllowed, "That address is not one I download from.");
        }

        var part = destination + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                throw new InstallException(InstallFailure.DownloadFailed, "The download is larger than I allow.");
            }

            long total;
            string actual;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var hasher = expected.Algorithm == "sha512" ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512) : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > maxBytes)
                    {
                        throw new InstallException(InstallFailure.DownloadFailed, "The download is larger than I allow.");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total);
                }

                actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected.Hex)))
            {
                throw new InstallException(InstallFailure.HashMismatch, "What I downloaded is not what was reviewed, so I deleted it.");
            }

            File.Move(part, destination, overwrite: true);
            return new DownloadedFile(destination, total);
        }
        catch (HttpRequestException exception)
        {
            throw new InstallException(InstallFailure.DownloadFailed, "I could not download it. Check your internet connection.", exception);
        }
        catch (IOException exception) when (exception is not FileNotFoundException)
        {
            throw new InstallException(InstallFailure.DownloadFailed, "I could not save the download. Check that the disk has room.", exception);
        }
        finally
        {
            TryDelete(part);
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

    // The request, with redirects followed by hand: each hop must be a place the policy lets a redirect go to.
    private async Task<HttpResponseMessage> SendAsync(Uri start, CancellationToken cancellationToken)
    {
        var uri = start;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (status is 301 or 302 or 303 or 307 or 308 && location is not null)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (hop >= _policy.MaxRedirects || !_policy.IsAllowed(next, redirect: true))
                {
                    throw new InstallException(InstallFailure.NotAllowed, "The download was sent somewhere I do not download from.");
                }

                uri = next;
                continue;
            }

            throw new InstallException(
                InstallFailure.DownloadFailed,
                status == (int)HttpStatusCode.NotFound ? "It is not there to download any more." : "The download was refused or failed.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A partial file that cannot be removed is overwritten by the next download.
        }
    }
}
