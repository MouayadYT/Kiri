using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Models;

namespace Assistant.ModelHost.Tests;

/// <summary>A controller for tests that do not load models: nothing is ever loaded, and nothing can be.</summary>
internal sealed class NullModelController : IModelController
{
    public ModelStatusReport Current { get; } = new(0, ModelStatus.NotLoaded);

    public ModelInfo? Model => null;

    public event EventHandler<ModelStatusReport>? Changed
    {
        add { }
        remove { }
    }

    public Task<ModelInfo> LoadAsync(LoadModelRequest request, CancellationToken cancellationToken) =>
        throw new ModelRequestException(ModelHostErrorCode.ModelNotFound, ModelFailure.ModelNotFound);

    public Task<string?> UnloadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
