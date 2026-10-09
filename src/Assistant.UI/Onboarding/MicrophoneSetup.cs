using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Assistant.Core.Settings;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;
using Assistant.Windows.Audio;

namespace Assistant.UI.Onboarding;

/// <summary>
/// The microphone the Assistant listens to, under the wake word in setup and in Settings: which one, and a way to try it. A PC may list several (a
/// headset that is off, a camera's, the virtual ones that streaming and audio software add) and the one Windows uses by default is not always the one
/// that hears the user; a microphone that hears nothing is why a wake word or a recognizer seems not to work. The choice is saved as it is made.
/// </summary>
public sealed partial class SetupViewModel
{
    /// <summary>How long a microphone is tried for before it is said to have heard nothing.</summary>
    internal static readonly TimeSpan MicrophoneTestTime = TimeSpan.FromSeconds(4);

    // Below this, what was heard is not a quiet room but a device that nothing feeds.
    private const double NoSoundLevel = 0.0005;

    internal const string MicrophoneListening = "Say something…";
    internal const string MicrophoneHeard = "Heard you.";
    internal const string MicrophoneSilent = "No sound from this microphone. Choose another.";
    internal const string MicrophoneQuiet = "Too quiet to tell. Speak up, or choose another microphone.";

    private IMicrophoneDevices? _microphoneDevices;
    private IMicrophoneProbe? _microphoneProbe;
    private RelayCommand _testMicrophone = null!;
    private MicrophoneOption? _selectedMicrophone;
    private string? _microphoneId;
    private string _microphoneStatus = "";
    private double _microphoneLevel;
    private bool _listingMicrophones, _testingMicrophone;

    /// <summary>The microphones to choose from: the one Windows uses, then each one that is connected.</summary>
    public ObservableCollection<MicrophoneOption> Microphones { get; } = [];

    /// <summary>Whether microphones can be listed, so that the choice is offered.</summary>
    public bool HasMicrophones => _microphoneDevices is not null;

    /// <summary>Whether a microphone can be tried.</summary>
    public bool CanTestMicrophone => _microphoneProbe is not null;

    /// <summary>The microphone to listen to. Choosing one saves it: the wake word starts again on it at once, and the next spoken question uses it.</summary>
    public MicrophoneOption? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (value is null || _listingMicrophones || !Set(ref _selectedMicrophone, value))
            {
                return;
            }

            _microphoneId = value.Id;
            MicrophoneStatus = "";
            _ = SaveMicrophoneAsync(value.Id);
        }
    }

    /// <summary>What trying the microphone found, or what is being waited for.</summary>
    public string MicrophoneStatus { get => _microphoneStatus; private set => Set(ref _microphoneStatus, value); }

    /// <summary>How loud the microphone is while it is tried, from 0 to 1, for the bar under it.</summary>
    public double MicrophoneLevel { get => _microphoneLevel; private set => Set(ref _microphoneLevel, value); }

    /// <summary>Whether the microphone is being tried.</summary>
    public bool IsTestingMicrophone
    {
        get => _testingMicrophone;
        private set
        {
            if (Set(ref _testingMicrophone, value))
            {
                _testMicrophone.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Listens to the chosen microphone for a few seconds and says whether it heard anything.</summary>
    public ICommand TestMicrophoneCommand => _testMicrophone;

    /// <summary>The test that is running, or a finished task; for tests.</summary>
    internal Task MicrophoneTest { get; private set; } = Task.CompletedTask;

    private void InitializeMicrophones(IMicrophoneDevices? devices, IMicrophoneProbe? probe)
    {
        _microphoneDevices = devices;
        _microphoneProbe = probe;
        _testMicrophone = new RelayCommand(_ => MicrophoneTest = TestMicrophoneAsync(), _ => _microphoneProbe is not null && !_testingMicrophone);
    }

    /// <summary>
    /// Lists the microphones as they are now and selects the saved one; a saved one that is not connected stays chosen, and is shown as that. The list
    /// is only touched when it changed, so that looking again does not move what is selected.
    /// </summary>
    public void RefreshMicrophones()
    {
        if (_microphoneDevices is null)
        {
            return;
        }

        List<MicrophoneOption> options = [new(null, "Windows default", false)];
        options.AddRange(_microphoneDevices.List().Select(device => new MicrophoneOption(device.Id, device.IsDefault ? device.Name + " (default)" : device.Name, false)));
        if (_microphoneId is not null && options.All(option => option.Id != _microphoneId))
        {
            options.Add(new MicrophoneOption(_microphoneId, "Chosen microphone (not connected)", true));
        }

        _listingMicrophones = true;
        try
        {
            if (!options.SequenceEqual(Microphones))
            {
                Microphones.Clear();
                foreach (var option in options)
                {
                    Microphones.Add(option);
                }
            }

            Set(ref _selectedMicrophone, Microphones.FirstOrDefault(option => option.Id == _microphoneId) ?? Microphones[0], nameof(SelectedMicrophone));
        }
        finally
        {
            _listingMicrophones = false;
        }
    }

    private void ShowMicrophone(VoiceSettings voice)
    {
        _microphoneId = string.IsNullOrWhiteSpace(voice.MicrophoneDeviceId) ? null : voice.MicrophoneDeviceId;
        RefreshMicrophones();
    }

    private async Task SaveMicrophoneAsync(string? id)
    {
        try
        {
            await SaveAsync(settings => settings with { Voice = settings.Voice with { MicrophoneDeviceId = id } });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsValidationException)
        {
            MicrophoneStatus = "The microphone could not be saved. Choose it again.";
        }
    }

    private async Task TestMicrophoneAsync()
    {
        if (_microphoneProbe is null || _testingMicrophone)
        {
            return;
        }

        IsTestingMicrophone = true;
        MicrophoneStatus = MicrophoneListening;
        try
        {
            // The bar is drawn against a voice, not against full scale: a normal voice is a tenth of it.
            var result = await _microphoneProbe.ListenAsync(_microphoneId, MicrophoneTestTime, level => MicrophoneLevel = Math.Min(1, level * 8));
            MicrophoneStatus = result.Failure is { } failure
                ? VoiceInputViewModel.Describe(failure) + "."
                : result.Heard
                    ? MicrophoneHeard
                    : result.Peak < NoSoundLevel ? MicrophoneSilent : MicrophoneQuiet;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MicrophoneStatus = VoiceInputViewModel.Describe(MicrophoneFailure.Unavailable) + ".";
        }
        finally
        {
            MicrophoneLevel = 0;
            IsTestingMicrophone = false;
        }
    }
}
