using Assistant.Core.Settings;

namespace Assistant.Core.Events;

/// <summary>
/// The user's settings were saved (PROJECT_SPEC §5.10): a part of the app that follows a setting while it runs, such as the clipboard
/// history that keeps nothing while its permission is off, reads what it needs from <see cref="Settings"/>.
/// </summary>
/// <param name="Settings">The settings as they are saved now.</param>
public sealed record SettingsSaved(AppSettings Settings);
