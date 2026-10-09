using Assistant.UI.Onboarding;

namespace Assistant.UI.Settings;

/// <summary>
/// The dedicated ASR settings page: everything about listening. It deliberately uses the same setup view model and control as
/// onboarding so model downloads, Handy sharing, Windows recognition, device selection, wake-word setup and deletion behave
/// identically in both places, and under them shows the microphone and how the recognizer is doing, which the Voice page
/// keeps the state of and no longer shows itself.
/// </summary>
public sealed class AsrPage : SettingsPage
{
    /// <summary>Creates the ASR page over the shared onboarding/setup state.</summary>
    internal AsrPage(SettingsViewModel root, SetupViewModel? setup, VoicePage? listening = null)
        : base(root, SettingsSection.Asr)
    {
        Setup = setup;
        Listening = listening;
    }

    /// <summary>The shared ASR setup model, or null in lightweight hosts that do not provide onboarding services.</summary>
    public SetupViewModel? Setup { get; }

    /// <summary>Whether there is a <see cref="Setup"/> to show.</summary>
    public bool HasSetup => Setup is not null;

    /// <summary>The microphone, the wake word's switch and the recognizer's status, as the Voice page keeps them; null where there is no voice.</summary>
    public VoicePage? Listening { get; }

    internal override void Apply(Assistant.Core.Settings.AppSettings settings, bool fresh)
    {
        // SetupViewModel is refreshed by SettingsViewModel.LoadAsync. The page itself owns no duplicate settings state.
    }
}
