using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Integrations;

/// <summary>What a program the installer ran for a setup step did.</summary>
/// <param name="ExitCode">What it exited with; -1 when it did not exit by itself.</param>
/// <param name="TimedOut">Whether it was ended because it took too long.</param>
/// <param name="StartFailed">Whether it could not be started.</param>
internal sealed record ProcessResult(int ExitCode, bool TimedOut, bool StartFailed)
{
    /// <summary>Whether it exited with success.</summary>
    public bool Succeeded => !TimedOut && !StartFailed && ExitCode == 0;
}

/// <summary>A program to run for a setup step: always a program the installer set up itself, with its arguments one by one.</summary>
/// <param name="FileName">The full path of the program.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="WorkingDirectory">Where it runs.</param>
/// <param name="Environment">What it is given besides the few variables Windows programs need.</param>
/// <param name="Timeout">The most time it is given.</param>
internal sealed record ProcessSpec(
    string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout);

/// <summary>Runs a program for a setup step (installing a package's dependencies, making a Python environment).</summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="spec"/> to the end. What the program writes is read and thrown away (it never reaches a log, a message or the user), so it
    /// cannot fill a pipe. It runs in a job object, so it ends with the Assistant, and a program that takes longer than its time is ended with everything
    /// it started.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the program was ended.</exception>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the setup programs of an installation (<c>node npm-cli.js install …</c>, <c>python -m pip install …</c>) directly, never through a shell, with an
/// environment of its own (<see cref="McpLaunchEnvironment"/>: nothing of the Assistant's is passed on), without a window, and in a job object.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <inheritdoc/>
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var info = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var (name, value) in McpLaunchEnvironment.Build(System.Environment.GetEnvironmentVariable, spec.Environment))
        {
            info.Environment[name] = value;
        }

        using var process = new Process { StartInfo = info };
        using var job = McpProcessJob.TryCreate();
        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, false, true);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessResult(-1, false, true);
        }

        job?.TryAssign(process);
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // It has gone already.
        }

        // What it writes is read and dropped.
        var draining = Task.WhenAll(Drain(process.StandardOutput.BaseStream), Drain(process.StandardError.BaseStream));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(spec.Timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await draining.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, false, false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(-1, true, false);
        }
        catch (TimeoutException)
        {
            return new ProcessResult(process.HasExited ? process.ExitCode : -1, false, false);
        }
    }

    private static async Task Drain(Stream stream)
    {
        var buffer = new byte[8192];
        try
        {
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The program ended.
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
            // It ended by itself first.
        }
    }
}
