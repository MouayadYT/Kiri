using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Processes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Server;

/// <summary>
/// Runs the <see cref="ModelHostServer"/> for the life of the process. When serving ends, for whatever reason, it ends
/// the model engine and then stops the process, so the engine never outlives the host (PROJECT_SPEC §5.6).
/// </summary>
internal sealed class ModelHostService(
    ModelHostServer server,
    IModelProcessManager engine,
    ModelHostOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<ModelHostService> logger) : BackgroundService
{
    /// <summary>The process's exit code: 0 when the owner ended the host or went away, 1 when something failed.</summary>
    public int ExitCode { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            ModelHostLog.Starting(
                logger,
                typeof(ModelHostService).Assembly.GetName().Version,
                ModelHostProtocol.Version,
                options.OwnerProcessId ?? 0);

            var reason = await server.RunAsync(stoppingToken).ConfigureAwait(false);
            ExitCode = reason is ModelHostExitReason.ConnectTimedOut
                or ModelHostExitReason.ProtocolViolation
                or ModelHostExitReason.PipeUnavailable
                ? 1
                : 0;
            ModelHostLog.Exiting(logger, reason, ExitCode);
        }
        catch (Exception exception)
        {
            ExitCode = 1;
            ModelHostLog.Failed(logger, exception);
        }
        finally
        {
            try
            {
                await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ModelProcessLog.StopFailed(logger, exception);
            }

            lifetime.StopApplication();
        }
    }
}
