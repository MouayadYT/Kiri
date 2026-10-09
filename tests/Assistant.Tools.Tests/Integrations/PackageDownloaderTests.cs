using System.Net;
using System.Security.Cryptography;
using Assistant.Tools.Integrations;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 108: the installer's only way to download: a fixed list of hosts, https, checked hash, bounded size, no stray files.</summary>
public sealed class PackageDownloaderTests : IDisposable
{
    private static readonly byte[] Payload = [.. Enumerable.Range(0, 5000).Select(index => (byte)(index % 251))];

    private readonly TempFolder _folder = new();
    private readonly FakeHandler _handler = new();

    public void Dispose() => _folder.Dispose();

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private PackageDownloader Downloader(PackageDownloadPolicy? policy = null) => new(policy ?? PackageDownloadPolicy.Standard, new HttpClient(_handler));

    private static HttpResponseMessage Ok(byte[] bytes, bool length = true)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (!length)
        {
            response.Content = new StreamContent(new MemoryStream(bytes));
        }

        return response;
    }

    private string Target => _folder.File("download.bin");

    private static ContentHash Sha256Of(byte[] bytes) => new("sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    // ---- what is saved ----

    [Fact]
    public async Task ADownloadThatHashesAsExpectedIsSavedAndReportsItsProgress()
    {
        _handler.Respond = _ => Ok(Payload);
        var reported = new List<long>();

        var file = await Downloader().DownloadAsync(
            new Uri("https://nodejs.org/dist/v24.21.0/node.zip"), Target, Sha256Of(Payload), 1_000_000, new Progress<long>(reported.Add), CancellationToken.None);

        Assert.Equal(Target, file.Path);
        Assert.Equal(Payload.Length, file.SizeBytes);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Target));
        Assert.False(File.Exists(Target + ".part"));
        await Task.Delay(50);
        Assert.NotEmpty(reported);
    }

    [Fact]
    public async Task ASha512ChecksumIsCheckedToo()
    {
        _handler.Respond = _ => Ok(Payload);

        await Downloader().DownloadAsync(
            new Uri("https://registry.npmjs.org/p/-/p-1.0.0.tgz"), Target, new ContentHash("sha512", Convert.ToHexString(SHA512.HashData(Payload)).ToLowerInvariant()), 1_000_000, null, CancellationToken.None);

        Assert.True(File.Exists(Target));
    }

    [Fact]
    public async Task ADownloadWithTheWrongHashLeavesNothingAndSaysItIsNotWhatWasReviewed()
    {
        _handler.Respond = _ => Ok(Payload);

        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://nodejs.org/x.zip"), Target, new ContentHash("sha256", new string('0', 64)), 1_000_000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.HashMismatch, failure.Failure);
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public async Task AnExistingFileIsNotReplacedByADownloadThatFails()
    {
        await File.WriteAllTextAsync(Target, "earlier");
        _handler.Respond = _ => Ok(Payload);

        await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://nodejs.org/x.zip"), Target, new ContentHash("sha256", new string('0', 64)), 1_000_000, null, CancellationToken.None));

        Assert.Equal("earlier", await File.ReadAllTextAsync(Target));
    }

    [Fact]
    public async Task ADownloadThatDeclaresMoreThanTheLimitIsRefusedBeforeItIsRead()
    {
        _handler.Respond = _ => Ok(Payload);

        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://nodejs.org/x.zip"), Target, Sha256Of(Payload), 100, null, CancellationToken.None));

        Assert.Equal(InstallFailure.DownloadFailed, failure.Failure);
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public async Task ADownloadThatDoesNotDeclareItsSizeIsStoppedAtTheLimitAsItArrives()
    {
        _handler.Respond = _ => Ok(Payload, length: false);

        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://nodejs.org/x.zip"), Target, Sha256Of(Payload), 1000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.DownloadFailed, failure.Failure);
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        _handler.Respond = _ =>
        {
            cancellation.Cancel();
            return Ok(Payload);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader().DownloadAsync(
            new Uri("https://nodejs.org/x.zip"), Target, Sha256Of(Payload), 1_000_000, null, cancellation.Token));

        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnErrorAnswerIsADownloadFailure(HttpStatusCode status)
    {
        _handler.Respond = _ => new HttpResponseMessage(status);

        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://nodejs.org/x.zip"), Target, Sha256Of(Payload), 1_000_000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.DownloadFailed, failure.Failure);
    }

    [Fact]
    public async Task ANetworkFailureIsADownloadFailureThatSaysToCheckTheConnection()
    {
        _handler.Respond = _ => throw new HttpRequestException("no network");

        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://nodejs.org/x.zip"), Target, Sha256Of(Payload), 1_000_000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.DownloadFailed, failure.Failure);
        Assert.Contains("connection", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no network", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestWithoutAChecksumIsRefused()
    {
        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://nodejs.org/x.zip"), Target, new ContentHash("sha256", "abc"), 1_000_000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.NotAllowed, failure.Failure);
        Assert.Empty(_handler.Requests);
    }

    // ---- where from ----

    [Theory]
    [InlineData("http://nodejs.org/x.zip")]
    [InlineData("https://evil.example.com/x.zip")]
    [InlineData("https://nodejs.org:8443/x.zip")]
    [InlineData("https://user:pw@nodejs.org/x.zip")]
    [InlineData("https://nodejs.org/x.zip#frag")]
    [InlineData("https://github.com/example/repo/raw/main/x.zip")]
    [InlineData("https://github.com/example/repo/archive/main.zip")]
    [InlineData("https://release-assets.githubusercontent.com/x.zip")]
    [InlineData("https://registry.npmjs.org.evil.example.com/x.tgz")]
    [InlineData("ftp://nodejs.org/x.zip")]
    [InlineData("file:///C:/x.zip")]
    [InlineData("http://127.0.0.1:5000/x.zip")]
    public async Task AnAddressOutsideTheListIsRefusedWithoutAnythingBeingSent(string address)
    {
        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri(address), Target, Sha256Of(Payload), 1_000_000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.NotAllowed, failure.Failure);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("https://nodejs.org/dist/v24.21.0/node-v24.21.0-win-x64.zip")]
    [InlineData("https://registry.npmjs.org/@doist/todoist-mcp/-/todoist-mcp-13.4.0.tgz")]
    [InlineData("https://files.pythonhosted.org/packages/aa/bb/x-1.0-py3-none-any.whl")]
    [InlineData("https://github.com/astral-sh/python-build-standalone/releases/download/20261001/cpython.tar.gz")]
    [InlineData("https://github.com/example/notes/releases/latest/download/notes.mcpb")]
    public void TheStandardPolicyAllowsTheHostsTheAssistantDownloadsFrom(string address)
    {
        Assert.True(PackageDownloadPolicy.Standard.IsAllowed(new Uri(address)));
    }

    [Fact]
    public void LoopbackIsOnlyAllowedByThePolicyThatSaysSoAndOnlyForThisPc()
    {
        var withLoopback = PackageDownloadPolicy.WithLoopback();

        Assert.False(PackageDownloadPolicy.Standard.AllowLoopback);
        Assert.True(withLoopback.AllowLoopback);
        Assert.True(withLoopback.IsAllowed(new Uri("http://127.0.0.1:5001/notes.mcpb")));
        Assert.True(withLoopback.IsAllowed(new Uri("http://localhost:5001/notes.mcpb")));
        Assert.False(withLoopback.IsAllowed(new Uri("http://example.com/notes.mcpb")));
        Assert.False(withLoopback.IsAllowed(new Uri("http://192.168.1.5/notes.mcpb")));
        Assert.False(PackageDownloadPolicy.Standard.IsAllowed(new Uri("http://127.0.0.1:5001/notes.mcpb")));
    }

    // ---- redirects ----

    [Fact]
    public async Task AGitHubReleaseThatRedirectsToGitHubsAssetHostIsFollowed()
    {
        _handler.Respond = request => request.RequestUri!.Host == "github.com"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/asset?sig=1") } }
            : Ok(Payload);

        await Downloader().DownloadAsync(
            new Uri("https://github.com/example/notes/releases/download/v1/notes.mcpb"), Target, Sha256Of(Payload), 1_000_000, null, CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.True(File.Exists(Target));
    }

    [Theory]
    [InlineData("https://evil.example.com/x.zip")]
    [InlineData("http://release-assets.githubusercontent.com/x.zip")]
    [InlineData("file:///C:/x.zip")]
    [InlineData("https://github.com/example/notes/raw/main/x.mcpb")]
    public async Task ARedirectToAPlaceTheListsDoNotNameIsRefused(string location)
    {
        _handler.Respond = request => request.RequestUri!.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } }
            : Ok(Payload);

        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://github.com/example/notes/releases/download/v1/notes.mcpb"), Target, Sha256Of(Payload), 1_000_000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.NotAllowed, failure.Failure);
        Assert.Single(_handler.Requests);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task TooManyRedirectsAreRefused()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://objects.githubusercontent.com/again") } };

        var failure = await Assert.ThrowsAsync<InstallException>(() => Downloader().DownloadAsync(
            new Uri("https://github.com/example/notes/releases/download/v1/notes.mcpb"), Target, Sha256Of(Payload), 1_000_000, null, CancellationToken.None));

        Assert.Equal(InstallFailure.NotAllowed, failure.Failure);
        Assert.Equal(4, _handler.Requests.Count);
    }

    // ---- what is sent ----

    [Fact]
    public async Task OnlyAGetIsSentWithAUserAgentThatNamesTheAssistantAndNoCredential()
    {
        _handler.Respond = _ => Ok(Payload);

        await Downloader().DownloadAsync(new Uri("https://nodejs.org/x.zip"), Target, Sha256Of(Payload), 1_000_000, null, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("Assistant-Installer/1.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task TheLoopbackPolicyDownloadsFromThisPcOverPlainHttp()
    {
        _handler.Respond = _ => Ok(Payload);

        await Downloader(PackageDownloadPolicy.WithLoopback()).DownloadAsync(
            new Uri("http://127.0.0.1:5002/notes.mcpb"), Target, Sha256Of(Payload), 1_000_000, null, CancellationToken.None);

        Assert.True(File.Exists(Target));
    }
}
