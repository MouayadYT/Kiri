using System.Text.Json.Serialization;

namespace Assistant.Tools.Mcp;

/// <summary>The ways the Assistant can speak to an MCP server. Which one an integration uses is stored with it; callers only ever see <see cref="IMcpClient"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<McpTransportKind>))]
public enum McpTransportKind
{
    /// <summary>A local program started by the Assistant, spoken to over its standard input and output (one JSON message per line).</summary>
    Stdio = 0,

    /// <summary>
    /// A remote or local HTTP server (MCP "Streamable HTTP"): each message is a POST, the answer is JSON or an event stream. It also covers
    /// servers of the earlier protocol versions that use the same transport; when a server turns out to speak only the older HTTP+SSE
    /// transport the connection falls back to <see cref="LegacySse"/>.
    /// </summary>
    StreamableHttp = 1,

    /// <summary>The older HTTP+SSE transport (protocol version 2024-11-05), deprecated by the protocol but still what some servers speak.</summary>
    LegacySse = 2,
}

/// <summary>Why a request to an MCP server did not give an answer. A code, so that nothing the server or the user said has to be put in a log or a message.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<McpFailure>))]
public enum McpFailure
{
    /// <summary>The Assistant refused to connect: the integration is disabled, or Local Only mode is on and it would send something off this PC.</summary>
    Blocked = 0,

    /// <summary>The integration's record cannot be used (no address, no program, an address or program that the rules refuse).</summary>
    NotConfigured = 1,

    /// <summary>The program could not be started.</summary>
    LaunchFailed = 2,

    /// <summary>The server could not be reached.</summary>
    ConnectFailed = 3,

    /// <summary>The connection ended: the program exited, or the server closed the stream.</summary>
    Closed = 4,

    /// <summary>The server did not answer in time.</summary>
    TimedOut = 5,

    /// <summary>The server wants a sign-in (HTTP 401) that the Assistant does not have.</summary>
    AuthRequired = 6,

    /// <summary>The server refused the Assistant (HTTP 403).</summary>
    Forbidden = 7,

    /// <summary>The server answered with an HTTP error.</summary>
    HttpError = 8,

    /// <summary>The server sent something that is not a valid MCP message, or one that makes no sense where it came.</summary>
    Protocol = 9,

    /// <summary>The server speaks no protocol version, transport or result type this version of the Assistant understands.</summary>
    Unsupported = 10,

    /// <summary>The server answered a request with an error.</summary>
    Server = 11,

    /// <summary>A message, a list or a result was larger than the Assistant accepts.</summary>
    TooLarge = 12,

    /// <summary>The server's session ended (HTTP 404 on a request that carried one).</summary>
    SessionExpired = 13,
}

/// <summary>
/// An MCP request failed. <see cref="Exception.Message"/> says only what kind of failure it was, in the Assistant's own words: what the
/// server said is in <see cref="ServerMessage"/> and is private content (PROJECT_SPEC §3.2), never to be logged. The code that reads
/// the exception decides what the model or the user is told.
/// </summary>
public sealed class McpException : Exception
{
    /// <summary>Creates the exception.</summary>
    public McpException(McpFailure failure, int? httpStatus = null, int? rpcCode = null, string? serverMessage = null, Exception? inner = null)
        : base(Describe(failure), inner)
    {
        Failure = failure;
        HttpStatus = httpStatus;
        RpcCode = rpcCode;
        ServerMessage = serverMessage;
    }

    /// <summary>What kind of failure it was.</summary>
    public McpFailure Failure { get; }

    /// <summary>The HTTP status that went with it, when there was one.</summary>
    public int? HttpStatus { get; }

    /// <summary>The JSON-RPC error code the server gave, when it gave one.</summary>
    public int? RpcCode { get; }

    /// <summary>
    /// What the server said about it (an error message), cut to a reasonable length; <see langword="null"/> when it said nothing. It is third-party
    /// text: shown to the model only as data, and never logged.
    /// </summary>
    public string? ServerMessage { get; }

    /// <summary>
    /// For <see cref="McpFailure.AuthRequired"/> over HTTP: whether the server pointed to its sign-in information (a
    /// <c>resource_metadata</c> in <c>WWW-Authenticate</c>), which tells an OAuth server from one that wants a key.
    /// </summary>
    public bool OffersSignIn { get; init; }

    private static string Describe(McpFailure failure) => failure switch
    {
        McpFailure.Blocked => "The connection was not allowed.",
        McpFailure.NotConfigured => "The integration is not set up so that it can be reached.",
        McpFailure.LaunchFailed => "The integration's program could not be started.",
        McpFailure.ConnectFailed => "The integration could not be reached.",
        McpFailure.Closed => "The connection to the integration ended.",
        McpFailure.TimedOut => "The integration did not answer in time.",
        McpFailure.AuthRequired => "The integration needs a sign-in.",
        McpFailure.Forbidden => "The integration refused the request.",
        McpFailure.HttpError => "The integration answered with an error.",
        McpFailure.Protocol => "The integration sent something that is not a valid message.",
        McpFailure.Unsupported => "The integration uses a protocol version or transport that is not supported.",
        McpFailure.Server => "The integration reported an error.",
        McpFailure.TooLarge => "The integration sent more than is accepted.",
        McpFailure.SessionExpired => "The integration's session ended.",
        _ => "The integration failed.",
    };
}
