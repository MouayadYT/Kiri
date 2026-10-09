using System.Text;

namespace Assistant.Tools.Mcp.Auth;

/// <summary>Why a sign-in to a connected app did not work. It is a code: the server's own words are never kept.</summary>
public enum OAuthFailure
{
    /// <summary>The server could not be reached, or answered with something that is not a sign-in answer.</summary>
    Unreachable = 0,

    /// <summary>The server does not offer the sign-in the Assistant does (no authorization server was found at it).</summary>
    NotSupported = 1,

    /// <summary>The server's authorization server does not let a program register itself, so the Assistant has no client identity to sign in with.</summary>
    NoRegistration = 2,

    /// <summary>The browser could not be opened.</summary>
    BrowserFailed = 3,

    /// <summary>Nobody finished signing in before the time ran out.</summary>
    TimedOut = 4,

    /// <summary>The user said no, or the server refused, in the browser.</summary>
    Denied = 5,

    /// <summary>The server did not give a token for the code, or refused to renew one.</summary>
    TokenRefused = 6,

    /// <summary>The sign-in was stopped.</summary>
    Cancelled = 7,

    /// <summary>A private browser window was asked for and could not be opened.</summary>
    PrivateBrowserFailed = 8,
}

/// <summary>A sign-in did not work. Its message holds no address, code, token or key.</summary>
public sealed class OAuthException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OAuthException(OAuthFailure failure, Exception? inner = null)
        : base("Signing in did not work: " + failure, inner)
    {
        Failure = failure;
    }

    /// <summary>What went wrong.</summary>
    public OAuthFailure Failure { get; }
}

/// <summary>Where the sign-in of a server happens, as its authorization server describes it.</summary>
/// <param name="Resource">The MCP server's address, as the resource the token is for.</param>
/// <param name="AuthorizationEndpoint">Where the user is sent to say yes.</param>
/// <param name="TokenEndpoint">Where the code is exchanged for a token, and the token renewed.</param>
/// <param name="RegistrationEndpoint">Where a program registers itself (dynamic client registration), or <see langword="null"/> when it cannot.</param>
public sealed record OAuthServerInfo(Uri Resource, Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri? RegistrationEndpoint);

/// <summary>What a server gave for a sign-in: tokens that are secrets, never logged and never shown.</summary>
/// <param name="AccessToken">The token sent with every request.</param>
/// <param name="RefreshToken">The token that gets a new access token, or <see langword="null"/> when none was given.</param>
/// <param name="ExpiresAt">When the access token stops working, when the server said.</param>
/// <param name="Scope">What the server says it granted.</param>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? Scope)
{
    // A token is a secret: ToString, and so a log, holds none.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"HasRefresh = {RefreshToken is not null}, ExpiresAt = {ExpiresAt}");
        return true;
    }
}

/// <summary>The result of a sign-in: who the Assistant signed in as (the client identity it registered) and the tokens it got.</summary>
/// <param name="TokenEndpoint">Where tokens are renewed.</param>
/// <param name="ClientId">The identity the Assistant registered with the server.</param>
/// <param name="Resource">The resource the tokens are for.</param>
/// <param name="Tokens">The tokens.</param>
public sealed record OAuthSignIn(Uri TokenEndpoint, string ClientId, Uri? Resource, OAuthTokens Tokens)
{
    /// <summary>What the sign-in asked for, kept so that renewing asks for the same.</summary>
    public string? Scope { get; init; }
}

/// <summary>
/// The sign-in of a service whose authorization server and client identity are known and fixed (Microsoft's, for Microsoft To Do), where nothing is found or registered at run time.
/// </summary>
/// <param name="AuthorizationEndpoint">Where the user is sent to say yes.</param>
/// <param name="TokenEndpoint">Where the code is exchanged for a token, and the token renewed.</param>
/// <param name="ClientId">The identity the Assistant signs in as: an application (client) ID.</param>
/// <param name="Scope">What to ask for.</param>
/// <param name="RedirectHost">The name the browser is sent back to on this PC: <c>127.0.0.1</c>, or <c>localhost</c> for a service that only registers that.</param>
/// <param name="Prompt">The <c>prompt</c> the service is asked to show (<c>select_account</c> makes Microsoft list the accounts instead of signing in the one the browser already has), or <see langword="null"/> for none.</param>
public sealed record OAuthFixedClient(
    Uri AuthorizationEndpoint, Uri TokenEndpoint, string ClientId, string Scope, string RedirectHost = "127.0.0.1", string? Prompt = null);

/// <summary>How the page of a sign-in is shown to the user, for the person who has more than one account or more than one browser.</summary>
public sealed record OAuthSignInOptions
{
    /// <summary>Open the page in a private browser window (InPrivate, incognito), which has none of the accounts the browser is signed in to, instead of the default browser's own window.</summary>
    public bool PrivateWindow { get; init; }

    /// <summary>
    /// Called with the address of the sign-in page as soon as it is known and before the browser is opened, so that it can be shown for the user to copy and open wherever they
    /// choose (another browser, another profile). It holds nothing that signs anyone in: the page is the service's own, and what it would give back goes only to this PC.
    /// </summary>
    public Action<Uri>? AddressReady { get; init; }
}

/// <summary>Opens a web address in the user's own browser, for the part of a sign-in only they can do. The Assistant never types a password for them.</summary>
public interface IOAuthBrowser
{
    /// <summary>Opens <paramref name="address"/>. Returns whether the browser could be started.</summary>
    Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default);

    /// <summary>Opens <paramref name="address"/> in a private window of the user's browser. Returns whether one could be started; a browser that cannot do that says no.</summary>
    Task<bool> OpenPrivateAsync(Uri address, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>The browser of a program that has none: a sign-in cannot start.</summary>
internal sealed class NoOAuthBrowser : IOAuthBrowser
{
    /// <inheritdoc/>
    public Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
