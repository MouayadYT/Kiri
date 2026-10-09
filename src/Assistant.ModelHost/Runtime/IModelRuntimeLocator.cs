namespace Assistant.ModelHost.Runtime;

/// <summary>Finds the inference runtime bundled with the app and checks that it can run.</summary>
internal interface IModelRuntimeLocator
{
    /// <summary>
    /// Checks the runtime's files as they are now. A missing, damaged or unreadable runtime is a status, not an
    /// exception. Nothing is ever downloaded.
    /// </summary>
    ModelRuntimeStatus Locate();
}
