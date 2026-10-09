using Microsoft.Extensions.Logging;

namespace Assistant.Core.ModelHosting;

/// <summary>A running model host and the connection to it, which its owner disposes to end the host.</summary>
public interface IModelHostConnection : IAsyncDisposable
{
    /// <summary>The connection to the host.</summary>
    ModelHostClient Client { get; }
}

/// <summary>Starts a model host and connects to it.</summary>
public interface IModelHostLauncher
{
    /// <summary>Starts the host, connects to it and pings it.</summary>
    /// <exception cref="ModelHostException">The host could not be started or reached.</exception>
    Task<IModelHostConnection> StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>Starts the model host as a process the app owns (<see cref="ModelHostProcess"/>).</summary>
public sealed class ModelHostProcessLauncher(ModelHostLaunchOptions options, ILoggerFactory loggerFactory)
    : IModelHostLauncher
{
    /// <inheritdoc/>
    public async Task<IModelHostConnection> StartAsync(CancellationToken cancellationToken = default) =>
        await ModelHostProcess.StartAsync(options, loggerFactory, cancellationToken).ConfigureAwait(false);
}
