using Assistant.Core.Audit;
using Microsoft.Extensions.Hosting;

namespace Assistant.UI.Bootstrap;

/// <summary>
/// Lets the activity log finish writing when the app ends (PROJECT_SPEC §3.5, step 117): the log is written in the background, one state after another, so what happened in
/// the last moments is kept. It waits a few seconds at most and never holds the app up beyond that.
/// </summary>
internal sealed class AuditShutdown(AuditLog log) : IHostedService
{
    /// <summary>The longest the app waits for the log.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await log.FlushAsync().WaitAsync(MaxWait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // What is not written by now is left: the app is closing.
        }
    }
}
