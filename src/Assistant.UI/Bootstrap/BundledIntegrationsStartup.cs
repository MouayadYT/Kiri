using System.IO;
using Assistant.Tools.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap;

/// <summary>
/// Keeps the integrations that are programs shipped with the Assistant (Microsoft To Do's) pointing at the copy that is here now: the Assistant may have been installed somewhere else, or
/// replaced by a newer version, since the program was recorded. It looks once, in the background, when the application starts, and changes nothing but the path of a program that
/// moved. A program that is no longer there is left as it is, and its use says so.
/// </summary>
internal sealed partial class BundledIntegrationsStartup(IInstalledIntegrationRegistry registry, ILogger<BundledIntegrationsStartup> logger) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RefreshAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var integration in await registry.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (integration.Source is { Kind: IntegrationSourceKind.Bundled, Origin: { } program }
                    && integration.Transport.Kind == Assistant.Tools.Mcp.McpTransportKind.Stdio
                    && BundledPrograms.Find(program) is { } here
                    && !string.Equals(integration.Transport.Command, here, StringComparison.OrdinalIgnoreCase))
                {
                    await registry.UpdateAsync(integration.Id, current => current with { Transport = current.Transport with { Command = here } }, cancellationToken).ConfigureAwait(false);
                    LogMoved(logger);
                }
            }
        }
        catch (Exception exception) when (exception is IntegrationException or IOException)
        {
            LogFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 3280, Level = LogLevel.Information, Message = "A program that ships with the Assistant was found at its new place")]
    private static partial void LogMoved(ILogger logger);

    [LoggerMessage(EventId = 3281, Level = LogLevel.Warning, Message = "The programs that ship with the Assistant could not be checked: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
