using Assistant.Core.Settings;

namespace Assistant.Core.Contracts;

/// <summary>Loads and saves the user's settings as one <see cref="AppSettings"/> document.</summary>
public interface ISettingsService
{
    /// <summary>Loads the current settings, or the defaults when none are saved.</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves <paramref name="settings"/>, replacing the previous settings.</summary>
    /// <exception cref="SettingsValidationException">
    /// <paramref name="settings"/> hold values the app cannot work with (<see cref="SettingsValidator"/>). Nothing is saved.
    /// </exception>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the current settings with <paramref name="change"/> and saves the result, and returns it. A service that
    /// can does this as one step, so two windows changing different settings at once never undo each other; this
    /// default is a load followed by a save.
    /// </summary>
    /// <exception cref="SettingsValidationException">The changed settings hold values the app cannot work with. Nothing is saved.</exception>
    async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var updated = change(await LoadAsync(cancellationToken).ConfigureAwait(false));
        await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
