using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;

namespace Assistant.UI.Settings;

/// <summary>
/// Cleanup: chats that are deleted by themselves once they have not been added to for a while. The chats from the Search or Ask bar have their
/// own time, in hours, and the chats of the full window theirs, in days; each has a switch, and its number is typed over. Both take effect at
/// once (<c>HistoryRetentionService</c> deletes within a minute of a change, and every ten minutes after that).
/// </summary>
public sealed class CleanupPage : SettingsPage
{
    private bool _deleteBarChats;
    private bool _deleteWindowChats;

    internal CleanupPage(SettingsViewModel root)
        : base(root, SettingsSection.Cleanup)
    {
        BarChatHours = new NumberField(
            hours => Range(hours, SettingsLimits.MinCleanupHours, SettingsLimits.MaxCleanupHours),
            hours => Commit(settings => settings with { Cleanup = settings.Cleanup with { BarChatHours = hours } }));
        WindowChatDays = new NumberField(
            days => Range(days, SettingsLimits.MinCleanupDays, SettingsLimits.MaxCleanupDays),
            days => Commit(settings => settings with { Cleanup = settings.Cleanup with { WindowChatDays = days } }));
    }

    /// <summary>Whether the chats from the Search or Ask bar are deleted after <see cref="BarChatHours"/>.</summary>
    public bool DeleteBarChats
    {
        get => _deleteBarChats;
        set
        {
            if (Set(ref _deleteBarChats, value))
            {
                Commit(settings => settings with { Cleanup = settings.Cleanup with { DeleteBarChats = value } });
            }
        }
    }

    /// <summary>How many hours a chat from the bar is kept for after it was last added to.</summary>
    public NumberField BarChatHours { get; }

    /// <summary>Whether the chats of the full window are deleted after <see cref="WindowChatDays"/>.</summary>
    public bool DeleteWindowChats
    {
        get => _deleteWindowChats;
        set
        {
            if (Set(ref _deleteWindowChats, value))
            {
                Commit(settings => settings with { Cleanup = settings.Cleanup with { DeleteWindowChats = value } });
            }
        }
    }

    /// <summary>How many days a chat of the full window is kept for after it was last added to.</summary>
    public NumberField WindowChatDays { get; }

    internal override void Apply(AppSettings settings, bool fresh)
    {
        DeleteBarChats = settings.Cleanup.DeleteBarChats;
        DeleteWindowChats = settings.Cleanup.DeleteWindowChats;
        Show(BarChatHours, settings.Cleanup.BarChatHours, fresh);
        Show(WindowChatDays, settings.Cleanup.WindowChatDays, fresh);
    }

    private static ContextAdvice Range(int value, int min, int max) =>
        value >= min && value <= max
            ? ContextAdvice.None
            : new ContextAdvice(ContextAdviceLevel.Error, $"Enter a number from {NumberField.Format(min)} to {NumberField.Format(max)}.");

    private static void Show(NumberField field, int value, bool fresh)
    {
        if (fresh)
        {
            field.Reset(value);
        }
        else
        {
            field.Show(value);
        }
    }
}