using Assistant.Core.ModelHosting;
using Microsoft.Extensions.Hosting;

namespace Assistant.UI.Bootstrap;

/// <summary>
/// Ends the model host with the app: when the host stops, the <see cref="ModelLifecycle"/> asks the model host to shut
/// down, which frees the model's memory (PROJECT_SPEC §5.6). The model host also exits by itself when the app's process
/// does, so this only makes the ending orderly.
/// </summary>
internal sealed class ModelLifecycleShutdown(ModelLifecycle lifecycle) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken) => await lifecycle.DisposeAsync().ConfigureAwait(false);
}
