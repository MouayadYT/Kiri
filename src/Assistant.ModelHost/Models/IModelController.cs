using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Models;

/// <summary>Tells whoever listens what the model's status is, and when it changes.</summary>
internal interface IModelStatusSource
{
    /// <summary>The latest status. Its <see cref="ModelStatusReport.Sequence"/> is 0 until the status has changed once.</summary>
    ModelStatusReport Current { get; }

    /// <summary>
    /// Raised, in order, each time the status changes, on the thread that changed it and while the source holds its
    /// lock: a handler must return at once, for example by queueing the report, and must not call back into the source.
    /// </summary>
    event EventHandler<ModelStatusReport>? Changed;
}

/// <summary>
/// Loads and unloads the model on the owner's behalf, one operation at a time, and keeps its status. It checks the
/// files, has the engine started for them (<see cref="Processes.IModelProcessManager"/>) and reports how that went.
/// </summary>
internal interface IModelController : IModelStatusSource
{
    /// <summary>The loaded model's identity and capabilities, or <see langword="null"/> when none is loaded.</summary>
    ModelInfo? Model { get; }

    /// <summary>
    /// Loads the model <paramref name="request"/> names, replacing the loaded model, and waits until it is ready. A load
    /// or unload that is still going is stopped first, and its own request fails with
    /// <see cref="ModelHostErrorCode.Cancelled"/>.
    /// </summary>
    /// <exception cref="ModelRequestException">The model could not be loaded, or the load was superseded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ModelInfo> LoadAsync(LoadModelRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Unloads the model, ending a load in progress, and waits until the engine has exited. Also succeeds when nothing
    /// was loaded.
    /// </summary>
    /// <returns>The identifier of the model that was loaded or loading, or <see langword="null"/> when there was none.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<string?> UnloadAsync(CancellationToken cancellationToken);
}
