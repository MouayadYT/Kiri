using System.Collections.Concurrent;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp.Auth;

/// <summary>Gives the access token of a connected app that is signed in with OAuth, renewing it when it has run out, for the connection that is being made.</summary>
public interface IMcpAccessTokens
{
    /// <summary>
    /// The access token to send for <paramref name="integration"/>: the one kept, or, when it is about to stop working, a new one got with the refresh token. A token that
    /// cannot be had is an <see cref="McpFailure.AuthRequired"/>, so the app is shown as needing to be signed in to again.
    /// </summary>
    /// <exception cref="McpException">There is none, or it could not be renewed.</exception>
    Task<string> GetAccessTokenAsync(InstalledIntegration integration, CancellationToken cancellationToken = default);

    /// <summary>
    /// The server refused the access token it was given (it was revoked, or ran out earlier than it said): marks it as run out, so that the next <see cref="GetAccessTokenAsync"/> renews it with the
    /// refresh token. Returns <see langword="false"/> when there is no refresh token, so that nothing can be renewed and the user has to sign in again.
    /// </summary>
    Task<bool> ExpireAsync(string integrationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the sign-ins of the connected apps (PROJECT_SPEC §4.8): each app's tokens, and what is needed to renew them, as one JSON value in the secret store. It is longer than one
/// credential holds, so it is cut into pieces of 900 characters, each its own secret named <c>&lt;id&gt;.oauth.&lt;n&gt;</c>; the integration's record names the pieces and holds nothing else.
/// Nothing here is logged, and the tokens are in memory only while a connection is made.
/// </summary>
public sealed class McpOAuthSessions : IMcpAccessTokens
{
    private const int PieceLength = 900;
    private const int MaxPieces = 8;
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(2);

    private readonly ISecretStore _secrets;
    private readonly McpOAuthClient _client;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Creates the store.</summary>
    public McpOAuthSessions(ISecretStore secrets, McpOAuthClient client, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(client);
        _secrets = secrets;
        _client = client;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The secret names of an integration's pieces, in order.</summary>
    public static IReadOnlyList<string> PieceNames(string integrationId, int count) =>
        [.. Enumerable.Range(0, count).Select(index => $"{integrationId}.oauth.{index}")];

    /// <summary>Keeps a new sign-in, replacing any before it, and says which secrets hold it.</summary>
    public async Task<IReadOnlyList<IntegrationSecretBinding>> SaveAsync(string integrationId, OAuthSignIn signIn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(integrationId);
        ArgumentNullException.ThrowIfNull(signIn);
        var session = new Session(1, signIn.TokenEndpoint.AbsoluteUri, signIn.ClientId, signIn.Resource?.OriginalString ?? string.Empty, signIn.Tokens.AccessToken, signIn.Tokens.RefreshToken,
            signIn.Tokens.ExpiresAt?.ToUnixTimeSeconds()) { Scope = signIn.Scope };
        return await WriteAsync(integrationId, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a sign-in is kept for the integration, and when its access token stops working, if it said.</summary>
    public async Task<(bool Exists, DateTimeOffset? ExpiresAt)> StatusAsync(string integrationId, CancellationToken cancellationToken)
    {
        var session = await ReadAsync(integrationId, cancellationToken).ConfigureAwait(false);
        return (session is not null, session?.ExpiresAt is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
    }

    /// <inheritdoc/>
    public async Task<bool> ExpireAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(integrationId);
        var gate = _gates.GetOrAdd(integrationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await ReadAsync(integrationId, cancellationToken).ConfigureAwait(false);
            if (session is null || string.IsNullOrEmpty(session.RefreshToken))
            {
                return false;
            }

            await WriteAsync(integrationId, session with { ExpiresAt = _clock.GetUtcNow().AddSeconds(-1).ToUnixTimeSeconds() }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SecretStoreException or ArgumentException or JsonException or OAuthException)
        {
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forgets the integration's sign-in.</summary>
    public async Task DeleteAsync(string integrationId, CancellationToken cancellationToken)
    {
        for (var index = 0; index < MaxPieces; index++)
        {
            await _secrets.DeleteAsync($"{integrationId}.oauth.{index}", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetAccessTokenAsync(InstalledIntegration integration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integration);
        var gate = _gates.GetOrAdd(integration.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session? session;
            try
            {
                session = await ReadAsync(integration.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SecretStoreException or ArgumentException or JsonException)
            {
                throw new McpException(McpFailure.AuthRequired, inner: exception);
            }

            if (session is null || string.IsNullOrEmpty(session.AccessToken))
            {
                throw new McpException(McpFailure.AuthRequired);
            }

            var expires = session.ExpiresAt is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
            if (expires is null || expires - _clock.GetUtcNow() > RenewBefore)
            {
                return session.AccessToken;
            }

            // About to stop working: renewed once, here, for every connection that is waiting.
            if (string.IsNullOrEmpty(session.RefreshToken) || !Uri.TryCreate(session.TokenEndpoint, UriKind.Absolute, out var endpoint) || !McpOAuthClient.IsAllowedAddress(endpoint))
            {
                throw new McpException(McpFailure.AuthRequired);
            }

            OAuthTokens renewed;
            try
            {
                renewed = await _client.RefreshAsync(
                    endpoint, session.ClientId, session.RefreshToken, Uri.TryCreate(session.Resource, UriKind.Absolute, out var resource) ? resource : null, cancellationToken, session.Scope)
                    .ConfigureAwait(false);
            }
            catch (OAuthException exception) when (exception.Failure == OAuthFailure.TokenRefused)
            {
                throw new McpException(McpFailure.AuthRequired, inner: exception);
            }
            catch (OAuthException exception)
            {
                throw new McpException(McpFailure.ConnectFailed, inner: exception);
            }

            await WriteAsync(
                integration.Id,
                session with { AccessToken = renewed.AccessToken, RefreshToken = renewed.RefreshToken, ExpiresAt = renewed.ExpiresAt?.ToUnixTimeSeconds() },
                cancellationToken).ConfigureAwait(false);
            return renewed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<IntegrationSecretBinding>> WriteAsync(string integrationId, Session session, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(session);
        var pieces = Enumerable.Range(0, (json.Length + PieceLength - 1) / PieceLength).Select(index => json.Substring(index * PieceLength, Math.Min(PieceLength, json.Length - index * PieceLength))).ToList();
        if (pieces.Count > MaxPieces)
        {
            throw new OAuthException(OAuthFailure.TokenRefused);
        }

        var names = PieceNames(integrationId, pieces.Count);
        for (var index = 0; index < pieces.Count; index++)
        {
            await _secrets.SetAsync(names[index], pieces[index], cancellationToken).ConfigureAwait(false);
        }

        // Pieces of an older, longer value are not left behind.
        for (var index = pieces.Count; index < MaxPieces; index++)
        {
            await _secrets.DeleteAsync($"{integrationId}.oauth.{index}", cancellationToken).ConfigureAwait(false);
        }

        return [.. names.Select(name => new IntegrationSecretBinding("oauth", name))];
    }

    private async Task<Session?> ReadAsync(string integrationId, CancellationToken cancellationToken)
    {
        var json = new System.Text.StringBuilder();
        for (var index = 0; index < MaxPieces; index++)
        {
            var piece = await _secrets.GetAsync($"{integrationId}.oauth.{index}", cancellationToken).ConfigureAwait(false);
            if (piece is null)
            {
                break;
            }

            json.Append(piece);
        }

        return json.Length == 0 ? null : JsonSerializer.Deserialize<Session>(json.ToString());
    }

    private sealed record Session(
        [property: System.Text.Json.Serialization.JsonPropertyName("v")] int Version,
        [property: System.Text.Json.Serialization.JsonPropertyName("t")] string TokenEndpoint,
        [property: System.Text.Json.Serialization.JsonPropertyName("c")] string ClientId,
        [property: System.Text.Json.Serialization.JsonPropertyName("r")] string Resource,
        [property: System.Text.Json.Serialization.JsonPropertyName("a")] string AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("f")] string? RefreshToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("e")] long? ExpiresAt)
    {
        [System.Text.Json.Serialization.JsonPropertyName("s")]
        public string? Scope { get; init; }
    }
}
