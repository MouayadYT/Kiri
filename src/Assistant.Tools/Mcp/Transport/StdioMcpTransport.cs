using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Assistant.Tools.Mcp;

/// <summary>
/// The MCP stdio transport: the Assistant starts the server's program and speaks to it over its standard input and output, one JSON message
/// per line. The program is started directly (never through a shell) with the arguments it was given one by one, with a small environment of
/// its own (<see cref="McpLaunchEnvironment"/>) and in a job object, so it ends with the Assistant even if the Assistant is killed. What the
/// program writes to its error stream is read and thrown away, unlooked at, so that it can never fill a pipe and never reaches a log. A line
/// from the program that is not a JSON-RPC message is ignored; one longer than the limit ends the connection.
/// </summary>
internal sealed class StdioMcpTransport : IMcpTransport
{
    private static readonly byte[] LineFeed = [(byte)'\n'];
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    private readonly McpLaunch _launch;
    private readonly int _maxMessageBytes;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private McpProcessJob? _job;
    private Stream? _input;
    private Task _reading = Task.CompletedTask;
    private Task _draining = Task.CompletedTask;
    private volatile bool _closed;
    private int _disposed;
    private int _closedRaised;

    /// <summary>Creates the transport for <paramref name="launch"/>. Nothing is started until <see cref="StartAsync"/>.</summary>
    public StdioMcpTransport(McpLaunch launch, McpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(options);
        _launch = launch;
        _maxMessageBytes = options.MaxMessageBytes;
    }

    /// <inheritdoc/>
    public McpTransportKind Kind => McpTransportKind.Stdio;

    /// <inheritdoc/>
    public string? ProtocolVersion { get; set; }

    /// <inheritdoc/>
    public event Action<JsonRpcMessage>? NotificationReceived;

    /// <inheritdoc/>
    public event Action? Closed;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_launch.Command))
        {
            throw new McpException(McpFailure.LaunchFailed);
        }

        var info = new ProcessStartInfo
        {
            FileName = _launch.Command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (_launch.WorkingDirectory is { } directory)
        {
            info.WorkingDirectory = directory;
        }

        foreach (var argument in _launch.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var (name, value) in McpLaunchEnvironment.Build(Environment.GetEnvironmentVariable, _launch.Environment))
        {
            info.Environment[name] = value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new McpException(McpFailure.LaunchFailed);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new McpException(McpFailure.LaunchFailed, inner: exception);
        }

        _process = process;
        _job = McpProcessJob.TryCreate();
        _job?.TryAssign(process);
        _input = process.StandardInput.BaseStream;
        _reading = Task.Run(() => ReadAsync(process.StandardOutput.BaseStream));
        _draining = Task.Run(() => DrainAsync(process.StandardError.BaseStream));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<JsonRpcMessage> RequestAsync(OutgoingMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id is not { } id)
        {
            throw new ArgumentException("A request has an id.", nameof(request));
        }

        ThrowIfClosed();
        var key = id.ToString(CultureInfo.InvariantCulture);
        var answer = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, answer))
        {
            throw new McpException(McpFailure.Protocol);
        }

        try
        {
            using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<JsonRpcMessage>)state!).TrySetCanceled(), answer);
            await WriteAsync(request.Json, cancellationToken).ConfigureAwait(false);
            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <inheritdoc/>
    public async Task NotifyAsync(OutgoingMessage notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ThrowIfClosed();
        await WriteAsync(notification.Json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closed = true;
        FailPending();
        await _stop.CancelAsync().ConfigureAwait(false);

        // The server is asked to end the way the protocol says, by closing what it reads; one that does not is ended.
        try
        {
            _input?.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Already closed.
        }

        if (_process is { } process)
        {
            try
            {
                using var grace = new CancellationTokenSource(ExitGrace);
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
            }
            catch (InvalidOperationException)
            {
                // Never started, or already gone.
            }
        }

        try
        {
            await Task.WhenAll(_reading, _draining).WaitAsync(ExitGrace).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or McpException or IOException or ObjectDisposedException)
        {
            // The streams were closed under them.
        }

        _job?.Dispose();
        _process?.Dispose();
        _stop.Dispose();
    }

    // The messages of the program, a line each, until it ends.
    private async Task ReadAsync(Stream output)
    {
        var reader = new BoundedLineReader(output, _maxMessageBytes);
        try
        {
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                foreach (var message in JsonRpcMessage.Parse(line))
                {
                    await DispatchAsync(message).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is McpException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The program ended, or sent more than a message may hold: either way nothing more is read.
        }
        finally
        {
            Ended();
        }
    }

    private async Task DispatchAsync(JsonRpcMessage message)
    {
        switch (message.Kind)
        {
            case JsonRpcKind.Response or JsonRpcKind.Error:
                if (message.IdKey is { } key && _pending.TryRemove(key, out var waiting))
                {
                    waiting.TrySetResult(message);
                }

                break;
            case JsonRpcKind.Request:
                if (JsonRpc.AnswerServerRequest(message) is { } answer)
                {
                    try
                    {
                        await WriteAsync(answer, _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is McpException or OperationCanceledException)
                    {
                        // The connection is ending.
                    }
                }

                break;
            default:
                try
                {
                    NotificationReceived?.Invoke(message);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // What listens cannot disturb the connection.
                }

                break;
        }
    }

    // What the program writes to its error stream is read and dropped: it may be anything, and it must never block the program.
    private async Task DrainAsync(Stream error)
    {
        var buffer = new byte[4096];
        try
        {
            while (await error.ReadAsync(buffer, _stop.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The program ended.
        }
    }

    // The program's output ended: nothing it was asked can be answered any more.
    private void Ended()
    {
        _closed = true;
        FailPending();
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            try
            {
                Closed?.Invoke();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // What listens cannot disturb the connection.
            }
        }
    }

    private void FailPending()
    {
        foreach (var (key, waiting) in _pending)
        {
            if (_pending.TryRemove(key, out _))
            {
                waiting.TrySetException(new McpException(McpFailure.Closed));
            }
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed || _input is null)
        {
            throw new McpException(McpFailure.Closed);
        }
    }

    private async Task WriteAsync(byte[] json, CancellationToken cancellationToken)
    {
        var input = _input ?? throw new McpException(McpFailure.Closed);
        if (json.Length > _maxMessageBytes)
        {
            throw new McpException(McpFailure.TooLarge);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            await input.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await input.WriteAsync(LineFeed, cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new McpException(McpFailure.Closed, inner: exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It ended on its own, or cannot be ended by this user: the job object still closes with the Assistant.
        }
    }
}
