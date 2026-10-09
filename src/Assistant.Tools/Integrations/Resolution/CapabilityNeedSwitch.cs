namespace Assistant.Tools.Integrations;

/// <summary>
/// Whether a request that names no app is read for what it asks of an integration (PROJECT_SPEC §4.8, step 116): "check my calendar" is then a need for something that reads a
/// calendar, and when none is installed the Assistant goes on as it does for a named app (it offers to look, installs only on a click, and carries on with the request). It is off
/// unless something turns it on, so by default a request that names no app is the model's, as it always was; the workflow demo turns it on while it is running.
/// </summary>
public sealed class CapabilityNeedSwitch
{
    private int _on;

    /// <summary>Whether such requests are read.</summary>
    public bool IsOn
    {
        get => Volatile.Read(ref _on) != 0;
        set => Volatile.Write(ref _on, value ? 1 : 0);
    }
}
