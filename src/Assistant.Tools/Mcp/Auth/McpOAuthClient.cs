using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assistant.Tools.Mcp.Auth;

/// <summary>
/// Signs the Assistant in to a connected app that is reached over HTTP, the way the MCP specification describes (OAuth 2.1 with PKCE, RFC 9728 and RFC 8414 for finding
/// the authorization server, RFC 7591 for the Assistant to register itself, RFC 8252 for coming back to a port on this PC). The user does the one thing only they can: in
/// their own browser, on the app's own page, they say yes. The Assistant never sees a password. What comes back are tokens, which are secrets: they are returned to the caller
/// to keep in the secret store, and are never logged. Every address that is talked to is <c>https</c> (or this PC) and nothing is followed to anywhere else.
/// </summary>
public sealed class McpOAuthClient : IDisposable
{
    /// <summary>How long the user has to finish in the browser.</summary>
    public static readonly TimeSpan DefaultSignInTime = TimeSpan.FromMinutes(5);

    private const int MaxResponseBytes = 256 * 1024;

    private readonly HttpClient _http;
    private readonly IOAuthBrowser _browser;
    private readonly TimeProvider _clock;

    /// <summary>Creates the client.</summary>
    /// <param name="browser">Opens the sign-in page for the user.</param>
    /// <param name="clock">The time, for when a token stops working.</param>
    /// <param name="handler">How requests are sent, for tests.</param>
    public McpOAuthClient(IOAuthBrowser browser, TimeProvider? clock = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        _browser = browser;
        _clock = clock ?? TimeProvider.System;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Signs in to the MCP server at <paramref name="endpoint"/>: finds its authorization server, registers the Assistant with it, sends the user to its page and waits for the
    /// user to come back, then trades what they were given for tokens.
    /// </summary>
    /// <param name="endpoint">The MCP server's address.</param>
    /// <param name="clientName">The name the server shows the user when asking them to say yes.</param>
    /// <param name="scope">What to ask for, or <see langword="null"/> to leave it to the server.</param>
    /// <param name="timeout">How long the user has; <see cref="DefaultSignInTime"/> when not given.</param>
    /// <param name="cancellationToken">Stops the sign-in.</param>
    /// <param name="options">How the sign-in page is shown, or <see langword="null"/> for the default browser.</param>
    /// <exception cref="OAuthException">It did not work.</exception>
    public async Task<OAuthSignIn> SignInAsync(
        Uri endpoint, string clientName, string? scope, TimeSpan? timeout, CancellationToken cancellationToken, OAuthSignInOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var info = await DiscoverAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (info.RegistrationEndpoint is null)
        {
            throw new OAuthException(OAuthFailure.NoRegistration);
        }

        return await AuthorizeAsync(
            info.AuthorizationEndpoint, info.TokenEndpoint, info.Resource, scope, "127.0.0.1", prompt: null, timeout, options,
            redirect => RegisterAsync(info.RegistrationEndpoint, clientName, redirect, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in to a service whose authorization server and client identity are known (<see cref="OAuthFixedClient"/>): nothing is discovered or registered, and the user
    /// is sent straight to the service's own page.
    /// </summary>
    /// <param name="options">How the sign-in page is shown, or <see langword="null"/> for the default browser.</param>
    /// <exception cref="OAuthException">It did not work.</exception>
    public Task<OAuthSignIn> SignInAsync(OAuthFixedClient client, TimeSpan? timeout, CancellationToken cancellationToken, OAuthSignInOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!IsAllowedAddress(client.AuthorizationEndpoint) || !IsAllowedAddress(client.TokenEndpoint))
        {
            throw new OAuthException(OAuthFailure.NotSupported);
        }

        return AuthorizeAsync(
            client.AuthorizationEndpoint, client.TokenEndpoint, resource: null, client.Scope, client.RedirectHost, client.Prompt, timeout, options,
            _ => Task.FromResult(client.ClientId), cancellationToken);
    }

    // The part every sign-in has in common: a port on this PC to come back to, PKCE, the user's browser, the code and its trade for tokens.
    private async Task<OAuthSignIn> AuthorizeAsync(
        Uri authorizationEndpoint,
        Uri tokenEndpoint,
        Uri? resource,
        string? scope,
        string redirectHost,
        string? prompt,
        TimeSpan? timeout,
        OAuthSignInOptions? options,
        Func<Uri, Task<string>> clientIdFor,
        CancellationToken cancellationToken)
    {
        using var listener = new LoopbackAuthorizationListener();
        // A service with a fixed client (Microsoft's) has registered its return as the bare address of this PC, so it is returned to without a path.
        var redirect = listener.Start(redirectHost, rootPath: resource is null);
        var redirectText = listener.RedirectText;
        var clientId = await clientIdFor(redirect).ConfigureAwait(false);

        var verifier = RandomToken(48);
        var state = RandomToken(24);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new List<(string, string)>
        {
            ("response_type", "code"),
            ("client_id", clientId),
            ("redirect_uri", redirectText),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
        };
        if (resource is not null)
        {
            query.Add(("resource", resource.OriginalString));
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            query.Add(("scope", scope));
        }

        // Asked to list the accounts, a service does not simply use the one the browser is signed in to (a school account, say) without the user choosing.
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            query.Add(("prompt", prompt));
        }

        var address = new Uri(AppendQuery(authorizationEndpoint, query));

        // The address is told before the browser is opened, so a user whose browser has the wrong account can copy it into another one.
        options?.AddressReady?.Invoke(address);
        if (options?.PrivateWindow == true)
        {
            if (!await _browser.OpenPrivateAsync(address, cancellationToken).ConfigureAwait(false))
            {
                throw new OAuthException(OAuthFailure.PrivateBrowserFailed);
            }
        }
        else if (!await _browser.OpenAsync(address, cancellationToken).ConfigureAwait(false))
        {
            throw new OAuthException(OAuthFailure.BrowserFailed);
        }

        IReadOnlyDictionary<string, string> returned;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(timeout ?? DefaultSignInTime);
            try
            {
                returned = await listener.WaitAsync("You are signed in. You can close this window and go back to the Assistant.", "Signing in did not work. You can close this window.", limit.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OAuthException(OAuthFailure.TimedOut);
            }
            catch (OperationCanceledException)
            {
                throw new OAuthException(OAuthFailure.Cancelled);
            }
        }

        if (returned.ContainsKey("error") || !returned.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
        {
            throw new OAuthException(OAuthFailure.Denied);
        }

        // Only the sign-in this program started can finish: the state that went out is the state that came back.
        if (!returned.TryGetValue("state", out var returnedState) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(returnedState), Encoding.ASCII.GetBytes(state)))
        {
            throw new OAuthException(OAuthFailure.Denied);
        }

        var form = new List<(string, string)>
        {
            ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectText), ("client_id", clientId), ("code_verifier", verifier),
        };
        if (resource is not null)
        {
            form.Add(("resource", resource.OriginalString));
        }

        if (!string.IsNullOrWhiteSpace(scope) && resource is null)
        {
            form.Add(("scope", scope));
        }

        var tokens = await TokenAsync(tokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        return new OAuthSignIn(tokenEndpoint, clientId, resource, tokens) { Scope = scope };
    }

    /// <summary>Gets a new access token with a refresh token. A server that gives a new refresh token too has its new one returned; otherwise the old one stays in use.</summary>
    /// <exception cref="OAuthException">The server refused, or could not be reached.</exception>
    public async Task<OAuthTokens> RefreshAsync(Uri tokenEndpoint, string clientId, string refreshToken, Uri? resource, CancellationToken cancellationToken, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        var form = new List<(string, string)> { ("grant_type", "refresh_token"), ("refresh_token", refreshToken), ("client_id", clientId) };
        if (resource is not null)
        {
            form.Add(("resource", resource.OriginalString));
        }

        if (resource is null && !string.IsNullOrWhiteSpace(scope))
        {
            form.Add(("scope", scope));
        }

        var tokens = await TokenAsync(tokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        return tokens.RefreshToken is null ? tokens with { RefreshToken = refreshToken } : tokens;
    }

    /// <summary>Finds the authorization server of the MCP server at <paramref name="endpoint"/>.</summary>
    internal async Task<OAuthServerInfo> DiscoverAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var origin = new Uri(endpoint.GetLeftPart(UriPartial.Authority));
        var path = endpoint.AbsolutePath.TrimEnd('/');

        // The server says which authorization server it trusts (RFC 9728); a server that does not say is its own authorization server. It also
        // says what it is called as a resource, which is what its authorization server expects to be asked for (RFC 8707): a server at
        // https://host/v2 may be the resource https://host, and asking for the address instead is refused ("invalid resource parameter").
        Uri authorizationServer = origin;
        var resource = new Uri(endpoint.AbsoluteUri);
        foreach (var candidate in new[] { path.Length > 0 ? new Uri(origin, "/.well-known/oauth-protected-resource" + path) : null, new Uri(origin, "/.well-known/oauth-protected-resource") })
        {
            if (candidate is null)
            {
                continue;
            }

            if (await GetJsonAsync(candidate, cancellationToken).ConfigureAwait(false) is { } document)
            {
                using (document)
                {
                    if (document.RootElement.TryGetProperty("authorization_servers", out var servers) && servers.ValueKind == JsonValueKind.Array
                        && servers.GetArrayLength() > 0 && servers[0].ValueKind == JsonValueKind.String
                        && Uri.TryCreate(servers[0].GetString(), UriKind.Absolute, out var found) && IsAllowedAddress(found))
                    {
                        authorizationServer = found;
                    }

                    if (Text(document.RootElement, "resource") is { } named && AdvertisedResource(endpoint, named) is { } advertised)
                    {
                        resource = advertised;
                    }
                }

                break;
            }
        }

        var asPath = authorizationServer.AbsolutePath.TrimEnd('/');
        var asOrigin = new Uri(authorizationServer.GetLeftPart(UriPartial.Authority));
        var metadataAddresses = new List<Uri>
        {
            new(asOrigin, "/.well-known/oauth-authorization-server" + asPath),
            new(asOrigin, "/.well-known/openid-configuration" + asPath),
            new(asOrigin, "/.well-known/oauth-authorization-server"),
            new(asOrigin, "/.well-known/openid-configuration"),
        };
        foreach (var address in metadataAddresses.Distinct())
        {
            if (await GetJsonAsync(address, cancellationToken).ConfigureAwait(false) is not { } document)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var authorize = Address(root, "authorization_endpoint");
                var token = Address(root, "token_endpoint");
                if (authorize is null || token is null)
                {
                    continue;
                }

                return new OAuthServerInfo(resource, authorize, token, Address(root, "registration_endpoint"));
            }
        }

        throw new OAuthException(OAuthFailure.NotSupported);
    }

    /// <summary>
    /// The resource a server says it is, when that can be the server at <paramref name="endpoint"/>: the same scheme, host and port, and a path the endpoint's is at or
    /// under. Anything else (another host, a path beside the endpoint's) is not taken, so a server cannot have a token asked for in another's name. The text is kept as the
    /// server wrote it (<see cref="Uri.OriginalString"/>), since an authorization server may compare it letter for letter.
    /// </summary>
    internal static Uri? AdvertisedResource(Uri endpoint, string? advertised)
    {
        if (string.IsNullOrWhiteSpace(advertised) || !Uri.TryCreate(advertised.Trim(), UriKind.Absolute, out var resource) || !IsAllowedAddress(resource)
            || !string.IsNullOrEmpty(resource.Query)
            || !string.Equals(resource.GetLeftPart(UriPartial.Authority), endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = resource.AbsolutePath.TrimEnd('/');
        var served = endpoint.AbsolutePath.TrimEnd('/');
        return path.Length == 0 || served.Equals(path, StringComparison.Ordinal) || served.StartsWith(path + "/", StringComparison.Ordinal) ? resource : null;
    }

    private async Task<string> RegisterAsync(Uri registration, string clientName, Uri redirect, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            client_name = clientName,
            redirect_uris = new[] { redirect.AbsoluteUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        });
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, registration) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new OAuthException(OAuthFailure.NoRegistration);
            }

            using var document = JsonDocument.Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false));
            return document.RootElement.TryGetProperty("client_id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString())
                ? id.GetString()!
                : throw new OAuthException(OAuthFailure.NoRegistration);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException && !cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException(OAuthFailure.Unreachable, exception);
        }
    }

    private async Task<OAuthTokens> TokenAsync(Uri tokenEndpoint, IReadOnlyList<(string Key, string Value)> form, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new OAuthException(OAuthFailure.TokenRefused);
            }

            using var document = JsonDocument.Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            var access = Text(root, "access_token");
            if (string.IsNullOrEmpty(access))
            {
                throw new OAuthException(OAuthFailure.TokenRefused);
            }

            DateTimeOffset? expires = root.TryGetProperty("expires_in", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt64(out var value) && value > 0
                ? _clock.GetUtcNow().AddSeconds(value)
                : null;
            return new OAuthTokens(access, Text(root, "refresh_token"), expires, Text(root, "scope"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException && !cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException(OAuthFailure.Unreachable, exception);
        }
    }

    // A page of JSON from a well-known address, or null when it is not there or is not JSON.
    private async Task<JsonDocument?> GetJsonAsync(Uri address, CancellationToken cancellationToken)
    {
        if (!IsAllowedAddress(address))
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? JsonDocument.Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false)) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes)
            {
                throw new IOException("The answer is larger than expected.");
            }
        }

        return buffer.ToArray();
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? Address(JsonElement root, string name) =>
        Text(root, name) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var address) && IsAllowedAddress(address) ? address : null;

    // https, or http on this PC for a server that runs here (a connected app's own local server).
    internal static bool IsAllowedAddress(Uri address) =>
        address.IsAbsoluteUri && string.IsNullOrEmpty(address.UserInfo) && string.IsNullOrEmpty(address.Fragment)
        && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp && address.IsLoopback);

    private static string AppendQuery(Uri address, IEnumerable<(string Key, string Value)> query)
    {
        var builder = new StringBuilder(address.AbsoluteUri);
        builder.Append(address.Query.Length == 0 ? '?' : '&');
        builder.Append(string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        return builder.ToString();
    }

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
