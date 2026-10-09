using System.Text.Json;

namespace Assistant.Core.Ipc;

/// <summary>What an entry point asks the running app to do (PROJECT_SPEC §5.7: invocation requests only).</summary>
public enum InvocationAction
{
    /// <summary>Open the floating conversation with the files attached, to ask about them (PROJECT_SPEC §4.4, Ask about).</summary>
    AskAboutFiles = 0,

    /// <summary>Take the text selected in a browser, with its page's title and address, to ask about it (PROJECT_SPEC §4.5, Selected-text actions).</summary>
    AskAboutBrowserSelection = 1,

    /// <summary>
    /// Show the Assistant's full window: what the app is asked when the user opens it while it is already running, so that opening it always shows
    /// it, and there is only ever one of it.
    /// </summary>
    ShowFullView = 2,
}

/// <summary>Why the app did not take an invocation request. Never holds anything from the request.</summary>
public enum InvocationErrorCode
{
    /// <summary>The request was not a request of this protocol: not JSON, a field missing or of the wrong kind, an empty or overlong path.</summary>
    Malformed = 0,

    /// <summary>The request was written for another version of the protocol.</summary>
    UnsupportedProtocolVersion = 1,

    /// <summary>The app does not know the action.</summary>
    UnknownAction = 2,

    /// <summary>The request names more paths than one request may (<see cref="InvocationProtocol.MaxPaths"/>).</summary>
    TooManyFiles = 3,

    /// <summary>The app cannot take requests now, such as while it is shutting down.</summary>
    Unavailable = 4,
}

/// <summary>
/// An invocation request: an action and what it is for, which is the full paths of files for <see cref="InvocationAction.AskAboutFiles"/>
/// and a <see cref="BrowserSelection"/> for <see cref="InvocationAction.AskAboutBrowserSelection"/>.
/// </summary>
public sealed record InvocationRequest(InvocationAction Action, IReadOnlyList<string> Paths, BrowserSelection? Selection = null)
{
    /// <summary>The request to show the full window.</summary>
    public static InvocationRequest ShowFullView { get; } = new(InvocationAction.ShowFullView, []);

    /// <summary>The request to ask about the text selected in a browser.</summary>
    public static InvocationRequest ForBrowserSelection(BrowserSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new InvocationRequest(InvocationAction.AskAboutBrowserSelection, [], selection);
    }
}

/// <summary>The app's answer to an <see cref="InvocationRequest"/>: taken, or refused with a code.</summary>
public sealed record InvocationReply(InvocationErrorCode? Error)
{
    /// <summary>The request was taken. What the app does with the files is up to it and is not reported back.</summary>
    public static InvocationReply Accepted { get; } = new((InvocationErrorCode?)null);

    /// <summary>Whether the request was taken.</summary>
    public bool IsAccepted => Error is null;
}

/// <summary>
/// Version 1 of the protocol on the app's pipe (<see cref="AppPipe"/>, PROJECT_SPEC §5.7): an entry point connects, sends one
/// request in one frame (<see cref="IpcFraming"/>) and reads one reply, then the connection ends. Each is a JSON envelope:
/// <c>{"v":1,"type":"askAboutFiles","body":{"paths":["C:\\…"]}}</c> or, from the browser's native-messaging host,
/// <c>{"v":1,"type":"askAboutBrowserSelection","body":{"selectionText":"…","selectionTruncated":false,"pageTitle":"…","pageUrl":"…","browserName":"…"}}</c>,
/// or, from the app itself when it is opened while it is already running, <c>{"v":1,"type":"showFullView"}</c>,
/// answered by <c>{"v":1,"type":"accepted"}</c> or <c>{"v":1,"type":"error","body":{"code":"tooManyFiles"}}</c>. Readers ignore
/// fields they do not know, and an app that does not know a request's type answers <c>unknownAction</c>. The JSON is written
/// and read by hand, without reflection, so the entry points start quickly.
/// </summary>
public static class InvocationProtocol
{
    /// <summary>The protocol version this build speaks.</summary>
    public const int Version = 1;

    /// <summary>
    /// The largest frame either side accepts, in bytes: room for the most files, each at the longest path written with every
    /// character escaped (six bytes each).
    /// </summary>
    public const int MaxFrameLength = 2 * 1024 * 1024;

    /// <summary>The most files an invocation uses (PROJECT_SPEC §4.4: at most 10 files per invocation); the rest are reported as left out.</summary>
    public const int MaxFiles = 10;

    /// <summary>
    /// The most paths one request may name: a whole selection in File Explorer, which is sent as it is so that what is left out can
    /// be said (<see cref="MaxFiles"/> of them are used). A selection of more is cut to this by the sender.
    /// </summary>
    public const int MaxPaths = 256;

    /// <summary>The longest path accepted, in UTF-16 characters: the longest a Windows path can be.</summary>
    public const int MaxPathLength = 32_767;

    private const string AskAboutFilesType = "askAboutFiles";
    private const string AskAboutBrowserSelectionType = "askAboutBrowserSelection";
    private const string ShowFullViewType = "showFullView";
    private const string AcceptedType = "accepted";
    private const string ErrorType = "error";

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 8 };

    /// <summary>Writes <paramref name="request"/> as a frame's payload.</summary>
    public static byte[] EncodeRequest(InvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Action == InvocationAction.AskAboutBrowserSelection)
        {
            return EncodeBrowserSelection(request.Selection
                ?? throw new ArgumentException("A browser selection request holds the selection.", nameof(request)));
        }

        if (request.Action == InvocationAction.ShowFullView)
        {
            return Write(writer => writer.WriteString("type", ShowFullViewType));
        }

        if (request.Action != InvocationAction.AskAboutFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Action, "The action is not part of the protocol.");
        }

        return Write(writer =>
        {
            writer.WriteString("type", AskAboutFilesType);
            writer.WriteStartObject("body");
            writer.WriteStartArray("paths");
            foreach (var path in request.Paths)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Reads a request from a frame's payload, checking its shape and limits. The paths are only checked as strings: whether
    /// they name files is for whoever acts on them.
    /// </summary>
    /// <returns><see langword="true"/> with the request, or <see langword="false"/> with why it was refused.</returns>
    public static bool TryDecodeRequest(ReadOnlySpan<byte> payload, out InvocationRequest? request, out InvocationErrorCode error)
    {
        request = null;
        error = InvocationErrorCode.Malformed;
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
                error = InvocationErrorCode.UnsupportedProtocolVersion;
                return false;
            }

            switch (type.GetString())
            {
                case AskAboutFilesType:
                    return TryReadFiles(root, out request, out error);
                case AskAboutBrowserSelectionType:
                    return TryReadBrowserSelection(root, out request);
                case ShowFullViewType:
                    request = InvocationRequest.ShowFullView;
                    return true;
                default:
                    error = InvocationErrorCode.UnknownAction;
                    return false;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Not JSON, or a string that is not text (an unpaired surrogate written as an escape).
            return false;
        }
    }

    private static bool TryReadFiles(JsonElement root, out InvocationRequest? request, out InvocationErrorCode error)
    {
        request = null;
        error = InvocationErrorCode.Malformed;
        if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var count = paths.GetArrayLength();
        if (count == 0)
        {
            return false;
        }

        if (count > MaxPaths)
        {
            error = InvocationErrorCode.TooManyFiles;
            return false;
        }

        var read = new List<string>(count);
        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.String || path.GetString() is not { Length: > 0 and <= MaxPathLength } text)
            {
                return false;
            }

            read.Add(text);
        }

        request = new InvocationRequest(InvocationAction.AskAboutFiles, read);
        return true;
    }

    private static bool TryReadBrowserSelection(JsonElement root, out InvocationRequest? request)
    {
        request = null;
        if (!BrowserSelectionJson.TryReadBody(root, out var selection))
        {
            return false;
        }

        request = InvocationRequest.ForBrowserSelection(selection!);
        return true;
    }

    private static byte[] EncodeBrowserSelection(BrowserSelection selection) => Write(writer =>
    {
        writer.WriteString("type", AskAboutBrowserSelectionType);
        BrowserSelectionJson.WriteBody(writer, selection);
    });

    /// <summary>Writes <paramref name="reply"/> as a frame's payload.</summary>
    public static byte[] EncodeReply(InvocationReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return Write(writer =>
        {
            if (reply.Error is not { } code)
            {
                writer.WriteString("type", AcceptedType);
                return;
            }

            writer.WriteString("type", ErrorType);
            writer.WriteStartObject("body");
            writer.WriteString("code", CodeName(code));
            writer.WriteEndObject();
        });
    }

    /// <summary>Reads a reply from a frame's payload.</summary>
    /// <exception cref="IpcProtocolException">The payload is not a reply of this protocol, or names an unknown error code.</exception>
    public static InvocationReply DecodeReply(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray(), ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("v", out var version) && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var number) && number == Version
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                switch (type.GetString())
                {
                    case AcceptedType:
                        return InvocationReply.Accepted;
                    case ErrorType when root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object
                        && body.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                        && ParseCode(code.GetString()) is { } error:
                        return new InvocationReply(error);
                }
            }
        }
        catch (JsonException)
        {
        }

        throw new IpcProtocolException("The app's reply is not a reply of the invocation protocol.");
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

    private static string CodeName(InvocationErrorCode code) => code switch
    {
        InvocationErrorCode.Malformed => "malformed",
        InvocationErrorCode.UnsupportedProtocolVersion => "unsupportedProtocolVersion",
        InvocationErrorCode.UnknownAction => "unknownAction",
        InvocationErrorCode.TooManyFiles => "tooManyFiles",
        InvocationErrorCode.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    private static InvocationErrorCode? ParseCode(string? name) => name switch
    {
        "malformed" => InvocationErrorCode.Malformed,
        "unsupportedProtocolVersion" => InvocationErrorCode.UnsupportedProtocolVersion,
        "unknownAction" => InvocationErrorCode.UnknownAction,
        "tooManyFiles" => InvocationErrorCode.TooManyFiles,
        "unavailable" => InvocationErrorCode.Unavailable,
        _ => null,
    };
}
