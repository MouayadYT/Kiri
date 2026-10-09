using System.Text.Json;
using Assistant.Core.Ipc;

namespace Assistant.BrowserBridge.Protocol;

/// <summary>What a message from the extension asks the host to do.</summary>
internal enum BrowserMessageKind
{
    /// <summary>Check that the host is there and speaks this version; nothing is forwarded.</summary>
    Ping = 0,

    /// <summary>Take the text selected in the browser to the app.</summary>
    Selection = 1,
}

/// <summary>Why the host did not carry out a message. Never holds anything from the message.</summary>
internal enum BrowserMessageError
{
    /// <summary>The message was not a message of this protocol: not JSON, a field missing or of the wrong kind, no text, a limit passed.</summary>
    Malformed = 0,

    /// <summary>The message was written for another version of the protocol.</summary>
    UnsupportedProtocolVersion = 1,

    /// <summary>The host does not know the message's type.</summary>
    UnknownType = 2,

    /// <summary>The app is not running and could not be started: it is not where the host expects it.</summary>
    AppNotFound = 3,

    /// <summary>The app did not answer in time, or the connection to it broke.</summary>
    AppDidNotRespond = 4,

    /// <summary>The app answered and refused the selection, or answered with something this build does not understand.</summary>
    AppRefused = 5,
}

/// <summary>A message from the extension, read: what it asks, and for a selection the selection.</summary>
internal sealed record BrowserMessage(BrowserMessageKind Kind, BrowserSelection? Selection);

/// <summary>
/// Version 1 of the protocol between the browser extension and the native-messaging host (PROJECT_SPEC §4.5, §5.7), carried over the
/// host's standard input and output as the browser frames it (a 4-byte length, then UTF-8 JSON; <see cref="IpcFraming"/> reads and
/// writes the same frames). Each message is a JSON envelope with the version and a type:
/// <c>{"v":1,"type":"selection","body":{"selectionText":"…","selectionTruncated":false,"pageTitle":"…","pageUrl":"…","browserName":"…"}}</c>, answered
/// by <c>{"v":1,"type":"accepted"}</c> once the app has taken it, or <c>{"v":1,"type":"error","body":{"code":"appNotFound"}}</c>; and
/// <c>{"v":1,"type":"ping"}</c>, answered by <c>{"v":1,"type":"pong"}</c>. Readers ignore fields they do not know. An error never
/// holds text from the message.
/// </summary>
internal static class BrowserMessageProtocol
{
    /// <summary>The protocol version this build speaks.</summary>
    public const int Version = 1;

    /// <summary>
    /// The largest message the host reads, in bytes: the app pipe's own limit, which holds a selection of the most characters with every
    /// one of them written as an escape.
    /// </summary>
    public const int MaxMessageLength = InvocationProtocol.MaxFrameLength;

    private const string SelectionType = "selection";
    private const string PingType = "ping";
    private const string AcceptedType = "accepted";
    private const string PongType = "pong";
    private const string ErrorType = "error";

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 8 };

    /// <summary>Reads a message from a frame's payload, checking its shape and limits.</summary>
    /// <returns><see langword="true"/> with the message, or <see langword="false"/> with why it was refused.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out BrowserMessage? message, out BrowserMessageError error)
    {
        message = null;
        error = BrowserMessageError.Malformed;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray(), ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!version.TryGetInt32(out var number) || number != Version)
            {
                error = BrowserMessageError.UnsupportedProtocolVersion;
                return false;
            }

            switch (type.GetString())
            {
                case PingType:
                    message = new BrowserMessage(BrowserMessageKind.Ping, null);
                    return true;
                case SelectionType:
                    return TryReadSelection(root, out message);
                default:
                    error = BrowserMessageError.UnknownType;
                    return false;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Not JSON, or a string that is not text (an unpaired surrogate written as an escape).
            return false;
        }
    }

    /// <summary>Writes the message the extension sends for <paramref name="selection"/> (the extension builds it in JavaScript; this is for tests and tools).</summary>
    public static byte[] EncodeSelection(BrowserSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Write(writer =>
        {
            writer.WriteString("type", SelectionType);
            BrowserSelectionJson.WriteBody(writer, selection);
        });
    }

    /// <summary>Writes a ping.</summary>
    public static byte[] EncodePing() => Write(writer => writer.WriteString("type", PingType));

    /// <summary>Writes the reply to a selection the app took.</summary>
    public static byte[] EncodeAccepted() => Write(writer => writer.WriteString("type", AcceptedType));

    /// <summary>Writes the reply to a ping.</summary>
    public static byte[] EncodePong() => Write(writer => writer.WriteString("type", PongType));

    /// <summary>Writes the reply that says why a message was not carried out.</summary>
    public static byte[] EncodeError(BrowserMessageError error) => Write(writer =>
    {
        writer.WriteString("type", ErrorType);
        writer.WriteStartObject("body");
        writer.WriteString("code", CodeName(error));
        writer.WriteEndObject();
    });

    /// <summary>The name of <paramref name="error"/> on the wire.</summary>
    public static string CodeName(BrowserMessageError error) => error switch
    {
        BrowserMessageError.Malformed => "malformed",
        BrowserMessageError.UnsupportedProtocolVersion => "unsupportedProtocolVersion",
        BrowserMessageError.UnknownType => "unknownType",
        BrowserMessageError.AppNotFound => "appNotFound",
        BrowserMessageError.AppDidNotRespond => "appDidNotRespond",
        BrowserMessageError.AppRefused => "appRefused",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };

    private static bool TryReadSelection(JsonElement root, out BrowserMessage? message)
    {
        message = null;
        if (!BrowserSelectionJson.TryReadBody(root, out var selection))
        {
            return false;
        }

        message = new BrowserMessage(BrowserMessageKind.Selection, selection);
        return true;
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
