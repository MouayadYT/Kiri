using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Assistant.ModelHost.Runtime;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Processes;

/// <summary>Asks the engine which devices it can offload a model to, and how much memory each has free.</summary>
internal interface IEngineDeviceProbe
{
    /// <summary>The devices of <paramref name="runtime"/>, or none when they cannot be listed.</summary>
    Task<IReadOnlyList<EngineDevice>> ListAsync(ModelRuntime runtime, CancellationToken cancellationToken);
}

/// <summary>
/// Lists the devices by running the bundled server with <c>--list-devices</c>, which prints them and exits. The
/// server gets the same clean environment as the engine (<see cref="LlamaServerCommand"/>), so the user's variables
/// cannot change what it lists. The device names it prints are read for their ids and free memory only, and never logged.
/// </summary>
internal sealed class LlamaServerDeviceProbe(ILogger<LlamaServerDeviceProbe> logger) : IEngineDeviceProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<EngineDevice>> ListAsync(ModelRuntime runtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var startInfo = new ProcessStartInfo(runtime.ServerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = runtime.Directory,
        };
        startInfo.ArgumentList.Add("--list-devices");
        LlamaServerCommand.ScrubEnvironment(startInfo);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("No process was started.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            EngineDeviceLog.ProbeFailed(logger, exception);
            return [];
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timeout);
            try
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(limit.Token);
                var errors = process.StandardError.ReadToEndAsync(limit.Token);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return EngineDevices.Parse(await output.ConfigureAwait(false) + "\n" + await errors.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                EngineDeviceLog.ProbeTimedOut(logger);
                return [];
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                EngineDeviceLog.ProbeFailed(logger, exception);
                return [];
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                    {
                        // It exited meanwhile, or cannot be ended; it lists devices and exits by itself.
                    }
                }
            }
        }
    }
}

/// <summary>The device probe's and the device choice's log: counts only, never device names.</summary>
internal static partial class EngineDeviceLog
{
    [LoggerMessage(EventId = 2360, Level = LogLevel.Warning, Message = "The engine's devices could not be listed")]
    public static partial void ProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2361, Level = LogLevel.Warning, Message = "Listing the engine's devices took too long")]
    public static partial void ProbeTimedOut(ILogger logger);

    [LoggerMessage(EventId = 2362, Level = LogLevel.Information, Message = "The engine offloads to {ChosenCount} of {AvailableCount} devices")]
    public static partial void Chosen(ILogger logger, int chosenCount, int availableCount);

    [LoggerMessage(EventId = 2363, Level = LogLevel.Warning, Message = "The engine did not start on its devices ({Failure}); starting it on the CPU only")]
    public static partial void FallingBackToCpu(ILogger logger, ModelProcessFailure failure);
}
