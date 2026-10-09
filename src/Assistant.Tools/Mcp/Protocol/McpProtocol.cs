using System.Reflection;
using System.Text.Json.Nodes;

namespace Assistant.Tools.Mcp;

/// <summary>Which of the two eras of the Model Context Protocol a server speaks.</summary>
internal enum McpEra
{
    /// <summary>
    /// The stateless protocol of revision 2026-07-28 and later: no handshake, every request carries its protocol version and the client's
    /// capabilities in <c>_meta</c>, and the server is asked what it is with <c>server/discover</c>.
    /// </summary>
    Modern = 0,

    /// <summary>The protocol of revisions 2025-11-25 and earlier: a connection starts with <c>initialize</c> and <c>notifications/initialized</c>.</summary>
    Legacy = 1,
}

/// <summary>
/// The Model Context Protocol the Assistant speaks (step 104): the versions it knows, the method names and error codes it uses, and the
/// <c>_meta</c> a modern request carries. It is a client of tools only: it declares no client capabilities (no sampling, roots or elicitation),
/// so a server cannot ask it for anything, and it never uses resources or prompts.
/// </summary>
internal static class McpProtocol
{
    /// <summary>The modern revision the Assistant speaks.</summary>
    public const string ModernVersion = "2026-07-28";

    /// <summary>The newest of the earlier revisions, which a connection that starts with <c>initialize</c> asks for first.</summary>
    public const string LatestLegacyVersion = "2025-11-25";

    /// <summary>The earlier revisions the Assistant speaks, newest first. A server that answers <c>initialize</c> with another version is refused.</summary>
    public static readonly IReadOnlyList<string> LegacyVersions = [LatestLegacyVersion, "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>The name the Assistant gives itself to a server.</summary>
    public const string ClientName = "Assistant";

    // Methods.
    public const string Initialize = "initialize";
    public const string Initialized = "notifications/initialized";
    public const string Discover = "server/discover";
    public const string ListTools = "tools/list";
    public const string CallTool = "tools/call";
    public const string Ping = "ping";
    public const string Cancelled = "notifications/cancelled";
    public const string ToolsListChanged = "notifications/tools/list_changed";

    // Error codes.
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int HeaderMismatch = -32020;
    public const int MissingRequiredClientCapability = -32021;
    public const int UnsupportedProtocolVersion = -32022;

    // The names of the per-request metadata of the modern era.
    public const string MetaProtocolVersion = "io.modelcontextprotocol/protocolVersion";
    public const string MetaClientInfo = "io.modelcontextprotocol/clientInfo";
    public const string MetaClientCapabilities = "io.modelcontextprotocol/clientCapabilities";

    // The headers of the HTTP transport.
    public const string ProtocolVersionHeader = "MCP-Protocol-Version";
    public const string SessionIdHeader = "Mcp-Session-Id";
    public const string MethodHeader = "Mcp-Method";
    public const string NameHeader = "Mcp-Name";
    public const string ParamHeaderPrefix = "Mcp-Param-";

    /// <summary>The version of the Assistant, as it tells a server.</summary>
    public static string ClientVersion { get; } =
        typeof(McpProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] is { Length: > 0 } version
            ? version
            : "0.1.0";

    /// <summary>Whether <paramref name="code"/> is one of the codes only a server of the modern era gives: its answer to a request of the modern era that it did not accept.</summary>
    public static bool IsModernErrorCode(int code) =>
        code is HeaderMismatch or MissingRequiredClientCapability or UnsupportedProtocolVersion;

    /// <summary>Whether <paramref name="version"/> is a legacy revision the Assistant speaks.</summary>
    public static bool IsKnownLegacyVersion(string? version) => version is not null && LegacyVersions.Contains(version, StringComparer.Ordinal);

    /// <summary>The Assistant's identity, as <c>clientInfo</c>.</summary>
    public static JsonObject ClientInfo() => new() { ["name"] = ClientName, ["version"] = ClientVersion };

    /// <summary>The <c>_meta</c> every request of the modern era carries: the version spoken, who is speaking, and no capabilities.</summary>
    public static JsonObject ModernMeta() => new()
    {
        [MetaProtocolVersion] = ModernVersion,
        [MetaClientInfo] = ClientInfo(),
        [MetaClientCapabilities] = new JsonObject(),
    };
}
