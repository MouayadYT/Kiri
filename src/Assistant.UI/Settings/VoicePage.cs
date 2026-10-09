using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Assets;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Assistant.UI.ViewModels;
using Assistant.UI.Voice;
using Assistant.Voice;

namespace Assistant.UI.Settings;

/// <summary>One line of the voice-speed table: what a text-to-speech engine did on this PC when it was measured.</summary>
public sealed class VoiceBenchmarkRow : NotifyingObject
{
    internal VoiceBenchmarkRow(TextToSpeechModel model, TextToSpeechBenchmarkResult? result, string? note)
    {
        Model = model;
        Result = result;
        Note = note;
    }

    /// <summary>The engine.</summary>
    public TextToSpeechModel Model { get; }

    /// <summary>What was measured, or <see langword="null"/> when the engine could not be.</summary>
    public TextToSpeechBenchmarkResult? Result { get; }

    /// <summary>Why the engine could not be measured, in words, or <see langword="null"/>.</summary>
    public string? Note { get; }

    /// <summary>What the engine is called.</summary>
    public string Name => Model.DisplayName;

    /// <summary>How soon a short answer starts being heard: the engine's time to first audio for the shortest text.</summary>
    public string FirstAudio => Result is { HasSamples: true } result ? Format(result.ShortestTimeToFirstAudio) : "—";

    /// <summary>The middle time to first audio over all the texts.</summary>
    public string Median => Result is { HasSamples: true } result ? Format(result.MedianTimeToFirstAudio) : "—";

    /// <summary>How many seconds of speech the engine makes for each second it works: above 1 it keeps ahead of the speakers.</summary>
    public string Speed => Result is { HasSamples: true } result
        ? string.Create(CultureInfo.CurrentCulture, $"{result.SpeedMultiple:0.0}× real time")
        : "—";

    /// <summary>How long the engine took to load, when it was loaded for the measurement.</summary>
    public string Load => Result?.LoadTime is { } load ? Format(load) : "—";

    /// <summary>Whether the engine could not be measured.</summary>
    public bool HasNote => !string.IsNullOrEmpty(Note);

    internal static string Format(TimeSpan time) =>
        time < TimeSpan.FromSeconds(1)
            ? string.Create(CultureInfo.CurrentCulture, $"{time.TotalMilliseconds:0} ms")
            : string.Create(CultureInfo.CurrentCulture, $"{time.TotalSeconds:0.0} s");
}

/// <summary>
/// Voice (PROJECT_SPEC §4.9, step 125): which text-to-speech engine speaks answers (KittenTTS Mini 0.8, Kokoro-82M or Piper, switched without restarting and
/// remembered), whether it is ready, a sample to hear, a measurement of how fast each engine is on this PC, whether the wake word "Kiri" is listened for, and
/// where speech recognition and each voice stand. Everything runs on this PC from files in the voices folders; none needs a server. Which engines' files
/// came packaged with the Assistant is checked against the package's checksums (step 123); files the user put in their own voices folder are used as they are.
/// </summary>
public sealed class VoicePage : SettingsPage, IDisposable
{
    private const string SampleText = "This is how I sound. I speak your answers aloud, on this PC, as I write them.";

    private readonly ITextToSpeechAssets? _voices;
    private readonly ITextToSpeechService? _speech;
    private readonly IVoiceRuntime? _runtime;
    private readonly ISpeechToTextService? _recognizer;
    private readonly VoiceModelFolders? _folders;
    private readonly Dispatcher? _dispatcher;
    private readonly IDisposable? _subscription;
    private readonly RelayCommand _check;
    private readonly Assistant.Windows.Audio.IMicrophoneDevices? _microphoneDevices;
    private readonly RelayCommand _refreshMicrophones;
    private readonly ObservableCollection<MicrophoneOption> _microphoneOptions = [];
    private string? _microphoneId;
    private string _recognizerId = VoiceModelFolders.SpeechRecognitionGroup;
    private MicrophoneOption? _selectedMicrophone;
    private bool _showingMicrophones;
    private readonly RelayCommand _speakSample;
    private readonly RelayCommand _measure;
    private readonly RelayCommand _compare;
    private readonly ObservableCollection<VoiceBenchmarkRow> _rows = [];
    private TextToSpeechModel _model = TextToSpeechModels.KittenTtsMini;
    private bool _wakeWord;
    private bool _checking;
    private bool _measuring;
    private string _sampleResult = "";
    private string _measureSummary = "";
    private bool _disposed;

    internal VoicePage(
        SettingsViewModel root, ITextToSpeechAssets? voices = null, IAppEventBus? events = null, Dispatcher? dispatcher = null,
        ITextToSpeechService? speech = null, IVoiceRuntime? runtime = null, ISpeechToTextService? recognizer = null, VoiceModelFolders? folders = null,
        Assistant.Windows.Audio.IMicrophoneDevices? microphones = null)
        : base(root, SettingsSection.Voice)
    {
        _microphoneDevices = microphones;
        _refreshMicrophones = new RelayCommand(_ => ShowMicrophones());
        _voices = voices;
        _speech = speech;
        _runtime = runtime;
        _recognizer = recognizer;
        _folders = folders;
        _dispatcher = dispatcher;
        _check = new RelayCommand(CheckFiles, _ => _voices is not null && !_checking);
        _speakSample = new RelayCommand(_ => SpeakSample(), _ => _speech is not null && !_measuring);
        _measure = new RelayCommand(_ => _ = MeasureAsync(compareAll: false), _ => _speech is not null && !_measuring);
        _compare = new RelayCommand(_ => _ = MeasureAsync(compareAll: true), _ => _speech is not null && !_measuring);
        Rows = new ReadOnlyObservableCollection<VoiceBenchmarkRow>(_rows);
        Engines = voices is null
            ? []
            : [.. voices.Peek().Select(status => new VoiceEngineItem(status, voices.FolderOf(status.Model)))];
        if (voices is not null && events is not null && dispatcher is not null)
        {
            _subscription = events.Subscribe<VoicePage, AssetStateChanged>(this, static (page, changed, _) => page.OnAssetChanged(changed));
        }

        if (dispatcher is not null)
        {
            if (speech is not null)
            {
                speech.StatusChanged += OnVoiceChanged;
            }

            if (runtime is not null)
            {
                runtime.WakeWordChanged += OnVoiceChanged;
            }

            if (recognizer is not null)
            {
                recognizer.StatusChanged += OnVoiceChanged;
            }
        }
    }

    /// <summary>The microphones to choose from: the one Windows uses, then each one that is plugged in.</summary>
    public ReadOnlyObservableCollection<MicrophoneOption> Microphones => _microphoneReadOnly ??= new ReadOnlyObservableCollection<MicrophoneOption>(_microphoneOptions);

    private ReadOnlyObservableCollection<MicrophoneOption>? _microphoneReadOnly;

    /// <summary>Whether the page can list microphones, so that it offers the choice.</summary>
    public bool HasMicrophones => _microphoneDevices is not null;

    /// <summary>The microphone to listen to. Choosing one saves it, and it is used the next time the microphone is opened (the wake word starts again on it at once).</summary>
    public MicrophoneOption? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (value is not null && !_showingMicrophones && Set(ref _selectedMicrophone, value))
            {
                _microphoneId = value.Id;
                Commit(settings => settings with { Voice = settings.Voice with { MicrophoneDeviceId = value.Id } });
                OnPropertyChanged(nameof(MicrophoneNote));
            }
        }
    }

    /// <summary>Says when the microphone that was chosen is not plugged in, so that the default is used; or what the choice means.</summary>
    public string MicrophoneNote =>
        _microphoneId is not null && _selectedMicrophone is { IsMissing: true }
            ? "The microphone you chose is not connected, so the one Windows uses is listened to until it is."
            : _microphoneOptions.Count <= 1
                ? "No microphone was found. Plug one in and press Look again."
                : "Used for spoken questions and for the wake word. Nothing it hears is recorded.";

    /// <summary>Lists the microphones again, for one that was plugged in since.</summary>
    public ICommand RefreshMicrophonesCommand => _refreshMicrophones;

    // Lists the microphones now and selects the one saved; a saved one that is not there stays as it was, shown as not connected.
    private void ShowMicrophones()
    {
        if (_microphoneDevices is null)
        {
            return;
        }

        var devices = _microphoneDevices.List();
        _showingMicrophones = true;
        try
        {
            _microphoneOptions.Clear();
            _microphoneOptions.Add(new MicrophoneOption(null, "Windows default", false));
            foreach (var device in devices)
            {
                _microphoneOptions.Add(new MicrophoneOption(device.Id, device.IsDefault ? device.Name + " (default)" : device.Name, false));
            }

            var chosen = _microphoneOptions.FirstOrDefault(option => option.Id == _microphoneId);
            if (chosen is null && _microphoneId is not null)
            {
                chosen = new MicrophoneOption(_microphoneId, "Chosen microphone (not connected)", true);
                _microphoneOptions.Add(chosen);
            }

            Set(ref _selectedMicrophone, chosen ?? _microphoneOptions[0], nameof(SelectedMicrophone));
        }
        finally
        {
            _showingMicrophones = false;
        }

        OnPropertyChanged(nameof(MicrophoneNote));
    }

    /// <summary>The text-to-speech models on offer.</summary>
    public IReadOnlyList<TextToSpeechModel> Models => TextToSpeechModels.All;

    /// <summary>The engines and whether each one's files are installed on this PC, in the order they are offered.</summary>
    public IReadOnlyList<VoiceEngineItem> Engines { get; }

    /// <summary>Whether the page knows where the engines' files are, so that it lists them.</summary>
    public bool HasEngines => Engines.Count > 0;

    /// <summary>Whether the page can speak, so that it shows the engine's status, a sample and the measurement.</summary>
    public bool HasVoice => _speech is not null;

    /// <summary>A line that says how many engines are installed, in words.</summary>
    public string EnginesSummary
    {
        get
        {
            var installed = Engines.Count(engine => engine.IsInstalled);
            var attention = Engines.Count(engine => engine.NeedsAttention);
            var text = installed == 0 && attention == 0
                ? "No voice is installed on this PC yet."
                : installed == Engines.Count
                    ? "All three voices are installed on this PC."
                    : $"{installed} of {Engines.Count} voices are installed on this PC.";
            return attention > 0 ? $"{text} {attention} {(attention == 1 ? "needs" : "need")} attention." : text;
        }
    }

    /// <summary>Whether the engines' files are being checked.</summary>
    public bool IsChecking
    {
        get => _checking;
        private set
        {
            if (Set(ref _checking, value))
            {
                _check.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Checks every engine's packaged files against the package's checksums, reading all of them.</summary>
    public ICommand CheckFilesCommand => _check;

    /// <summary>The model that speaks answers. Choosing another saves it, and the Assistant switches to it at once.</summary>
    public TextToSpeechModel SelectedModel
    {
        get => _model;
        set
        {
            if (value is not null && Set(ref _model, value))
            {
                Commit(settings => settings with { Voice = settings.Voice with { TextToSpeechModelId = value.Id } });
                ShowEngineStatus();
            }
        }
    }

    /// <summary>
    /// Where the chosen engine stands, in words: "Ready", "Loading…", "Failed: …" or why it is not installed. It follows the engine as it loads, so
    /// choosing another shows "Loading…" and then "Ready".
    /// </summary>
    public string EngineStatus
    {
        get
        {
            if (_speech is null)
            {
                return "";
            }

            var status = _speech.Status;
            if (!string.Equals(status.EngineId, _model.Id, StringComparison.Ordinal))
            {
                return "Loading…";
            }

            return status.Engine.State switch
            {
                VoiceEngineState.Ready => status.Engine.LoadTime is { } load ? $"Ready · loaded in {VoiceBenchmarkRow.Format(load)}" : "Ready",
                VoiceEngineState.Loading => "Loading…",
                VoiceEngineState.Failed => $"Failed: {status.Engine.Message}",
                VoiceEngineState.NotInstalled => status.Engine.Message ?? "Not installed",
                _ => "Not loaded yet. It loads the first time it speaks.",
            };
        }
    }

    /// <summary>Whether the chosen engine could not be loaded or is not installed, which the page draws as a warning.</summary>
    public bool EngineNeedsAttention =>
        _speech is { } speech && string.Equals(speech.Status.EngineId, _model.Id, StringComparison.Ordinal)
        && speech.Status.Engine.State is VoiceEngineState.Failed or VoiceEngineState.NotInstalled;

    /// <summary>Whether the chosen engine is ready.</summary>
    public bool EngineIsReady => _speech is { } speech && string.Equals(speech.Status.EngineId, _model.Id, StringComparison.Ordinal) && speech.Status.IsReady;

    /// <summary>Speaks a sample sentence in the chosen voice, as an answer is spoken.</summary>
    public ICommand SpeakSampleCommand => _speakSample;

    /// <summary>What the sample took: how soon the first sound was handed to the speakers.</summary>
    public string SampleResult
    {
        get => _sampleResult;
        private set => Set(ref _sampleResult, value);
    }

    /// <summary>Measures the chosen voice on this PC.</summary>
    public ICommand MeasureCommand => _measure;

    /// <summary>Measures every installed voice in turn on this PC, and puts the one that was chosen back.</summary>
    public ICommand CompareAllCommand => _compare;

    /// <summary>Whether a measurement is running.</summary>
    public bool IsMeasuring
    {
        get => _measuring;
        private set
        {
            if (Set(ref _measuring, value))
            {
                OnPropertyChanged(nameof(IsNotMeasuring));
                _speakSample.RaiseCanExecuteChanged();
                _measure.RaiseCanExecuteChanged();
                _compare.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether nothing is being measured, which is when the voice can be changed.</summary>
    public bool IsNotMeasuring => !_measuring;

    /// <summary>What was measured since the Settings window was opened, a line for each voice, the newest of each.</summary>
    public ReadOnlyObservableCollection<VoiceBenchmarkRow> Rows { get; }

    /// <summary>Whether anything has been measured.</summary>
    public bool HasRows => _rows.Count > 0;

    /// <summary>A sentence about the last measurement, or what is being measured now.</summary>
    public string MeasureSummary
    {
        get => _measureSummary;
        private set => Set(ref _measureSummary, value);
    }

    /// <summary>The word that wakes the Assistant.</summary>
    public string WakeWord => VoiceSettings.WakeWord;

    /// <summary>Whether the Assistant listens, on this PC alone, for <see cref="WakeWord"/>.</summary>
    public bool WakeWordEnabled
    {
        get => _wakeWord;
        set
        {
            if (Set(ref _wakeWord, value))
            {
                Commit(settings => settings with { Voice = settings.Voice with { WakeWordEnabled = value, VoiceInputEnabled = value || settings.Voice.VoiceInputEnabled } });
                OnPropertyChanged(nameof(WakeWordStatus));
                OnPropertyChanged(nameof(WakeWordNeedsAttention));
            }
        }
    }

    /// <summary>
    /// What the wake word is doing, in words: off, "Listening for “Kiri”", still starting, or why it cannot (no microphone, access off in Windows, the
    /// model's files missing).
    /// </summary>
    public string WakeWordStatus
    {
        get
        {
            if (!_wakeWord)
            {
                return "Off. The microphone is not open for it. Alt+A and the microphone button work as always.";
            }

            if (_runtime is null)
            {
                return "On.";
            }

            var status = _runtime.WakeWord;
            if (!status.IsOn)
            {
                return "Starting…";
            }

            return status.Message ?? (status.IsListening
                ? "Listening for “Hey Kiri” on this PC. Nothing it hears is recorded or sent anywhere."
                : "Starting…");
        }
    }

    /// <summary>Whether the wake word is on but cannot listen.</summary>
    public bool WakeWordNeedsAttention =>
        _wakeWord && _runtime?.WakeWord is { IsOn: true, Message: { } message } && !message.StartsWith("Starting", StringComparison.Ordinal);

    /// <summary>Where speech recognition stands, in words.</summary>
    public string RecognizerStatus
    {
        get
        {
            if (_recognizer is null)
            {
                return "";
            }

            var status = _recognizer.Status;
            return status.State switch
            {
                VoiceEngineState.Ready => "Ready. It recognizes speech on this PC.",
                VoiceEngineState.Loading => "Loading…",
                VoiceEngineState.Failed => $"Failed: {status.Message}",
                VoiceEngineState.NotInstalled => status.Message ?? "Not installed",

                // A recognizer that is not loaded yet has nothing to say for itself, unless it is the one whose files the user puts in the voices folder
                // by hand: Handy's, Windows' own and a downloaded one are set up above, and say there whether they are ready.
                _ => !UsesFolderRecognizer
                    ? ""
                    : _folders is { } folders && !folders.IsInstalled(VoiceModelFolders.SpeechRecognitionGroup)
                        ? "Not installed. Put its files in the voices folder below."
                        : "Installed. It loads the first time the microphone is used.",
            };
        }
    }

    /// <summary>Whether there is anything to say about the recognizer: the line is left out when there is not.</summary>
    public bool HasRecognizerStatus => RecognizerStatus.Length > 0;

    // The recognizer the settings name is the one that comes as files in the voices folder (the first one the Assistant had).
    private bool UsesFolderRecognizer => string.Equals(_recognizerId, VoiceModelFolders.SpeechRecognitionGroup, StringComparison.Ordinal);

    /// <summary>Whether speech recognition cannot be used.</summary>
    public bool RecognizerNeedsAttention => _recognizer?.Status.State is VoiceEngineState.Failed or VoiceEngineState.NotInstalled
        || (_recognizer is not null && UsesFolderRecognizer && _recognizer.Status.State == VoiceEngineState.NotLoaded && _folders is { } folders
            && !folders.IsInstalled(VoiceModelFolders.SpeechRecognitionGroup));

    /// <summary>The folder the user's own voices go in: one folder for each engine, and <c>speech-recognition</c> and <c>wake-word</c> for the listeners.</summary>
    public string VoicesFolder => _folders?.UserVoicesDirectory ?? "";

    /// <summary>Whether the page knows where the user's voices folder is.</summary>
    public bool HasVoicesFolder => _folders is not null;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription?.Dispose();
        if (_speech is not null)
        {
            _speech.StatusChanged -= OnVoiceChanged;
        }

        if (_runtime is not null)
        {
            _runtime.WakeWordChanged -= OnVoiceChanged;
        }

        if (_recognizer is not null)
        {
            _recognizer.StatusChanged -= OnVoiceChanged;
        }
    }

    internal override void Apply(AppSettings settings, bool fresh)
    {
        SelectedModel = TextToSpeechModels.Find(settings.Voice.TextToSpeechModelId) ?? TextToSpeechModels.KittenTtsMini;
        WakeWordEnabled = settings.Voice.WakeWordEnabled;
        _recognizerId = settings.Voice.SpeechRecognitionModelId;
        _microphoneId = settings.Voice.MicrophoneDeviceId;
        ShowMicrophones();
        ShowEngines(_voices?.Peek());
        ShowEngineStatus();
    }

    private void ShowEngines(IReadOnlyList<TextToSpeechAssetStatus>? statuses)
    {
        if (statuses is null)
        {
            return;
        }

        foreach (var status in statuses)
        {
            if (Engines.FirstOrDefault(engine => engine.Model.Id == status.Model.Id) is { } item)
            {
                item.Show(status);
                item.ShowUserFiles(_folders?.HasUserFiles(status.Model.Id) == true);
            }
        }

        OnPropertyChanged(nameof(EnginesSummary));
    }

    // Every status the page shows about the voice changed.
    private void ShowEngineStatus()
    {
        OnPropertyChanged(nameof(EngineStatus));
        OnPropertyChanged(nameof(EngineNeedsAttention));
        OnPropertyChanged(nameof(EngineIsReady));
        OnPropertyChanged(nameof(WakeWordStatus));
        OnPropertyChanged(nameof(WakeWordNeedsAttention));
        OnPropertyChanged(nameof(RecognizerStatus));
        OnPropertyChanged(nameof(HasRecognizerStatus));
        OnPropertyChanged(nameof(RecognizerNeedsAttention));
    }

    // Raised by the voice on whatever thread it changed on.
    private void OnVoiceChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is null || _disposed)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                ShowEngineStatus();
            }
        });
    }

    // A sample sentence, spoken as an answer is: through the same pipeline, so what it measures is what the user will hear.
    private void SpeakSample()
    {
        if (_speech is null)
        {
            return;
        }

        SampleResult = "Speaking…";
        var response = _speech.BeginResponse();
        response.Finished += (_, _) => _ = _dispatcher?.InvokeAsync(() =>
        {
            SampleResult = response.IsStopped && response.TimeToFirstAudio is null
                ? "The sample could not be played. " + (EngineNeedsAttention ? EngineStatus : "Check the speakers and the voice's status above.")
                : response.TimeToFirstAudio is { } first
                    ? $"The first sound came {VoiceBenchmarkRow.Format(first)} after the first words."
                    : "";
        });
        response.Append(SampleText);
        response.Complete();
    }

    private async Task MeasureAsync(bool compareAll)
    {
        if (_speech is not { } speech || _measuring)
        {
            return;
        }

        IsMeasuring = true;
        var chosen = _model;
        try
        {
            MeasureSummary = "Measuring… each text is made into speech without playing it.";
            var models = compareAll ? TextToSpeechModels.All : [chosen];
            foreach (var model in models)
            {
                MeasureSummary = $"Measuring {model.DisplayName}…";
                if (!string.Equals(speech.Status.EngineId, model.Id, StringComparison.Ordinal))
                {
                    await speech.SelectEngineAsync(model.Id).ConfigureAwait(true);
                }

                var result = await speech.BenchmarkAsync().ConfigureAwait(true);
                Show(new VoiceBenchmarkRow(model, result.HasSamples ? result : null, result.Failure));
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        finally
        {
            if (compareAll && !string.Equals(speech.Status.EngineId, chosen.Id, StringComparison.Ordinal))
            {
                await speech.SelectEngineAsync(chosen.Id).ConfigureAwait(true);
            }

            MeasureSummary = _rows.Count == 0
                ? ""
                : "Measured on this PC with the processor alone. A voice that makes more than one second of speech for each second it works keeps ahead of the speakers.";
            IsMeasuring = false;
            ShowEngineStatus();
        }
    }

    // The newest line for the engine replaces the one before it.
    private void Show(VoiceBenchmarkRow row)
    {
        var existing = _rows.FirstOrDefault(known => known.Model.Id == row.Model.Id);
        if (existing is not null)
        {
            _rows[_rows.IndexOf(existing)] = row;
        }
        else
        {
            var order = TextToSpeechModels.All.ToList();
            var index = 0;
            while (index < _rows.Count && order.FindIndex(model => model.Id == _rows[index].Model.Id) < order.FindIndex(model => model.Id == row.Model.Id))
            {
                index++;
            }

            _rows.Insert(index, row);
        }

        OnPropertyChanged(nameof(HasRows));
    }

    private void CheckFiles(object? parameter)
    {
        if (_voices is not null && !_checking)
        {
            IsChecking = true;
            _ = CheckFilesAsync(_voices);
        }
    }

    // Every file is read in full. The states are shown as they change (OnAssetChanged); this only says when the checking is over.
    private async Task CheckFilesAsync(ITextToSpeechAssets voices)
    {
        try
        {
            await voices.VerifyAsync(AssetCheckMode.Reverify).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // Each engine's own state says that it could not be read.
        }
        finally
        {
            if (_dispatcher is not null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    IsChecking = false;
                    ShowEngines(_voices?.Peek());
                });
            }
        }
    }

    // Called on the thread that finished or began a check.
    private Task OnAssetChanged(AssetStateChanged changed) =>
        changed.Kind != AssetKind.Voice || _dispatcher is null
            ? Task.CompletedTask
            : _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    ShowEngines(_voices?.Peek());
                }
            }).Task;
}
