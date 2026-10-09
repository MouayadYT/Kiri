using Assistant.BrowserBridge.Protocol;
using Assistant.Core.Ipc;

namespace Assistant.BrowserBridge.Host;

/// <summary>How <see cref="NativeMessagingHost.RunAsync"/> ended.</summary>
internal enum HostExit
{
    /// <summary>The browser closed its end after the last message, as it does when it is done.</summary>
    InputClosed = 0,

    /// <summary>The input broke the framing (a length that is zero or over the limit, or a message cut short), so it cannot be read in step.</summary>
    InputBroken = 1,

    /// <summary>The browser stopped reading the host's replies.</summary>
    OutputClosed = 2,
}

/// <summary>
/// The message loop of the native-messaging host (PROJECT_SPEC §4.5, §5.7): reads the extension's messages from the standard input,
/// one at a time, and answers each on the standard output (<see cref="BrowserMessageProtocol"/>). A selection is handed to
/// <c>forward</c>, which takes it to the running app (starting the app when it is not running) and says how that ended; a ping is
/// answered at once. Nothing from a message is written anywhere else: the host keeps no log, and a reply holds no text of the message.
/// </summary>
/// <remarks>
/// The browser starts a host for each <c>sendNativeMessage</c> and ends it after the first reply, or keeps it for a
/// <c>connectNative</c> port; the loop serves both. Only whole messages are acted on: a message that cannot be read is answered with an
/// error and the loop goes on, but a broken frame ends it, because the rest of the input cannot be told apart from a message.
/// </remarks>
internal sealed class NativeMessagingHost(Func<InvocationRequest, ForwardResult> forward)
{
    /// <summary>Serves <paramref name="input"/> until it ends, writing the replies to <paramref name="output"/>.</summary>
    public async Task<HostExit> RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        while (true)
        {
            byte[]? payload;
            try
            {
                payload = await IpcFraming.ReadFrameAsync(input, BrowserMessageProtocol.MaxMessageLength, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IpcProtocolException)
            {
                return HostExit.InputBroken;
            }
            catch (IOException)
            {
                return HostExit.InputClosed;
            }

            if (payload is null)
            {
                return HostExit.InputClosed;
            }

            var reply = await AnswerAsync(payload, cancellationToken).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteFrameAsync(output, reply, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return HostExit.OutputClosed;
            }
        }
    }

    private async Task<byte[]> AnswerAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (!BrowserMessageProtocol.TryDecode(payload, out var message, out var error))
        {
            return BrowserMessageProtocol.EncodeError(error);
        }

        if (message!.Kind == BrowserMessageKind.Ping)
        {
            return BrowserMessageProtocol.EncodePong();
        }

        // Forwarding blocks (it may start the app and wait for it), so it runs off the reading thread.
        var request = InvocationRequest.ForBrowserSelection(message.Selection!);
        ForwardResult result;
        try
        {
            result = await Task.Run(() => forward(request), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            result = ForwardResult.AppDidNotRespond;
        }

        return result switch
        {
            ForwardResult.Forwarded => BrowserMessageProtocol.EncodeAccepted(),
            ForwardResult.AppNotFound => BrowserMessageProtocol.EncodeError(BrowserMessageError.AppNotFound),
            ForwardResult.AppDidNotRespond => BrowserMessageProtocol.EncodeError(BrowserMessageError.AppDidNotRespond),
            _ => BrowserMessageProtocol.EncodeError(BrowserMessageError.AppRefused),
        };
    }
}
