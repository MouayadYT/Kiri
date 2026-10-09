using System.ComponentModel;
using System.Diagnostics;

namespace Assistant.ModelHost.Processes;

/// <summary>
/// One run of the llama.cpp server: its process, its socket, and what its output has said so far. The output is read
/// to the end as it comes, so the server never blocks on a full pipe, and each line is reduced by
/// <see cref="LlamaServerOutput"/> before anything else sees it.
/// </summary>
internal sealed class EngineProcess
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputWait = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly TimeProvider _timeProvider;
    private readonly Action<EngineProcess, LlamaServerSignal> _onSignal;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _started;
    private readonly object _endLock = new();
    private Task _output = Task.CompletedTask;
    private Task? _end;
    private long _exited;
    private int _errorCount;
    private int _warningCount;
    private int _reportedFailure = -1;
    private int _contextTokens;

    private EngineProcess(
        Process process,
        string socketPath,
        TimeProvider timeProvider,
        Action<EngineProcess, LlamaServerSignal> onSignal)
    {
        _process = process;
        _timeProvider = timeProvider;
        _onSignal = onSignal;
        _started = timeProvider.GetTimestamp();
        SocketPath = socketPath;
    }

    /// <summary>The server's process id.</summary>
    public int ProcessId { get; private set; }

    /// <summary>The UNIX socket the server listens on, removed when this run ends.</summary>
    public string SocketPath { get; }

    /// <summary>Completes when the server has loaded its model and listens.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Completes with the exit code when the process has exited.</summary>
    public Task<int> Exited => _exit.Task;

    /// <summary>How long the process ran, or has run so far.</summary>
    public TimeSpan Uptime =>
        Interlocked.Read(ref _exited) is var exited and not 0
            ? _timeProvider.GetElapsedTime(_started, exited)
            : _timeProvider.GetElapsedTime(_started);

    /// <summary>Errors the server printed that no other signal explains.</summary>
    public int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>Warnings the server printed.</summary>
    public int WarningCount => Volatile.Read(ref _warningCount);

    /// <summary>The context, in tokens, that each of the server's slots was started with, or 0 until it has said.</summary>
    public int ContextTokens => Volatile.Read(ref _contextTokens);

    /// <summary>
    /// The fatal problem the server reported before it exited, if any: <see cref="LlamaServerSignalKind.ModelLoadFailed"/>
    /// or <see cref="LlamaServerSignalKind.EndpointFailed"/>.
    /// </summary>
    public LlamaServerSignalKind? ReportedFailure =>
        Volatile.Read(ref _reportedFailure) is var failure and >= 0 ? (LlamaServerSignalKind)failure : null;

    /// <summary>Launches the server and starts reading its output.</summary>
    /// <param name="startInfo">How to launch it (<see cref="LlamaServerCommand"/>).</param>
    /// <param name="socketPath">The socket it was told to listen on.</param>
    /// <param name="timeProvider">The clock for its uptime.</param>
    /// <param name="onSignal">Called for each understood line, on a thread that reads the output.</param>
    /// <exception cref="Win32Exception">Windows could not start the executable.</exception>
    public static EngineProcess Start(
        ProcessStartInfo startInfo,
        string socketPath,
        TimeProvider timeProvider,
        Action<EngineProcess, LlamaServerSignal> onSignal)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var engine = new EngineProcess(process, socketPath, timeProvider, onSignal);
        process.Exited += (_, _) => engine.KeepExit();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("No process was started.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        engine.ProcessId = process.Id;

        // The server reads nothing; its input is closed so it never waits on one.
        process.StandardInput.Close();
        engine._output = Task.WhenAll(
            engine.PumpAsync(process.StandardOutput),
            engine.PumpAsync(process.StandardError));
        return engine;
    }

    /// <summary>Puts the process in <paramref name="job"/>, so it ends with the host.</summary>
    /// <exception cref="Win32Exception">It could not be assigned.</exception>
    public void AssignTo(ProcessJob job) => job.Assign(_process);

    /// <summary>
    /// Waits until the output has been read to its end, so every line the server printed before it exited has been
    /// classified. Gives up after a moment, in case something else still holds the pipes.
    /// </summary>
    public Task WaitForOutputAsync() => Task.WhenAny(_output, Task.Delay(OutputWait));

    /// <summary>
    /// Ends the run: ends the process if it is still running, waits for it and its output, and removes its socket.
    /// Safe to call more than once and from several places; each call waits for the same end.
    /// </summary>
    public Task EndAsync()
    {
        lock (_endLock)
        {
            return _end ??= EndCoreAsync();
        }
    }

    private async Task EndCoreAsync()
    {
        if (!_exit.Task.IsCompleted)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // It has exited already, or cannot be ended; the job ends it with the host.
            }

            await Task.WhenAny(_exit.Task, Task.Delay(ExitWait)).ConfigureAwait(false);
        }

        await WaitForOutputAsync().ConfigureAwait(false);
        try
        {
            File.Delete(SocketPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A socket name is used once, so one left behind never gets in a later run's way.
        }

        _process.Dispose();
    }

    private void KeepExit()
    {
        Interlocked.Exchange(ref _exited, _timeProvider.GetTimestamp());
        try
        {
            _exit.TrySetResult(_process.ExitCode);
        }
        catch (InvalidOperationException)
        {
            _exit.TrySetResult(-1);
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (LlamaServerOutput.Classify(line) is { } signal)
                {
                    Observe(signal);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe closed with the process.
        }
    }

    private void Observe(LlamaServerSignal signal)
    {
        switch (signal.Kind)
        {
            case LlamaServerSignalKind.Listening:
                _ready.TrySetResult();
                break;
            case LlamaServerSignalKind.ModelLoadFailed or LlamaServerSignalKind.EndpointFailed:
                Interlocked.CompareExchange(ref _reportedFailure, (int)signal.Kind, -1);
                break;
            case LlamaServerSignalKind.Slots:
                Volatile.Write(ref _contextTokens, signal.ContextTokens);
                break;
            case LlamaServerSignalKind.Warning:
                Interlocked.Increment(ref _warningCount);
                break;
            case LlamaServerSignalKind.Error:
                Interlocked.Increment(ref _errorCount);
                break;
        }

        _onSignal(this, signal);
    }
}
