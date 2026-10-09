using System.Text.Json.Serialization;
using Assistant.Core.Domain;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Integrations;

/// <summary>
/// One connected app (PROJECT_SPEC §4.8, step 104): exactly what is needed to reconnect to it later, and nothing else. It holds no secret
/// (an access token or key lives in Windows Credential Manager and is named here by <see cref="IntegrationSecretBinding.SecretName"/>),
/// no tool schema, no result and no conversation content. It is the record the registry keeps and the connection manager reads; it is
/// changed only by code that was asked to (never by the model, a tool's result or a file).
/// </summary>
public sealed record InstalledIntegration
{
    /// <summary>The integration's id: a short run of lower-case letters and digits (<see cref="IntegrationRules.IsValidId"/>), stable for as long as it is installed.</summary>
    public required string Id { get; init; }

    /// <summary>The name of the app or service, as the user knows it ("Todoist"), shown to them and used to match a request to the app.</summary>
    public required string Name { get; init; }

    /// <summary>Where it came from.</summary>
    public IntegrationSource Source { get; init; } = new(IntegrationSourceKind.UserAdded, null);

    /// <summary>How to reach it: a remote endpoint, or the program to start.</summary>
    public required IntegrationTransport Transport { get; init; }

    /// <summary>The version of the integration (the server) that was installed, as its source gave it; <see langword="null"/> when unknown.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>
    /// Whether it may be used. A disabled integration is never connected to and none of its tools is offered; it stays installed.
    /// Off until the code that installs it turns it on.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>What the server said it can do when last connected to.</summary>
    public IntegrationCapabilities Capabilities { get; init; } = IntegrationCapabilities.None;

    /// <summary>How it signs the Assistant in, and whether that works at present.</summary>
    public IntegrationAuthentication Authentication { get; init; } = IntegrationAuthentication.None;

    /// <summary>What the user allowed it, and what its tools may do: the limits the tool registry applies to what it offers.</summary>
    public IntegrationPermissions Permissions { get; init; } = IntegrationPermissions.Default;

    /// <summary>How the last attempt to reach it went.</summary>
    public IntegrationHealth Health { get; init; } = IntegrationHealth.Unknown;

    /// <summary>
    /// How the Assistant installed it, when it did (step 108): the package, its checksum and who made it, so that a newer version can be looked for and
    /// the files it owns found; <see langword="null"/> for an integration the user added themselves.
    /// </summary>
    public ManagedInstall? Managed { get; init; }

    /// <summary>
    /// Whether it is made up for trying the Assistant (step 116): the calendar and the messaging app of the workflow demo. What it holds is not the user's own, and a message sent
    /// through it reaches no one; the Assistant says so in its confirmation and in what it tells the model. Only the code that installs a sample sets it.
    /// </summary>
    public bool IsSample { get; init; }
}

/// <summary>Where an integration came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntegrationSourceKind>))]
public enum IntegrationSourceKind
{
    /// <summary>The user added it themselves, by giving its address or the program to run.</summary>
    UserAdded = 0,

    /// <summary>It was found in the official MCP registry.</summary>
    OfficialRegistry = 1,

    /// <summary>It was found in a community catalog.</summary>
    CommunityRegistry = 2,

    /// <summary>It ships with the Assistant.</summary>
    Bundled = 3,
}

/// <summary>An integration's origin.</summary>
/// <param name="Kind">What kind of place it came from.</param>
/// <param name="Origin">
/// Where, as its source names it (a registry name, a package or a web address), for the user to see what they installed; at most
/// <see cref="IntegrationRules.MaxOriginLength"/> characters. It is never followed or fetched.
/// </param>
public sealed record IntegrationSource(IntegrationSourceKind Kind, string? Origin);

/// <summary>How to reach an integration. Which members apply depends on <see cref="Kind"/>.</summary>
public sealed record IntegrationTransport
{
    /// <summary>The way the Assistant speaks to it: a local program over its standard streams, or a remote server over HTTP.</summary>
    public McpTransportKind Kind { get; init; } = McpTransportKind.StreamableHttp;

    /// <summary>
    /// For an HTTP kind, the server's address: <c>https</c>, or <c>http</c> only on this PC (<c>localhost</c>, <c>127.0.0.1</c>,
    /// <c>[::1]</c>), without a user name, a password or a fragment (<see cref="McpEndpointRules"/>).
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// For <see cref="McpTransportKind.Stdio"/>, the full path of the program to start: an <c>.exe</c> on a local drive that is not a shell
    /// or a script host (<see cref="McpLaunchRules"/>). It is started directly, never through a shell. Set by the code that installs the
    /// integration; nothing the model, a tool or a file says can change it.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>The program's arguments, one by one (never joined into a command line).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The folder the program starts in; <see langword="null"/> for the program's own.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables the program is given besides the few Windows needs (the Assistant's own environment is not passed on). Not for
    /// secrets: those are named by <see cref="IntegrationAuthentication.Secrets"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Public HTTP access-mode options. Credentials remain in Authentication, never here.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>What the server said it can do (MCP <c>capabilities</c>), kept so that a request can be matched to the app before it is connected to.</summary>
public sealed record IntegrationCapabilities
{
    /// <summary>It offers tools.</summary>
    public bool Tools { get; init; }

    /// <summary>It offers resources (not used in this version).</summary>
    public bool Resources { get; init; }

    /// <summary>It offers prompts (not used in this version).</summary>
    public bool Prompts { get; init; }

    /// <summary>The MCP protocol version that was agreed when last connected to, such as <c>2026-07-28</c>; <see langword="null"/> before the first connection.</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>
    /// The names the server gave its tools when last connected to (at most <see cref="IntegrationRules.MaxToolNames"/>), so that a request that
    /// mentions what a tool does can be matched to the app without starting it. Only names: no description, schema or result.
    /// </summary>
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    /// <summary>When the tool names were last read.</summary>
    public DateTimeOffset? RefreshedAt { get; init; }

    /// <summary>An integration that has not been connected to.</summary>
    public static IntegrationCapabilities None => new();
}

/// <summary>How an integration signs the Assistant in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntegrationAuthKind>))]
public enum IntegrationAuthKind
{
    /// <summary>It needs no sign-in.</summary>
    None = 0,

    /// <summary>A bearer token sent as <c>Authorization: Bearer</c> over HTTP.</summary>
    BearerToken = 1,

    /// <summary>A key sent in an HTTP header whose name the binding gives.</summary>
    HeaderKey = 2,

    /// <summary>Secrets given to the program as environment variables whose names the bindings give (the way a local server is signed in).</summary>
    EnvironmentSecret = 3,

    /// <summary>OAuth 2.1 sign-in in the user's browser. The sign-in itself is not built in this version: the state records that it is needed.</summary>
    OAuth = 4,
}

/// <summary>Whether an integration's sign-in works at present.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntegrationAuthState>))]
public enum IntegrationAuthState
{
    /// <summary>Nothing is known (not connected to yet).</summary>
    Unknown = 0,

    /// <summary>It needs no sign-in.</summary>
    NotRequired = 1,

    /// <summary>The credentials are there and were accepted.</summary>
    Ready = 2,

    /// <summary>The server asked for a sign-in the Assistant does not have (an HTTP 401), or the secret is not in the secret store.</summary>
    NeedsSignIn = 3,

    /// <summary>The credentials were once accepted and have expired.</summary>
    Expired = 4,

    /// <summary>The server refused the credentials it was given.</summary>
    Rejected = 5,
}

/// <summary>
/// A secret an integration uses, by the name it is kept under in Windows Credential Manager (<c>ISecretStore</c>). The registry file holds
/// only this reference, never the secret.
/// </summary>
/// <param name="Target">
/// Where it goes: the header's name for <see cref="IntegrationAuthKind.HeaderKey"/>, the variable's name for
/// <see cref="IntegrationAuthKind.EnvironmentSecret"/>, and for a bearer token the word <c>Authorization</c>.
/// </param>
/// <param name="SecretName">The secret's name in the secret store (<c>SecretNames.IsValid</c>).</param>
public sealed record IntegrationSecretBinding(string Target, string SecretName);

/// <summary>How an integration signs in, and how that stands.</summary>
public sealed record IntegrationAuthentication
{
    /// <summary>How it signs in.</summary>
    public IntegrationAuthKind Kind { get; init; }

    /// <summary>Whether the sign-in works at present.</summary>
    public IntegrationAuthState State { get; init; } = IntegrationAuthState.NotRequired;

    /// <summary>The secrets it uses, by reference.</summary>
    public IReadOnlyList<IntegrationSecretBinding> Secrets { get; init; } = [];

    /// <summary>The scopes the user granted, as the server named them (for the user to see; they are not acted on).</summary>
    public IReadOnlyList<string> GrantedScopes { get; init; } = [];

    /// <summary>When the credentials stop working, if that is known.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// For a program the Assistant starts that is signed in with OAuth (Microsoft To Do's), the name of the environment variable the access token is given to it in each time it starts.
    /// The token itself is in the secret store.
    /// </summary>
    public string? TokenVariable { get; init; }

    /// <summary>When the state was last decided.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>An integration that needs no sign-in.</summary>
    public static IntegrationAuthentication None => new();
}

/// <summary>
/// What an integration may do, in terms the tool registry applies when it decides what to offer and how to treat a call. It is fixed by the
/// code that installs the integration and the user; what a server says about its own tools (a hint that one only reads) is a hint and
/// changes nothing unless <see cref="TrustToolAnnotations"/> says the user trusts the server's.
/// </summary>
public sealed record IntegrationPermissions
{
    /// <summary>
    /// The permission (Settings, Permissions) every one of its tools needs besides the integration being enabled, so that the app is
    /// refused while the user has it off; <see langword="null"/> for none. Never Destructive Actions (<see cref="IntegrationRules"/>).
    /// </summary>
    public PermissionCapability? RequiredCapability { get; init; }

    /// <summary>
    /// Whether using it sends something off this PC even though it is reached locally (a local server that calls a web service). An
    /// integration reached over the network always counts as leaving this PC. Local Only mode (Settings, Privacy) refuses both.
    /// </summary>
    public bool LeavesThisPc { get; init; }

    /// <summary>
    /// Whether any tool that is not known to be read-only may be offered. When it is <see langword="false"/> only the tools the user vetted
    /// as read-only are offered. A tool that changes something is run only after the user confirms the call, whatever this says.
    /// </summary>
    public bool AllowSideEffects { get; init; } = true;

    /// <summary>
    /// Whether the tools known to be read-only (the ones the user vetted, or the server's hint where the user trusts it) may be offered (step 119). When it is
    /// <see langword="false"/> none of them is: the integration cannot be used to look at anything. It is on for an integration that was installed before the control existed.
    /// </summary>
    public bool AllowReads { get; init; } = true;

    /// <summary>
    /// Whether the Assistant may connect to this integration over a network, or at all when it is recorded as reaching out itself (step 119): it applies to one reached at an
    /// <c>https</c> address and to a local program recorded as <see cref="LeavesThisPc"/>. While it is <see langword="false"/> such an integration is not connected to, whatever Local Only mode
    /// says. It changes nothing for a local program that is not recorded as reaching out, since the Assistant cannot see or limit what such a program does itself.
    /// </summary>
    public bool AllowNetwork { get; init; } = true;

    /// <summary>
    /// Whether the integration may use the account it is signed in to, that is, whether its sign-in (a key, a token) may be given to it (step 119). While it is <see langword="false"/> an
    /// integration that signs in is not connected to, so none of its secrets is read. It does nothing for an integration that needs no sign-in.
    /// </summary>
    public bool AllowAccountAccess { get; init; } = true;

    /// <summary>
    /// Whether the Assistant may look for a newer version of this integration and offer it (step 119). Off, it is never looked up and never offered; the version that is
    /// installed stays as it is. An update is made only when the user approves the offer, whatever this says.
    /// </summary>
    public bool AllowUpdates { get; init; } = true;

    /// <summary>
    /// Whether a tool the server marks read-only (<c>readOnlyHint</c>) is treated as read-only. Off by default: the protocol says a server's
    /// hints are not to be trusted unless the server is, so every tool needs the user's confirmation unless it is listed in
    /// <see cref="ReadOnlyTools"/>.
    /// </summary>
    public bool TrustToolAnnotations { get; init; }

    /// <summary>The tools (by the name the server gives them) that the user vetted as only reading, which run without being confirmed.</summary>
    public IReadOnlyList<string> ReadOnlyTools { get; init; } = [];

    /// <summary>The tools (by the name the server gives them) that are never offered.</summary>
    public IReadOnlyList<string> BlockedTools { get; init; } = [];

    /// <summary>The default: every tool is confirmed each time, nothing is trusted, and no permission beyond the integration being enabled.</summary>
    public static IntegrationPermissions Default => new();
}

/// <summary>How the last attempt to reach an integration went.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntegrationHealthStatus>))]
public enum IntegrationHealthStatus
{
    /// <summary>Never connected to.</summary>
    Unknown = 0,

    /// <summary>The last connection worked.</summary>
    Healthy = 1,

    /// <summary>The server wants a sign-in the Assistant does not have.</summary>
    AuthRequired = 2,

    /// <summary>The server could not be reached or started, or stopped answering.</summary>
    Unreachable = 3,

    /// <summary>The server speaks no protocol version or transport this version understands.</summary>
    Incompatible = 4,

    /// <summary>Anything else went wrong.</summary>
    Failed = 5,
}

/// <summary>How the last attempt to reach an integration went. It holds a code, never the server's words.</summary>
public sealed record IntegrationHealth
{
    /// <summary>The state.</summary>
    public IntegrationHealthStatus Status { get; init; }

    /// <summary>When it was last checked.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>What went wrong, when something did.</summary>
    public McpFailure? Failure { get; init; }

    /// <summary>How many attempts in a row have failed.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>An integration that has not been checked.</summary>
    public static IntegrationHealth Unknown => new();
}
