namespace Assistant.Core.ModelHosting;

/// <summary>
/// The versioned protocol between the desktop app and its model host, the hidden process that runs local inference
/// (PROJECT_SPEC §5.6, §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Transport.</b> One named pipe per host process, created by the host under a name the app picks for that launch
/// (<see cref="ModelHostArguments"/>), open only to the current user (<see cref="Ipc.LocalPipe"/>), with one
/// connection: the app's. Messages are frames of <see cref="Ipc.IpcFraming"/>, each holding one UTF-8 JSON envelope,
/// written by <see cref="ModelHostSerializer"/>:
/// <c>{"v":1,"id":7,"type":"health","body":{}}</c>. <c>v</c> is <see cref="Version"/>, <c>type</c> names the
/// message and <c>body</c> holds its fields in camelCase.
/// </para>
/// <para>
/// <b>Exchange.</b> The app sends <see cref="ModelHostRequest"/>s with ids that are positive and unique on the
/// connection. The host sends <see cref="ModelHostReply"/>s carrying the id of the request they answer. Every request
/// but <see cref="CancelGenerationRequest"/> gets exactly one final reply: its result, or a
/// <see cref="ModelHostError"/>. Before it, a load may send <see cref="ModelLoadProgress"/> and a generation streams
/// <see cref="TextDelta"/> and <see cref="ToolCallGenerated"/>. Requests run concurrently, so a cancel can overtake the
/// generation it stops. The first request on a connection is <see cref="HealthRequest"/>, the ping, which checks that
/// both sides speak the same version.
/// </para>
/// <para>
/// <b>Status.</b> The host also sends a <see cref="ModelStatusReport"/> on its own whenever the model's status changes
/// (loading, ready, failed, unloading, not loaded), as a reply with request id 0 that answers no request and has
/// no final reply of its own. Each carries a sequence number, so the app can tell which of two is newer.
/// </para>
/// <para>
/// <b>Versions.</b> <c>v</c> and <c>id</c> never change shape, so a peer can always answer a frame it cannot read,
/// with <see cref="ModelHostErrorCode.UnsupportedProtocolVersion"/>. Adding an optional field or a new message type
/// keeps the version: readers ignore fields they do not know and answer types they do not know with
/// <see cref="ModelHostErrorCode.UnknownMessageType"/>. A new <see cref="ModelHostErrorCode"/> keeps it too: a reader
/// that does not know the code reads the error as malformed, which fails the request just as the error would. Any
/// other change raises <see cref="Version"/>.
/// </para>
/// <para>
/// <b>Privacy.</b> Prompts, images and generated text cross the pipe but are private content (PROJECT_SPEC §3.2):
/// neither side logs them, and the messages that carry them leave them out of <c>ToString</c>.
/// </para>
/// </remarks>
public static class ModelHostProtocol
{
    /// <summary>The protocol version both sides must speak.</summary>
    public const int Version = 1;

    /// <summary>
    /// The largest frame either side accepts, in bytes. Room for a few screenshots, encoded in base64, in one
    /// <see cref="GenerateMultimodalRequest"/>.
    /// </summary>
    public const int MaxFrameLength = 64 * 1024 * 1024;

    /// <summary>What every model-host pipe name starts with.</summary>
    public const string PipeNamePrefix = "Assistant.ModelHost";
}
