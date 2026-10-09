using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Assistant.UI.Controls;
using Assistant.UI.Orb;
using Assistant.UI.ViewModels;
using Assistant.Windows.Audio;

namespace Assistant.UI.Views;

/// <summary>
/// A place to see the assistant orb move before the voice pipeline exists: the orb on white, as in the reference, in
/// each state, fed through its one amplitude input with invented speech, a slider, or the real microphone. The
/// microphone is on only while Microphone is chosen and this window is in front (PROJECT_SPEC §3.1 P2); its level is
/// read and shown as a number, never recorded.
/// </summary>
public partial class OrbPreviewWindow : Window
{
    private readonly PreviewAmplitude _amplitude;
    private readonly VoiceInputViewModel _microphone;
    private readonly DispatcherTimer _readout;

    internal OrbPreviewWindow(IMicrophoneLevelMeter meter)
    {
        InitializeComponent();
        _microphone = new VoiceInputViewModel(meter);
        _amplitude = new PreviewAmplitude(new MockSpeechAmplitude(), () => LevelSlider.Value, new VoiceLevelAmplitude(_microphone));
        Orb.AmplitudeSource = _amplitude;

        _microphone.PropertyChanged += OnMicrophoneChanged;
        _readout = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _readout.Tick += (_, _) => Readout.Text = string.Create(CultureInfo.InvariantCulture, $"Amplitude {_amplitude.ReadAmplitude():F2}");
        _readout.Start();

        Deactivated += (_, _) => StopMicrophone();
        Closed += (_, _) =>
        {
            _readout.Stop();
            _microphone.PropertyChanged -= OnMicrophoneChanged;
            StopMicrophone();
        };
    }

    /// <summary>The orb being previewed.</summary>
    internal AssistantOrb PreviewOrb => Orb;

    private void OnStateChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: OrbState state } && Orb is not null)
        {
            Orb.State = state;
        }
    }

    private void OnSoundChecked(object sender, RoutedEventArgs e)
    {
        // Choosing the first option in the markup raises this before the window is built.
        if (_amplitude is null)
        {
            return;
        }

        Notice.Text = string.Empty;
        LevelSlider.IsEnabled = SliderButton.IsChecked == true;
        _amplitude.Mode = SliderButton.IsChecked == true ? PreviewSound.Slider
            : MicrophoneButton.IsChecked == true ? PreviewSound.Microphone : PreviewSound.Speech;
        if (_amplitude.Mode == PreviewSound.Microphone)
        {
            _microphone.Start();
        }
        else
        {
            _microphone.Stop();
        }
    }

    // Stops listening, and goes back to the invented speech if the microphone was what was chosen.
    private void StopMicrophone()
    {
        if (_amplitude.Mode == PreviewSound.Microphone)
        {
            _microphone.Stop();
            SpeechButton.IsChecked = true;
        }
    }

    // The microphone stopped by itself (none found, access off, unplugged): say why, and use the invented speech.
    private void OnMicrophoneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VoiceInputViewModel.FailureMessage) && _microphone.FailureMessage is { } message)
        {
            SpeechButton.IsChecked = true;
            Notice.Text = message;
        }
    }

    /// <summary>Where the orb is fed from in the preview.</summary>
    internal enum PreviewSound
    {
        Speech,
        Slider,
        Microphone,
    }

    // The orb's one amplitude source, switching between the three the preview offers.
    internal sealed class PreviewAmplitude(IOrbAmplitudeSource speech, Func<double> slider, IOrbAmplitudeSource microphone)
        : IOrbAmplitudeSource
    {
        public PreviewSound Mode { get; set; }

        public double ReadAmplitude()
        {
            return Mode switch
            {
                PreviewSound.Slider => slider(),
                PreviewSound.Microphone => microphone.ReadAmplitude(),
                _ => speech.ReadAmplitude(),
            };
        }
    }
}
