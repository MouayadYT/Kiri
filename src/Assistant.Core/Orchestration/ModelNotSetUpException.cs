namespace Assistant.Core.Orchestration;

/// <summary>
/// Thrown when a turn is asked but no local model is set up, so there is nothing to answer with. It is not thrown for
/// a model that is set up but cannot be used, which is a <see cref="ModelHosting.ModelHostException"/>.
/// </summary>
public sealed class ModelNotSetUpException : InvalidOperationException
{
    /// <summary>Creates the exception, with its fixed, content-free message.</summary>
    public ModelNotSetUpException()
        : base("No local model is set up.")
    {
    }
}
