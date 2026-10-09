using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Models;

/// <summary>
/// A request to load a model failed. It maps to the error the owner is answered with; its message is fixed text, never
/// a path.
/// </summary>
internal sealed class ModelRequestException(ModelHostErrorCode code, ModelFailure? failure = null)
    : Exception($"The model request failed ({code}).")
{
    /// <summary>The error to answer the request with.</summary>
    public ModelHostErrorCode Code { get; } = code;

    /// <summary>What the status reports, or <see langword="null"/> when the request was only superseded.</summary>
    public ModelFailure? Failure { get; } = failure;
}
