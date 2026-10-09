using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Generation;

/// <summary>
/// The engine could not generate the answer. It maps to the error the owner is answered with; its message is fixed
/// text, never the engine's own words, which can repeat the prompt.
/// </summary>
internal sealed class GenerationException(ModelHostErrorCode code, Exception? innerException = null)
    : Exception($"The model engine could not generate the answer ({code}).", innerException)
{
    /// <summary>The error to answer the request with.</summary>
    public ModelHostErrorCode Code { get; } = code;
}
