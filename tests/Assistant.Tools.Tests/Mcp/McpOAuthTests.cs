using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Tools.FakeMcpServer;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>
/// The OAuth sign-in to a connected app (PROJECT_SPEC §4.8): finding the authorization server, registering the Assistant, PKCE, the return to a port on this PC, the exchange
/// for tokens, and keeping and renewing them. The servers are a fake handler; the port on this PC is real.
/// </summary>
public sealed class McpOAuthTests
{
    private static readonly Uri Mcp = new("https://mcp.example.com/mcp");

    [Fact]
    public async Task SignInRegistersTheAssistantAndTradesTheCodeForTokensWithPkce()
    {
        var server = new FakeOAuthServer();
        var browser = new ReturningBrowser();
        using var client = new McpOAuthClient(browser, handler: server);

        var signIn = await client.SignInAsync(Mcp, "Assistant", scope: null, timeout: null, CancellationToken.None);

        Assert.Equal("client-1", signIn.ClientId);
        Assert.Equal("AT1", signIn.Tokens.AccessToken);
        Assert.Equal("RT1", signIn.Tokens.RefreshToken);
        Assert.Equal(new Uri("https://auth.example.com/token"), signIn.TokenEndpoint);

        // The program registered itself as a public client that comes back to a port on this PC.
        using var registration = JsonDocument.Parse(server.RegistrationBody!);
        Assert.Equal("none", registration.RootElement.GetProperty("token_endpoint_auth_method").GetString());
        var redirect = registration.RootElement.GetProperty("redirect_uris")[0].GetString()!;
        Assert.StartsWith("http://127.0.0.1:", redirect, StringComparison.Ordinal);

        // The page the user was sent to asks for a code with PKCE, and names the resource.
        var query = browser.OpenedQuery!;
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(redirect, query["redirect_uri"]);
        Assert.Equal(Mcp.AbsoluteUri, query["resource"]);

        // The code was traded with the verifier whose hash is the challenge that went out.
        var token = server.TokenRequests.Single();
        Assert.Equal("authorization_code", token["grant_type"]);
        Assert.Equal("the-code", token["code"]);
        Assert.Equal(redirect, token["redirect_uri"]);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(token["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
    }

    [Theory]
    [InlineData("https://mcp.example.com/v2", "https://mcp.example.com", "https://mcp.example.com")]
    [InlineData("https://mcp.example.com/v2", "https://mcp.example.com/", "https://mcp.example.com/")]
    [InlineData("https://mcp.example.com/v2/mcp", "https://mcp.example.com/v2", "https://mcp.example.com/v2")]
    [InlineData("https://mcp.example.com/v2", "https://mcp.example.com/v2", "https://mcp.example.com/v2")]
    public void TheResourceAServerSaysItIsIsAskedForAsItWroteIt(string endpoint, string advertised, string expected) =>
        Assert.Equal(expected, McpOAuthClient.AdvertisedResource(new Uri(endpoint), advertised)?.OriginalString);

    [Theory]
    [InlineData("https://mcp.example.com/v2", "https://other.example.com")]
    [InlineData("https://mcp.example.com/v2", "https://mcp.example.com:8443")]
    [InlineData("https://mcp.example.com/v2", "http://mcp.example.com")]
    [InlineData("https://mcp.example.com/v2", "https://mcp.example.com/v20")]
    [InlineData("https://mcp.example.com/v2", "https://mcp.example.com/other")]
    [InlineData("https://mcp.example.com/v2", "https://mcp.example.com/?tenant=1")]
    [InlineData("https://mcp.example.com/v2", "not an address")]
    [InlineData("https://mcp.example.com/v2", "")]
    public void AResourceThatCannotBeTheServerIsNotAskedFor(string endpoint, string advertised) =>
        Assert.Null(McpOAuthClient.AdvertisedResource(new Uri(endpoint), advertised));

    [Fact]
    public async Task ThePageOfASignInIsToldBeforeTheBrowserIsOpened_SoItCanBeCopiedIntoAnotherBrowser()
    {
        var browser = new ReturningBrowser();
        using var client = new McpOAuthClient(browser, handler: new FakeOAuthServer());
        Uri? told = null;
        var openedWhenTold = true;

        await client.SignInAsync(
            Mcp, "Assistant", scope: null, timeout: null, CancellationToken.None,
            new OAuthSignInOptions { AddressReady = address => { told = address; openedWhenTold = browser.OpenedQuery is not null; } });

        Assert.NotNull(told);
        Assert.False(openedWhenTold, "The address must be known before the browser opens.");
        Assert.StartsWith("https://auth.example.com/authorize?", told!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A", told.AbsoluteUri, StringComparison.Ordinal);

        // What it holds is the service's own page asking for a code with PKCE: the verifier that trades the code stays here, so the link signs nobody in.
        Assert.Contains("code_challenge=", told.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", told.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivateWindowIsAskedForWhenTheUserChoseOne_AndTheOrdinaryOneThenStaysClosed()
    {
        var browser = new ReturningBrowser();
        using var client = new McpOAuthClient(browser, handler: new FakeOAuthServer());

        var signIn = await client.SignInAsync(Mcp, "Assistant", scope: null, timeout: null, CancellationToken.None, new OAuthSignInOptions { PrivateWindow = true });

        Assert.Equal("AT1", signIn.Tokens.AccessToken);
        Assert.True(browser.OpenedPrivately);
    }

    [Fact]
    public async Task APrivateWindowThatCannotBeOpenedIsSaid_AndNothingIsOpenedInItsPlace()
    {
        var browser = new ReturningBrowser { PrivateOpens = false };
        using var client = new McpOAuthClient(browser, handler: new FakeOAuthServer());

        var failure = await Assert.ThrowsAsync<OAuthException>(() =>
            client.SignInAsync(Mcp, "Assistant", scope: null, timeout: null, CancellationToken.None, new OAuthSignInOptions { PrivateWindow = true }));

        Assert.Equal(OAuthFailure.PrivateBrowserFailed, failure.Failure);
        Assert.Null(browser.OpenedQuery);
    }

    [Fact]
    public async Task AFixedClientThatAsksForAnAccountChoiceSendsItInThePage_AndOneThatDoesNotSendsNone()
    {
        foreach (var prompt in new string?[] { "select_account", null })
        {
            var browser = new ReturningBrowser();
            using var client = new McpOAuthClient(browser, handler: new FakeOAuthServer());
            var fixedClient = new OAuthFixedClient(
                new Uri("https://auth.example.com/authorize"), new Uri("https://auth.example.com/token"), "the-client", "Tasks.ReadWrite", "localhost", prompt);

            await client.SignInAsync(fixedClient, timeout: null, CancellationToken.None);

            if (prompt is null)
            {
                Assert.False(browser.OpenedQuery!.ContainsKey("prompt"));
            }
            else
            {
                Assert.Equal(prompt, browser.OpenedQuery!["prompt"]);
            }
        }
    }

    [Fact]
    public async Task AFixedClientIsReturnedToThePortOfThisPcWithNoPath_AsAnAppRegisteredAsLocalhostMustBe()
    {
        // Microsoft's app is registered as http://localhost: the port is ignored when matching, the path is not, so /callback was refused ("redirect_uri is not valid").
        var browser = new ReturningBrowser();
        var server = new FakeOAuthServer();
        using var client = new McpOAuthClient(browser, handler: server);
        var fixedClient = new OAuthFixedClient(new Uri("https://auth.example.com/authorize"), new Uri("https://auth.example.com/token"), "the-client", "Tasks.ReadWrite", "localhost");

        var signIn = await client.SignInAsync(fixedClient, timeout: null, CancellationToken.None);

        var sent = new Uri(browser.OpenedQuery!["redirect_uri"]);
        Assert.Equal("localhost", sent.Host);
        Assert.Equal("/", sent.AbsolutePath);
        Assert.DoesNotContain("/callback", browser.OpenedQuery["redirect_uri"], StringComparison.Ordinal);
        Assert.False(browser.OpenedQuery["redirect_uri"].EndsWith('/'));
        Assert.Equal(browser.OpenedQuery["redirect_uri"], server.TokenRequests.Single()["redirect_uri"]);
        Assert.Equal("AT1", signIn.Tokens.AccessToken);
    }

    [Fact]
    public void AReturnToTheRootIsTheCallbackOnlyWhenItCarriesAnAnswer()
    {
        Assert.Equal("a", LoopbackAuthorizationListener.ParseCallback("GET /?code=a&state=b HTTP/1.1", rootPath: true)!["code"]);
        Assert.Equal("access_denied", LoopbackAuthorizationListener.ParseCallback("GET /?error=access_denied HTTP/1.1", rootPath: true)!["error"]);
        Assert.Null(LoopbackAuthorizationListener.ParseCallback("GET / HTTP/1.1", rootPath: true));
        Assert.Null(LoopbackAuthorizationListener.ParseCallback("GET /favicon.ico HTTP/1.1", rootPath: true));
        Assert.Null(LoopbackAuthorizationListener.ParseCallback("GET /?code=a HTTP/1.1"));
    }

    [Fact]
    public async Task AReturnWithAnotherStateIsNotAccepted()
    {
        var server = new FakeOAuthServer();
        using var client = new McpOAuthClient(new ReturningBrowser() { State = "not-mine" }, handler: server);

        var failure = await Assert.ThrowsAsync<OAuthException>(() => client.SignInAsync(Mcp, "Assistant", null, null, CancellationToken.None));

        Assert.Equal(OAuthFailure.Denied, failure.Failure);
        Assert.Empty(server.TokenRequests);
    }

    [Fact]
    public async Task ADeniedSignInGivesNoTokens()
    {
        var server = new FakeOAuthServer();
        using var client = new McpOAuthClient(new ReturningBrowser() { Error = "access_denied" }, handler: server);

        var failure = await Assert.ThrowsAsync<OAuthException>(() => client.SignInAsync(Mcp, "Assistant", null, null, CancellationToken.None));

        Assert.Equal(OAuthFailure.Denied, failure.Failure);
        Assert.Empty(server.TokenRequests);
    }

    [Fact]
    public async Task ServerThatCannotRegisterTheAssistantIsNotSignedInTo()
    {
        var server = new FakeOAuthServer { Registration = false };
        var browser = new ReturningBrowser();
        using var client = new McpOAuthClient(browser, handler: server);

        var failure = await Assert.ThrowsAsync<OAuthException>(() => client.SignInAsync(Mcp, "Assistant", null, null, CancellationToken.None));

        Assert.Equal(OAuthFailure.NoRegistration, failure.Failure);
        Assert.Null(browser.OpenedQuery);
    }

    [Fact]
    public async Task ServerWithNoAuthorizationServerIsNotSupported()
    {
        var server = new FakeOAuthServer { Metadata = false };
        using var client = new McpOAuthClient(new ReturningBrowser(), handler: server);

        var failure = await Assert.ThrowsAsync<OAuthException>(() => client.SignInAsync(Mcp, "Assistant", null, null, CancellationToken.None));

        Assert.Equal(OAuthFailure.NotSupported, failure.Failure);
    }

    [Fact]
    public async Task NobodyComingBackTimesTheSignInOut()
    {
        var server = new FakeOAuthServer();
        using var client = new McpOAuthClient(new ReturningBrowser() { Silent = true }, handler: server);

        var failure = await Assert.ThrowsAsync<OAuthException>(() => client.SignInAsync(Mcp, "Assistant", null, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.Equal(OAuthFailure.TimedOut, failure.Failure);
    }

    [Fact]
    public async Task ABrowserThatDoesNotOpenIsSaid()
    {
        var server = new FakeOAuthServer();
        using var client = new McpOAuthClient(new ReturningBrowser() { Opens = false }, handler: server);

        var failure = await Assert.ThrowsAsync<OAuthException>(() => client.SignInAsync(Mcp, "Assistant", null, null, CancellationToken.None));

        Assert.Equal(OAuthFailure.BrowserFailed, failure.Failure);
    }

    [Fact]
    public async Task AnAuthorizationServerAtAnotherSchemeThanHttpsIsNotFollowed()
    {
        var server = new FakeOAuthServer { AuthorizationServer = "http://auth.example.com" };
        using var client = new McpOAuthClient(new ReturningBrowser(), handler: server);

        var failure = await Assert.ThrowsAsync<OAuthException>(() => client.SignInAsync(Mcp, "Assistant", null, null, CancellationToken.None));

        // The server's own address stands in for it, and it has no metadata of its own.
        Assert.Equal(OAuthFailure.NotSupported, failure.Failure);
    }

    [Fact]
    public async Task ARefreshTokenGetsANewAccessTokenAndKeepsTheOldRefreshTokenWhenNoneIsGiven()
    {
        var server = new FakeOAuthServer();
        using var client = new McpOAuthClient(new ReturningBrowser(), handler: server);

        var tokens = await client.RefreshAsync(new Uri("https://auth.example.com/token"), "client-1", "RT1", Mcp, CancellationToken.None);

        Assert.Equal("AT2", tokens.AccessToken);
        Assert.Equal("RT1", tokens.RefreshToken);
        Assert.Equal("refresh_token", server.TokenRequests.Single()["grant_type"]);
    }

    [Fact]
    public void TheCallbackIsReadFromTheRequestLineAndNothingElseIsTakenForIt()
    {
        var query = LoopbackAuthorizationListener.ParseCallback("GET /callback?code=a%20b&state=xyz HTTP/1.1");

        Assert.NotNull(query);
        Assert.Equal("a b", query["code"]);
        Assert.Equal("xyz", query["state"]);
        Assert.Null(LoopbackAuthorizationListener.ParseCallback("GET /favicon.ico HTTP/1.1"));
        Assert.Null(LoopbackAuthorizationListener.ParseCallback("POST /callback?code=a HTTP/1.1"));
        Assert.Null(LoopbackAuthorizationListener.ParseCallback(null));
    }

    [Fact]
    public async Task ASignInIsKeptInPiecesAndGivenBackUntilItRunsOut()
    {
        var secrets = new FakeSecretStore();
        var clock = new ManualTimeProvider();
        var server = new FakeOAuthServer();
        using var client = new McpOAuthClient(new ReturningBrowser(), clock, server);
        var sessions = new McpOAuthSessions(secrets, client, clock);
        var longToken = new string('a', 2500);
        var signIn = new OAuthSignIn(
            new Uri("https://auth.example.com/token"), "client-1", Mcp, new OAuthTokens(longToken, "RT1", clock.GetUtcNow().AddHours(1), null));

        var bindings = await sessions.SaveAsync("todoist", signIn, CancellationToken.None);

        Assert.True(bindings.Count > 1);
        Assert.All(secrets.Secrets.Values, piece => Assert.True(piece.Length <= 900));
        Assert.Equal(bindings.Select(binding => binding.SecretName), secrets.Secrets.Keys.Order());
        var integration = Sample.Remote("todoist", "Todoist");
        Assert.Equal(longToken, await sessions.GetAccessTokenAsync(integration));

        // Within two minutes of running out, a new token is got with the refresh token and kept.
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal("AT2", await sessions.GetAccessTokenAsync(integration));
        Assert.Equal("AT2", await sessions.GetAccessTokenAsync(integration));
        Assert.Single(server.TokenRequests);
    }

    [Fact]
    public async Task ASignInThatIsGoneOrCannotBeRenewedIsASignInNeeded()
    {
        var secrets = new FakeSecretStore();
        var clock = new ManualTimeProvider();
        var server = new FakeOAuthServer { RefreshWorks = false };
        using var client = new McpOAuthClient(new ReturningBrowser(), clock, server);
        var sessions = new McpOAuthSessions(secrets, client, clock);
        var integration = Sample.Remote("todoist", "Todoist");

        var none = await Assert.ThrowsAsync<McpException>(() => sessions.GetAccessTokenAsync(integration));
        Assert.Equal(McpFailure.AuthRequired, none.Failure);

        await sessions.SaveAsync(
            "todoist",
            new OAuthSignIn(new Uri("https://auth.example.com/token"), "client-1", Mcp, new OAuthTokens("AT1", "RT1", clock.GetUtcNow().AddMinutes(1), null)),
            CancellationToken.None);
        var refused = await Assert.ThrowsAsync<McpException>(() => sessions.GetAccessTokenAsync(integration));
        Assert.Equal(McpFailure.AuthRequired, refused.Failure);

        await sessions.DeleteAsync("todoist", CancellationToken.None);
        Assert.Empty(secrets.Secrets);
    }

    [Fact]
    public async Task TheFactorySendsTheOAuthAccessTokenAsABearerToken()
    {
        var secrets = new FakeSecretStore();
        var clock = new ManualTimeProvider();
        var server = new FakeOAuthServer();
        using var client = new McpOAuthClient(new ReturningBrowser(), clock, server);
        var sessions = new McpOAuthSessions(secrets, client, clock);
        var bindings = await sessions.SaveAsync(
            "todoist",
            new OAuthSignIn(new Uri("https://auth.example.com/token"), "client-1", Mcp, new OAuthTokens("AT-bearer", "RT1", clock.GetUtcNow().AddHours(1), null)),
            CancellationToken.None);
        var integration = Sample.Remote("todoist", "Todoist") with
        {
            Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.OAuth, State = IntegrationAuthState.Ready, Secrets = bindings },
        };
        var handler = new FakeMcpHttpHandler(new FakeMcpServerCore(new FakeMcpOptions { Era = FakeMcpEra.Modern }), new FakeHttpOptions { BearerToken = "AT-bearer" });
        var factory = new McpClientFactory(secrets, new McpClientOptions(), () => handler, sessions);

        await using var mcp = factory.Create(integration);
        await mcp.ConnectAsync(CancellationToken.None);

        Assert.All(handler.Requests, request => Assert.Equal("Bearer AT-bearer", request.Header("Authorization")));
    }
}
