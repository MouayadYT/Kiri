using System.Net;
using System.Text;
using Assistant.Tools.Integrations;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 106: the finder's only way onto the network fetches a few fixed hosts, over https, and nothing else.</summary>
public sealed class DiscoveryHttpTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (DiscoveryHttp Http, Handler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        return (new DiscoveryHttp(new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("https://registry.modelcontextprotocol.io/v0/servers?search=x", true)]
    [InlineData("https://api.github.com/search/repositories?q=x", true)]
    [InlineData("https://registry.npmjs.org/-/v1/search?text=x", true)]
    [InlineData("https://pypi.org/pypi/x/json", true)]
    [InlineData("http://api.github.com/search", false)]
    [InlineData("https://evil.example/search", false)]
    [InlineData("https://api.github.com.evil.example/", false)]
    [InlineData("https://user:pass@api.github.com/x", false)]
    [InlineData("https://api.github.com:8443/x", false)]
    [InlineData("https://api.github.com/x#fragment", false)]
    [InlineData("https://raw.githubusercontent.com/o/r/HEAD/README.md", false)]
    [InlineData("ftp://api.github.com/x", false)]
    public void OnlyTheFixedHostsOverHttpsAreAllowed(string address, bool allowed)
    {
        Assert.Equal(allowed, DiscoveryHttp.IsAllowed(new Uri(address)));
    }

    [Fact]
    public async Task AnAddressThatIsNotAllowedIsRefusedBeforeAnythingIsSent()
    {
        var (http, handler) = Make(_ => Ok("{}"));

        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => http.GetAsync(new Uri("https://evil.example/x"), "application/json", 1000, default));

        Assert.Equal(DiscoveryFailure.NotAllowed, failure.Failure);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task ARequestIsAGetThatNamesTheAssistantAndNobodyElse()
    {
        var (http, handler) = Make(_ => Ok("{}"));

        await http.GetAsync(new Uri("https://api.github.com/search/repositories?q=x"), "application/vnd.github+json", 1000, default);

        var request = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("Assistant-IntegrationFinder/1.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Referer"));
        Assert.Null(request.Content);
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task ABodyIsReadOnlyUpToTheLimit()
    {
        var (http, _) = Make(_ => Ok(new string('x', 10_000)));

        var response = await http.GetAsync(new Uri("https://pypi.org/pypi/x/json"), "application/json", 1000, default);

        Assert.Equal(1000, response.Body.Length);
    }

    [Fact]
    public async Task ANotFoundIsAnAnswerAndNotAnError()
    {
        var (http, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var response = await http.GetAsync(new Uri("https://pypi.org/pypi/x/json"), "application/json", 1000, default);

        Assert.Equal(404, response.Status);
        Assert.False(response.IsSuccess);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ARedirectIsNotFollowedAndAServerErrorIsAFailure(HttpStatusCode status)
    {
        var (http, handler) = Make(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://evil.example/");
            return response;
        });

        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => http.GetAsync(new Uri("https://pypi.org/pypi/x/json"), "application/json", 1000, default));

        Assert.Equal(DiscoveryFailure.ServerError, failure.Failure);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task TheRealClientDoesNotFollowRedirectsOrKeepCookies()
    {
        // The handler the app builds, not the test's: its settings are what keep a page from sending the finder somewhere else.
        using var http = new DiscoveryHttp();
        var field = typeof(DiscoveryHttp).GetField("_client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var client = (HttpClient)field.GetValue(http)!;
        var handlerField = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var handler = Assert.IsType<SocketsHttpHandler>(handlerField.GetValue(client));

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ARateLimitIsToldFromAnError()
    {
        var (http, _) = Make(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return response;
        });

        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => http.GetAsync(new Uri("https://api.github.com/search/repositories?q=x"), "application/json", 1000, default));

        Assert.Equal(DiscoveryFailure.RateLimited, failure.Failure);
    }

    [Fact]
    public async Task TooManyRequestsIsARateLimit()
    {
        var (http, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => http.GetAsync(new Uri("https://api.github.com/x"), "application/json", 1000, default));
        Assert.Equal(DiscoveryFailure.RateLimited, failure.Failure);
    }

    [Fact]
    public async Task ANetworkFailureIsAFailureWithACodeAndNoWords()
    {
        var handler = new Handler(_ => throw new HttpRequestException("secret host details"));
        var http = new DiscoveryHttp(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => http.GetAsync(new Uri("https://pypi.org/pypi/x/json"), "application/json", 1000, default));

        Assert.Equal(DiscoveryFailure.Network, failure.Failure);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingEndsARequest()
    {
        var handler = new Handler(_ => throw new OperationCanceledException());
        var http = new DiscoveryHttp(new HttpClient(handler));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync(new Uri("https://pypi.org/pypi/x/json"), "application/json", 1000, source.Token));
    }
}

/// <summary>Step 106: web text is cleaned before anything is kept, shown or given to a model.</summary>
public sealed class CandidateTextTests
{
    [Theory]
    [InlineData("  two   words\tand\nlines  ", 100, "two words and lines")]
    [InlineData("", 10, null)]
    [InlineData("   ", 10, null)]
    [InlineData(null, 10, null)]
    [InlineData("abcdefghij", 5, "abcde…")]
    public void ALineIsOneLineOfPrintableCharactersCutToALength(string? text, int max, string? expected)
    {
        Assert.Equal(expected, CandidateText.Line(text, max));
    }

    [Fact]
    public void ALineNeverCutsASurrogatePairInHalf()
    {
        var line = CandidateText.Line("ab\U0001F600\U0001F600", 3)!;
        Assert.DoesNotContain(line, char.IsSurrogate);
        Assert.StartsWith("ab", line, StringComparison.Ordinal);
    }

    [Fact]
    public void InvisibleSeparatorsAreBlanks()
    {
        var text = "a" + (char)0x2028 + "b" + (char)0x200B + "c" + (char)0xFEFF + "d";
        Assert.Equal("a b c d", CandidateText.Line(text, 100));
    }

    [Theory]
    [InlineData("https://github.com/o/r", "https://github.com/o/r")]
    [InlineData("http://github.com/o/r", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://user:pw@github.com/o/r", null)]
    [InlineData("https://github.com/o/r#frag", null)]
    [InlineData("https://github.com/o r", null)]
    [InlineData("https://localhost/x", null)]
    [InlineData("file:///C:/x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyAnHttpsAddressWithoutCredentialsIsKept(string? address, string? expected)
    {
        Assert.Equal(expected, CandidateText.Https(address));
    }

    [Theory]
    [InlineData("https://github.com/Doist/todoist-mcp", "Doist", "todoist-mcp")]
    [InlineData("git+https://github.com/Doist/todoist-mcp.git", "Doist", "todoist-mcp")]
    [InlineData("git://github.com/Doist/todoist-mcp.git", "Doist", "todoist-mcp")]
    [InlineData("git@github.com:Doist/todoist-mcp.git", "Doist", "todoist-mcp")]
    [InlineData("https://github.com/Doist/todoist-mcp/tree/main/packages/x", "Doist", "todoist-mcp")]
    public void AGitHubRepositoryIsReadFromAnyFormOfItsAddress(string address, string owner, string repo)
    {
        Assert.True(CandidateText.TryGitHubRepository(address, out var foundOwner, out var foundRepo));
        Assert.Equal(owner, foundOwner);
        Assert.Equal(repo, foundRepo);
        Assert.Equal($"https://github.com/{owner}/{repo}", CandidateText.GitHubRepositoryUrl(address));
    }

    [Theory]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("https://github.com/o")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/o/..")]
    [InlineData("https://evilgithub.com/o/r")]
    [InlineData("")]
    public void AnythingElseIsNotAGitHubRepository(string address)
    {
        Assert.False(CandidateText.TryGitHubRepository(address, out _, out _));
        Assert.Null(CandidateText.GitHubRepositoryUrl(address));
    }

    [Theory]
    [InlineData("The official Todoist MCP server", true)]
    [InlineData("Official server", true)]
    [InlineData("An unofficial Todoist server", false)]
    [InlineData("officially supported", false)]
    [InlineData("A Todoist server", false)]
    [InlineData(null, false)]
    public void OfficialIsAClaimOnlyWhenTheWordIsThere(string? text, bool expected)
    {
        Assert.Equal(expected, CandidateText.ClaimsOfficial(text));
    }

    [Theory]
    [InlineData("Todoist", "todoist")]
    [InlineData("To-Do!", "todo")]
    [InlineData("x", null)]
    [InlineData("日本語", null)]
    public void ASearchWordIsLettersAndDigitsOnly(string text, string? expected)
    {
        Assert.Equal(expected, CandidateText.SearchWord(text));
    }
}
