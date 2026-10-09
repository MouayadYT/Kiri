using Assistant.Core.Contracts;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Loads the local model the settings name before it is first used, so that the first question is answered without waiting for it: when setup
/// has just chosen it (PROJECT_SPEC §5.6). It is the same load a first question would start, so a question asked while it runs waits for this one
/// instead of starting another.
/// </summary>
public interface IModelPreloader
{
    /// <summary>
    /// Loads the model the settings name, starting the model host when it is not running, and waits until it is ready.
    /// </summary>
    /// <returns>The loaded model, or <see langword="null"/> when no model is set up.</returns>
    /// <exception cref="Exception">A <c>ModelHostException</c> when the model could not be loaded; the model's status says why.</exception>
    Task<ModelInfo?> PreloadAsync(CancellationToken cancellationToken = default);
}
