using Assistant.Core.Contracts;
using Assistant.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Data.Settings;

/// <summary>Registers the persisted settings.</summary>
public static class SettingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISettingsService"/> and <see cref="ISettingsLoadReport"/> over the settings file in the app's
    /// folder (<see cref="AppPaths.SettingsFilePath"/>). It needs <see cref="AppPaths"/>, <see cref="TimeProvider"/> and
    /// logging to be registered too. Nothing touches the disk until the settings are first read.
    /// </summary>
    public static IServiceCollection AddAssistantSettings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => new JsonSettingsService(
            provider.GetRequiredService<AppPaths>().SettingsFilePath,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<JsonSettingsService>>()));
        services.AddSingleton<ISettingsService>(provider => provider.GetRequiredService<JsonSettingsService>());
        services.AddSingleton<ISettingsLoadReport>(provider => provider.GetRequiredService<JsonSettingsService>());
        return services;
    }
}
