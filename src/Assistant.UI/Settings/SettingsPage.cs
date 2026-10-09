using Assistant.Core.Settings;

namespace Assistant.UI.Settings;

/// <summary>
/// One section's view model. It shows part of the <see cref="AppSettings"/> and, when the user changes a control, asks
/// the <see cref="SettingsViewModel"/> to save the change.
/// </summary>
public abstract class SettingsPage : NotifyingObject
{
    private readonly SettingsViewModel _root;

    /// <summary>Creates the page of <paramref name="root"/>.</summary>
    protected SettingsPage(SettingsViewModel root, SettingsSection section)
    {
        _root = root;
        Section = section;
    }

    /// <summary>The section this page is.</summary>
    public SettingsSection Section { get; }

    /// <summary>The settings as they are now.</summary>
    protected AppSettings Current => _root.Current;

    /// <summary>
    /// Shows <paramref name="settings"/>. No change made here is saved: the settings are just being read.
    /// <paramref name="fresh"/> is true when the window is opened or a change was refused, and the page then drops
    /// anything the user typed that was not accepted; otherwise it keeps what is being typed.
    /// </summary>
    internal abstract void Apply(AppSettings settings, bool fresh);

    /// <summary>
    /// Saves a change the user made. It does nothing while the page is only being filled from the settings, so reading
    /// the settings never writes them.
    /// </summary>
    protected void Commit(Func<AppSettings, AppSettings> change)
    {
        if (!_root.IsApplying)
        {
            _ = _root.CommitAsync(change);
        }
    }

    /// <summary>
    /// Saves a change the user made to a setting that also changes something outside the settings file: <paramref name="apply"/>
    /// does that first, and only when it worked is the setting saved; otherwise the page goes back and says
    /// <paramref name="failure"/>. Like the other, it does nothing while the page is being filled.
    /// </summary>
    protected void Commit(Func<AppSettings, AppSettings> change, Func<Task<bool>> apply, string failure)
    {
        if (!_root.IsApplying)
        {
            _ = _root.CommitAsync(change, apply, failure);
        }
    }

    /// <summary>Whether the page is being filled from the settings.</summary>
    protected bool IsApplying => _root.IsApplying;
}
