using Assistant.Core.Contracts;
using Assistant.Core.Settings;

namespace Assistant.UI.Tests;

/// <summary>
/// Settings kept in memory, so changes last until the object is gone and nothing touches the disk. The app keeps its
/// settings with <c>JsonSettingsService</c> (Assistant.Data); tests use this wherever they need settings of their own,
/// such as history turned off.
/// </summary>
internal sealed class InMemorySettingsService : ISettingsService
{
    private AppSettings _settings = new();

    /// <summary>Turns history on or off, as the Privacy page does.</summary>
    public bool HistoryEnabled
    {
        set => Volatile.Write(ref _settings, _settings with { Privacy = _settings.Privacy with { HistoryEnabled = value } });
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _settings));
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _settings, settings);
        return Task.CompletedTask;
    }
}
