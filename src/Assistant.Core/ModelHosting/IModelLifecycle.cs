using Assistant.Core.Contracts;
using Assistant.Core.Events;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Loads and unloads the local model in the model host and keeps its status (PROJECT_SPEC §5.6): the app-side
/// counterpart of the host's load and unload commands. Every change of status is also published on the
/// <see cref="IAppEventBus"/> as a <see cref="ModelStatusChanged"/>, in order.
/// </summary>
public interface IModelLifecycle
{
    /// <summary>The latest status, the same as the last <see cref="ModelStatusChanged"/> published.</summary>
    ModelStatusChanged Current { get; }

    /// <summary>
    /// The model that is loaded and ready, as its load returned it, or <see langword="null"/> while none is: before
    /// the first load, while one loads or unloads, and after a failure.
    /// </summary>
    ModelInfo? Model { get; }

    /// <summary>
    /// Loads the model made of <paramref name="files"/>, starting the model host first when it is not running, and
    /// replacing any model that is loaded or loading. Waits until the model is ready.
    /// </summary>
    /// <param name="files">The model file and the files that go with it.</param>
    /// <param name="cancellationToken">Cancelling stops the load: the model host is asked to unload.</param>
    /// <returns>The loaded model's identity and capabilities.</returns>
    /// <exception cref="Exception">
    /// A <c>ModelHostException</c> when the model could not be loaded, or when another load or unload replaced this
    /// one; <see cref="Current"/> says why.
    /// </exception>
    Task<ModelInfo> LoadAsync(ModelFiles files, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads the model and frees its memory, ending a load in progress. Does nothing when no model is loaded.
    /// </summary>
    Task UnloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates with the loaded model in the running model host, streaming the host's replies as
    /// <see cref="ModelHostClient.GenerateAsync"/> does. It never starts the host or loads a model: load it first.
    /// </summary>
    /// <exception cref="Exception">
    /// A <c>ModelHostException</c> with <see cref="ModelHostErrorCode.ModelNotFound"/> when no host runs, and as
    /// <see cref="ModelHostClient.GenerateAsync"/> otherwise.
    /// </exception>
    IAsyncEnumerable<ModelHostReply> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken = default);
}
