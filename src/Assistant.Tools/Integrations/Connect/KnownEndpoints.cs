using Assistant.Core.Domain;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Integrations;

/// <summary>
/// An explicitly selected MCP endpoint: a known app, service provider, or user-entered server. The known list is fixed in code;
/// custom URLs are accepted only from the connection editor. Nothing a search result or model response says can add to that list.
/// </summary>
/// <param name="AppKey">The app, as <see cref="KnownApp.Key"/> names it.</param>
/// <param name="Name">The app's name, as the user knows it.</param>
/// <param name="Endpoint">The server's address: <c>https</c>, or <c>http</c> on this PC for an app that runs here.</param>
/// <param name="Vendor">Whose server it is, as the user is shown it (the provider's domain).</param>
/// <param name="RunsOnThisPc">Whether the server is the app's own program on this PC, so that using it sends nothing over the internet and works while Local Only mode is on.</param>
/// <param name="Capability">The permission its tools need besides being turned on, or <see langword="null"/>.</param>
/// <param name="Scope">What to ask for when signing in, or <see langword="null"/> to leave it to the server.</param>
/// <param name="SignInHelp">What the user is told to do when the app must be set up before it can be signed in to (Beeper's switch), or <see langword="null"/>.</param>
public sealed record KnownEndpoint(
    string AppKey,
    string Name,
    string Endpoint,
    string Vendor,
    bool RunsOnThisPc = false,
    PermissionCapability? Capability = null,
    string? Scope = null,
    string? SignInHelp = null,
    string? Program = null,
    FixedSignIn? FixedSignIn = null,
    string? TokenVariable = null)
{
    /// <summary>The id the integration is recorded under.</summary>
    public string IntegrationId => AppKey;

    /// <summary>Whether the app is reached through a small program that ships with the Assistant (Microsoft To Do), and not through a server of its own.</summary>
    public bool IsBundled => Program is not null;

    /// <summary>Whether the app is reached through Pipedream, where the user signs in and selects the app.</summary>
    public bool IsThroughPipedream => string.Equals(Vendor, "pipedream.com", StringComparison.Ordinal);
}

/// <summary>How a bundled program's app is signed in to: the service's authorization server and a client identity that are known, since nothing can be found or registered at run time.</summary>
/// <param name="Authorization">Where the user is sent to say yes.</param>
/// <param name="Token">Where the code is traded for tokens.</param>
/// <param name="DefaultClientId">The application (client) ID used unless the user chose another in Settings.</param>
/// <param name="Scope">What is asked for.</param>
/// <param name="RedirectHost">The name the browser is sent back to on this PC.</param>
/// <param name="Prompt">The <c>prompt</c> asked of the service, or <see langword="null"/>: Microsoft's <c>select_account</c> lists the accounts, so that a person signed in to a school or work account in their browser can choose another.</param>
public sealed record FixedSignIn(string Authorization, string Token, string DefaultClientId, string Scope, string RedirectHost, string? Prompt = null)
{
    /// <summary>The sign-in as the OAuth client takes it, with <paramref name="clientId"/> when the user chose one.</summary>
    public Mcp.Auth.OAuthFixedClient For(string? clientId) =>
        new(new Uri(Authorization), new Uri(Token), string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId, Scope, RedirectHost, Prompt);
}

/// <summary>The programs that ship beside the Assistant and that it starts for an app (Microsoft To Do's).</summary>
public static class BundledPrograms
{
    /// <summary>The full path of the program called <paramref name="name"/> in the Assistant's own folder, or <see langword="null"/> when it is not there.</summary>
    public static string? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
        {
            return null;
        }

        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>The fixed list of <see cref="KnownEndpoint"/>s.</summary>
public static class KnownEndpoints
{
    private static readonly KnownEndpoint[] All_ =
    [
        new("todoist", "Todoist", "https://ai.todoist.net/mcp", "todoist.com"),
        new("notion", "Notion", "https://mcp.notion.com/mcp", "notion.com"),
        new("linear", "Linear", "https://mcp.linear.app/mcp", "linear.app"),
        new("sentry", "Sentry", "https://mcp.sentry.dev/mcp", "sentry.io"),
        new(
            "beeper", "Beeper", "http://localhost:23373/v0/mcp", "beeper.com", RunsOnThisPc: true, Capability: PermissionCapability.Messaging,
            SignInHelp: "Open Beeper, go to Settings → Integrations, and enable its Desktop API & MCP server. Keep Beeper running, then try Connect again."),

        // Microsoft has no hosted server for To Do, so a small program that ships with the Assistant talks to Microsoft Graph for it, with the access token the user's sign-in gave.
        new(
            "microsofttodo", "Microsoft To Do", "https://graph.microsoft.com/v1.0/me/todo", "microsoft.com",
            Program: "Assistant.MicrosoftTodo.exe",
            FixedSignIn: new FixedSignIn(
                "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                "14d82eec-204b-4c2f-b7e8-296a70dab67e", "Tasks.ReadWrite offline_access", "localhost", Prompt: "select_account"),
            TokenVariable: "MSGRAPH_ACCESS_TOKEN"),
        new("microsofttodopipedream", "Microsoft To Do", "https://mcp.pipedream.net/v2", "pipedream.com", Scope: "mcp offline_access",
            SignInHelp: "Sign in to Pipedream and select Microsoft To Do in its connection page."),

        // Discord, through Pipedream as well (https://mcp.pipedream.com/app/discord): Pipedream has one address for every app, and which app a
        // connection is for is chosen on its own page when signing in.
        new("discordpipedream", "Discord", "https://mcp.pipedream.net/v2", "pipedream.com", Scope: "mcp offline_access",
            SignInHelp: "Sign in to Pipedream and select Discord in its connection page."),
    ];

    /// <summary>Every known endpoint.</summary>
    public static IReadOnlyList<KnownEndpoint> All => All_;

    /// <summary>The endpoint of the app with <paramref name="appKey"/>, or <see langword="null"/> when none is known.</summary>
    public static KnownEndpoint? For(string? appKey) =>
        appKey is null ? null : Array.Find(All_, endpoint => string.Equals(endpoint.AppKey, appKey, StringComparison.Ordinal));

    internal static bool IsValid(KnownEndpoint endpoint)
    {
        // McpEndpointRules lets http through only on this PC and the user's own network; a server on this PC must also say that it is one.
        if (McpEndpointRules.Problem(endpoint.Endpoint, out var uri) is not null || uri is null
            || uri.Scheme != Uri.UriSchemeHttps && !(endpoint.RunsOnThisPc && uri.IsLoopback) && !McpNetwork.IsOwnNetwork(uri))
        {
            return false;
        }

        // A bundled program is a file name beside the Assistant, signed in to at fixed https addresses, with a token variable the program can have.
        return !endpoint.IsBundled
            || endpoint.Program is { } program && program == Path.GetFileName(program) && program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && endpoint.FixedSignIn is { } fixedSignIn && Uri.TryCreate(fixedSignIn.Authorization, UriKind.Absolute, out var authorize) && authorize.Scheme == Uri.UriSchemeHttps
            && Uri.TryCreate(fixedSignIn.Token, UriKind.Absolute, out var token) && token.Scheme == Uri.UriSchemeHttps
            && McpLaunchRules.IsValidVariableName(endpoint.TokenVariable);
    }
}
