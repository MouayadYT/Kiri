namespace Assistant.Core.ModelHosting;

/// <summary>
/// What to tell the user about the model runtime's state: one plain sentence that says what is wrong and what fixes it.
/// It never holds a path, so it is also safe in diagnostics (PROJECT_SPEC §3.3).
/// </summary>
public static class ModelRuntimeStateText
{
    /// <summary>Describes <paramref name="state"/>.</summary>
    public static string Describe(ModelRuntimeState state) => state switch
    {
        ModelRuntimeState.Ready => "The local model runtime is ready.",
        ModelRuntimeState.NotInstalled =>
            "The local model runtime isn't installed. Reinstall the Assistant to restore it.",
        ModelRuntimeState.Incomplete =>
            "Some files of the local model runtime are missing. Reinstall the Assistant to restore them.",
        ModelRuntimeState.Incompatible =>
            "The local model runtime is damaged or not built for this PC. Reinstall the 64-bit version of the Assistant.",
        ModelRuntimeState.MissingSystemComponent =>
            "Local models need the Microsoft Visual C++ Redistributable (x64). Install it from Microsoft, then restart the Assistant.",
        ModelRuntimeState.Inaccessible =>
            "The local model runtime couldn't be opened. Security software may be blocking it; restart the Assistant or reinstall it.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
