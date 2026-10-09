using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Processes;

namespace Assistant.ModelHost.Generation;

/// <summary>
/// Asks the running llama.cpp server for chat completions over its UNIX socket (PROJECT_SPEC §5.6), and streams them.
/// </summary>
/// <remarks>
/// The socket is looked up for each request, since a restarted engine listens on a new one, and each request has a
/// connection of its own that closes with it: closing it is how a generation is stopped, and the engine stops working
/// on it at once. Nothing the engine sends is logged; failures carry only a code.
/// </remarks>
internal sealed class LlamaServerChatEngine : IChatEngine, IDisposable
{
    private readonly IModelProcessManager _engine;
    private readonly HttpClient _http;

    public LlamaServerChatEngine(IModelProcessManager engine)
    {
        _engine = engine;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
        };

        // A long prompt can take minutes on a CPU before the first token: only the caller decides when to give up.
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
        GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionRequest.Path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(ChatCompletionRequest.Write(request)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.ConnectionClose = true;

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await Guard(() => response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await using var events = ChatCompletionStream.ReadAsync(body, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await Guard(() => events.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(false))
        {
            yield return events.Current;
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var response = await Guard(
            () => _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            // The error's type says what went wrong; its message, which can quote the prompt, is never read.
            var error = await Guard(() => response.Content.ReadAsStringAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            throw new GenerationException(ChatCompletionStream.CodeOfError(error));
        }
    }

    // The engine is not running, went away, or broke the connection: the generation failed, unless it was cancelled.
    private static async Task<T> Guard<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is IOException or HttpRequestException or SocketException or OperationCanceledException)
        {
            throw new GenerationException(ModelHostErrorCode.GenerationFailed, exception);
        }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var path = _engine.SocketPath ?? throw new IOException("The model engine is not running.");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
