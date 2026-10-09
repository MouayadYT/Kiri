using System.Net;
using System.Text;
using Assistant.Tools.Mcp.Auth;

namespace Assistant.Tools.Tests.Mcp;

// A fake of both the MCP server and its authorization server, which records what it was asked.
internal sealed class FakeOAuthServer : HttpMessageHandler
{
    public bool Registration { get; init; } = true;

    public bool Metadata { get; init; } = true;

    public bool RefreshWorks { get; init; } = true;

    public string AuthorizationServer { get; init; } = "https://auth.example.com";

    public string? RootMetadataHost { get; init; }

    /// <summary>What the server at <see cref="RootMetadataHost"/> says it is called as a resource, at its root; nothing is said when <see langword="null"/>.</summary>
    public string? RootResource { get; init; }

    public string? RegistrationBody { get; private set; }

    public List<Dictionary<string, string>> TokenRequests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var host = request.RequestUri.Host;
        if (host == "mcp.example.com" && path == "/.well-known/oauth-protected-resource/mcp")
        {
            return Json($$"""{"resource":"https://mcp.example.com/mcp","authorization_servers":["{{AuthorizationServer}}"]}""");
        }

        // A server that names itself as a resource at its root only, as Pipedream's does: nothing at the path of its address, and at the root a
        // resource that is the host without the address's path.
        if (RootResource is not null && host == RootMetadataHost && path == "/.well-known/oauth-protected-resource")
        {
            return Json($$"""{"resource":"{{RootResource}}","authorization_servers":["{{AuthorizationServer}}"]}""");
        }

        var authorizationHost = new Uri(AuthorizationServer).Host;
        if ((host == authorizationHost || host == RootMetadataHost) && path == "/.well-known/oauth-authorization-server")
        {
            return !Metadata
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(
                    "{\"authorization_endpoint\":\"" + AuthorizationServer + "/authorize\",\"token_endpoint\":\"" + AuthorizationServer + "/token\""
                    + (Registration ? ",\"registration_endpoint\":\"" + AuthorizationServer + "/register\"" : string.Empty) + "}");
        }

        if (host == authorizationHost && path == "/register" && request.Method == HttpMethod.Post)
        {
            RegistrationBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("{\"client_id\":\"client-1\"}");
        }

        if (host == authorizationHost && path == "/token" && request.Method == HttpMethod.Post)
        {
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            TokenRequests.Add(form);
            if (form["grant_type"] == "refresh_token")
            {
                return RefreshWorks ? Json("{\"access_token\":\"AT2\",\"expires_in\":3600}") : new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            return Json("{\"access_token\":\"AT1\",\"refresh_token\":\"RT1\",\"expires_in\":3600}");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

// The user's browser: opened on the page, it says yes (or no) by going to the address the Assistant gave to come back to.
internal sealed class ReturningBrowser : IOAuthBrowser
{
    public string? State { get; init; }

    public string? Error { get; init; }

    public bool Silent { get; init; }

    public bool Opens { get; init; } = true;

    public Dictionary<string, string>? OpenedQuery { get; private set; }

    /// <summary>Whether the page was opened in a private window, and not in the ordinary one.</summary>
    public bool OpenedPrivately { get; private set; }

    public bool PrivateOpens { get; init; } = true;

    public Task<bool> OpenPrivateAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (!PrivateOpens)
        {
            return Task.FromResult(false);
        }

        var opened = OpenAsync(address, cancellationToken);
        OpenedPrivately = true;
        return opened;
    }

    public Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (!Opens)
        {
            return Task.FromResult(false);
        }

        OpenedQuery = address.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        if (Silent)
        {
            return Task.FromResult(true);
        }

        var back = OpenedQuery["redirect_uri"]
            + (Error is null ? "?code=the-code" : "?error=" + Error)
            + "&state=" + Uri.EscapeDataString(State ?? OpenedQuery["state"]);
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            await http.GetAsync(back, CancellationToken.None);
        }, CancellationToken.None);
        return Task.FromResult(true);
    }
}
