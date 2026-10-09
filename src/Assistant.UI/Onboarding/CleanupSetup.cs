using Assistant.Core.Settings;
using Assistant.UI.Settings;

namespace Assistant.UI.Onboarding;

/// <summary>Setup's questions about Cleanup: whether chats are deleted by themselves after a while, and after how long (Settings > Privacy).</summary>
public sealed partial class SetupViewModel
{
    private bool _deleteBarChats;
    private bool _deleteWindowChats;
    private string _barChatHours = NumberField.Format(CleanupSettings.DefaultBarChatHours);
    private string _windowChatDays = NumberField.Format(CleanupSettings.DefaultWindowChatDays);

    /// <summary>Whether the chats from the Search or Ask bar are deleted after <see cref="BarChatHours"/> hours.</summary>
    public bool DeleteBarChats { get => _deleteBarChats; set { if (Set(ref _deleteBarChats, value)) Changed(); } }

    /// <summary>The hours as typed.</summary>
    public string BarChatHours { get => _barChatHours; set { if (Set(ref _barChatHours, value ?? string.Empty)) Changed(); } }

    /// <summary>Whether the chats of the full window are deleted after <see cref="WindowChatDays"/> days.</summary>
    public bool DeleteWindowChats { get => _deleteWindowChats; set { if (Set(ref _deleteWindowChats, value)) Changed(); } }

    /// <summary>The days as typed.</summary>
    public string WindowChatDays { get => _windowChatDays; set { if (Set(ref _windowChatDays, value ?? string.Empty)) Changed(); } }

    /// <summary>Whether what is typed for the hours is not a number of hours that can be kept.</summary>
    public bool BarChatHoursInvalid => !Hours(out _);

    /// <summary>Whether what is typed for the days is not a number of days that can be kept.</summary>
    public bool WindowChatDaysInvalid => !Days(out _);

    /// <summary>What is wrong with a number that was typed, or empty.</summary>
    public string CleanupProblem => (BarChatHoursInvalid, WindowChatDaysInvalid) switch
    {
        (true, _) => $"Enter the hours as a number from {NumberField.Format(SettingsLimits.MinCleanupHours)} to {NumberField.Format(SettingsLimits.MaxCleanupHours)}.",
        (_, true) => $"Enter the days as a number from {NumberField.Format(SettingsLimits.MinCleanupDays)} to {NumberField.Format(SettingsLimits.MaxCleanupDays)}.",
        _ => string.Empty,
    };

    /// <summary>Whether there is a <see cref="CleanupProblem"/>.</summary>
    public bool HasCleanupProblem => CleanupProblem.Length > 0;

    /// <summary>The page can be left once both numbers can be kept; the switches have no wrong answer.</summary>
    public bool CanContinueCleanup => !IsBusy && !HasCleanupProblem;

    public async Task<bool> ApplyCleanupAsync() => CanContinueCleanup && await RunAsync(SaveCleanupAsync);

    public async Task SaveCleanupAsync()
    {
        if (!Hours(out var hours) || !Days(out var days))
        {
            return;
        }

        var (bar, window) = (DeleteBarChats, DeleteWindowChats);
        await SaveAsync(settings => settings with
        {
            Cleanup = settings.Cleanup with { DeleteBarChats = bar, BarChatHours = hours, DeleteWindowChats = window, WindowChatDays = days },
        });
        Status = string.Empty;
    }

    private bool Hours(out int hours) =>
        NumberField.TryParse(BarChatHours, out hours) && hours is >= SettingsLimits.MinCleanupHours and <= SettingsLimits.MaxCleanupHours;

    private bool Days(out int days) =>
        NumberField.TryParse(WindowChatDays, out days) && days is >= SettingsLimits.MinCleanupDays and <= SettingsLimits.MaxCleanupDays;

    private void ShowCleanup(CleanupSettings saved)
    {
        DeleteBarChats = saved.DeleteBarChats;
        DeleteWindowChats = saved.DeleteWindowChats;
        BarChatHours = NumberField.Format(saved.BarChatHours);
        WindowChatDays = NumberField.Format(saved.WindowChatDays);
    }

    private void CleanupChanged()
    {
        OnPropertyChanged(nameof(BarChatHoursInvalid));
        OnPropertyChanged(nameof(WindowChatDaysInvalid));
        OnPropertyChanged(nameof(CleanupProblem));
        OnPropertyChanged(nameof(HasCleanupProblem));
        OnPropertyChanged(nameof(CanContinueCleanup));
    }
}