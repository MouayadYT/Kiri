using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using Assistant.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// The model host as a process the app owns (PROJECT_SPEC §5.6). <see cref="StartAsync"/> launches the hidden host
/// executable on a pipe named for that launch, connects and pings it. Disposing asks the host to shut down, and ends
/// the process if it does not exit in time.
/// </summary>
/// <remarks>
/// The host never outlives its owner: it exits when this connection closes, and also when the owning process exits,
/// even if it crashed before disposing this.
/// </remarks>
public sealed partial class ModelHostProcess : IModelHostConnection
{
    /// <summary>The host executable's file name.</summary>
    public const string ExecutableName = "Assistant.ModelHost.exe";

    private readonly Process _process;
    private readonly ModelHostLaunchOptions _options;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private ModelHostProcess(Process process, ModelHostClient client, ModelHostLaunchOptions options, ILogger logger)
    {
        _process = process;
        _options = options;
        _logger = logger;
        ProcessId = process.Id;
        Client = client;

        // The exit is kept, so it can still be read once the process object is disposed.
        process.Exited += (_, _) => KeepExit();
        process.EnableRaisingEvents = true;
        if (process.HasExited)
        {
            KeepExit();
        }
    }

    /// <summary>The host's process id.</summary>
    public int ProcessId { get; }

    /// <summary>The connection to the host.</summary>
    public ModelHostClient Client { get; }

    /// <summary>Whether the host process has exited.</summary>
    public bool HasExited => _exit.Task.IsCompleted;

    /// <summary>The host's exit code once it has exited: 0 when it stopped as asked or because its owner left.</summary>
    public int? ExitCode => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null;

    /// <summary>Starts the host, connects to it and pings it.</summary>
    /// <exception cref="ModelHostException">
    /// The host could not be started, exited early, did not answer within
    /// <see cref="ModelHostLaunchOptions.StartTimeout"/>, or answered with an error. The process has been ended.
    /// </exception>
    public static async Task<ModelHostProcess> StartAsync(
        ModelHostLaunchOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (!Path.IsPathFullyQualified(options.ExecutablePath))
        {
            throw new ArgumentException("The model host's path must be fully qualified.", nameof(options));
        }

        var logger = loggerFactory.CreateLogger<ModelHostProcess>();
        var start = Stopwatch.GetTimestamp();
        var arguments = new ModelHostArguments(
            LocalPipe.CreateUniqueName(ModelHostProtocol.PipeNamePrefix), Environment.ProcessId);

        Process process;
        try
        {
            process = Launch(options.ExecutablePath, arguments);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            LogStartFailed(logger, exception);
            throw new ModelHostException("The model host could not be started.", exception);
        }

        ModelHostClient? client = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.StartTimeout);

            var pipe = await ConnectAsync(process, arguments.PipeName, timeout.Token).ConfigureAwait(false);
            client = new ModelHostClient(pipe, loggerFactory.CreateLogger<ModelHostClient>());
            var health = await client.PingAsync(timeout.Token).ConfigureAwait(false);
            if (health.ProcessId != process.Id)
            {
                throw new ModelHostException("A process other than the model host answered on its pipe.");
            }

            LogStarted(logger, process.Id, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return new ModelHostProcess(process, client, options, logger);
        }
        catch (Exception exception)
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            End(process);
            process.Dispose();

            var callerCancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!callerCancelled)
            {
                LogStartFailed(logger, exception);
            }

            if (callerCancelled || exception is ModelHostException)
            {
                throw;
            }

            throw exception is OperationCanceledException
                ? new ModelHostException("The model host did not answer in time.", exception)
                : new ModelHostException("The model host could not be reached.", exception);
        }
    }

    /// <summary>Waits until the host process has exited.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Asks the host to shut down and waits for it to exit, up to <see cref="ModelHostLaunchOptions.ShutdownTimeout"/>;
    /// then ends it if it is still running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!HasExited)
        {
            using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
            try
            {
                await Client.ShutdownAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ModelHostException or OperationCanceledException)
            {
                // Also when the connection has closed already, in which case the host is exiting on its own.
                LogShutdownFailed(_logger, exception);
            }

            try
            {
                await _exit.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // It did not exit in time, and is ended below.
            }
        }

        await Client.DisposeAsync().ConfigureAwait(false);
        if (!HasExited)
        {
            End(_process);
            LogEnded(_logger, ProcessId);
            await Task.WhenAny(_exit.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        if (ExitCode is { } exitCode)
        {
            LogExited(_logger, ProcessId, exitCode);
        }

        _process.Dispose();
    }

    private void KeepExit()
    {
        try
        {
            _exit.TrySetResult(_process.ExitCode);
        }
        catch (InvalidOperationException)
        {
            // The process object was disposed first: the host has exited, with no code to report.
            _exit.TrySetCanceled();
        }
    }

    private static Process Launch(string executablePath, ModelHostArguments arguments)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
        };
        foreach (var argument in arguments.ToCommandLine())
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("No process was started.");
    }

    // Connects once the host has created its pipe, unless the host exits first.
    private static async Task<NamedPipeClientStream> ConnectAsync(
        Process process,
        string pipeName,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, LocalPipe.Options);
        try
        {
            using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var connect = pipe.ConnectAsync(stopWaiting.Token);
            var exit = process.WaitForExitAsync(stopWaiting.Token);
            var first = await Task.WhenAny(connect, exit).ConfigureAwait(false);
            stopWaiting.Cancel();

            if (first == exit && exit.IsCompletedSuccessfully)
            {
                await Task.WhenAny(connect).ConfigureAwait(false);
                throw new ModelHostException("The model host exited before it could be reached.");
            }

            await connect.ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void End(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // It has already exited, or it cannot be ended; either way nothing is left to do.
        }
    }

    [LoggerMessage(EventId = 2200, Level = LogLevel.Information, Message = "Model host started as process {ProcessId} and answered the ping after {ElapsedMs} ms")]
    private static partial void LogStarted(ILogger logger, int processId, long elapsedMs);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Error, Message = "Model host could not be started")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Information, Message = "Model host process {ProcessId} exited with code {ExitCode}")]
    private static partial void LogExited(ILogger logger, int processId, int exitCode);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning, Message = "Model host did not shut down when asked")]
    private static partial void LogShutdownFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2204, Level = LogLevel.Warning, Message = "Model host process {ProcessId} was ended because it did not exit")]
    private static partial void LogEnded(ILogger logger, int processId);
}
