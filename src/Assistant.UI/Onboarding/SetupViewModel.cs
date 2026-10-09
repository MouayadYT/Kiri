using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Hardware;
using Assistant.Core.Models;
using Assistant.Core.ModelHosting;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;
using Assistant.UI.Voice;
using Assistant.Voice;
using Assistant.Windows.Hardware;
using Microsoft.Win32;

namespace Assistant.UI.Onboarding;

/// <summary>Shared setup and model library for onboarding and Settings.</summary>
public sealed partial class SetupViewModel : NotifyingObject
{
    private readonly ISettingsService _settings;
    private readonly IAppEventBus _events;
    private readonly IModelLibrary _library;
    private readonly IModelLifecycle _model;
    private readonly ITextToSpeechService _speech;
    private readonly VoiceModelFolders _folders;
    private readonly ISecretStore _secrets;
    private readonly IHardwareProfileService _hardware;
    private readonly ISpeechToTextService? _recognizer;
    private readonly IWakeWordService? _wakeWord;
    private readonly IModelPreloader? _preloader;
    private readonly Assistant.Core.Startup.ILaunchAtLogin? _launchAtLogin;
    private readonly RelayCommand _downloadAi, _useAi, _downloadVoice, _test, _delete, _saveVoice;
    private ModelChoice? _selectedAi;
    private ModelChoice? _selectedVoice;
    private ComputeDevice? _aiDevice;
    private ComputeDevice? _voiceDevice;
    private string _customPath = "", _status = "", _benchmark = "", _asrBenchmark = "", _endpoint = "", _apiModel = "", _apiVoice = "";
    private bool _busy, _api, _remoteSpeech, _visionOnDemand, _savesVisionChoice, _skipVoice, _appliesVoiceControl;
    private double _progress;
    private CancellationTokenSource? _job;
    private readonly ModelChoice _cudaRuntime;
    private readonly RelayCommand _downloadRecognizer, _downloadWakeWord, _saveControl, _testRecognizer;
    private bool _voiceControl, _wakeEnabled;

    public SetupViewModel(ISettingsService settings, IAppEventBus events, IModelLibrary library, IModelLifecycle model,
        ITextToSpeechService speech, VoiceModelFolders folders, ISecretStore secrets, IHardwareProfileService hardware,
        ISpeechToTextService? recognizer = null, IWakeWordService? wakeWord = null, ConnectionsSetupViewModel? connections = null, SearchSetupViewModel? search = null, Assistant.UI.Voice.HandyIntegration? handy = null,
        IModelPreloader? preloader = null, Assistant.Core.Startup.ILaunchAtLogin? launchAtLogin = null,
        Assistant.Windows.Audio.IMicrophoneDevices? microphones = null, Assistant.Windows.Audio.IMicrophoneProbe? microphoneProbe = null)
    {
        _settings = settings; _events = events; _library = library; _model = model; _speech = speech; _folders = folders; _secrets = secrets; _hardware = hardware;
        _recognizer = recognizer; _wakeWord = wakeWord; _preloader = preloader; _launchAtLogin = launchAtLogin;
        Connections = connections ?? new(settings, events);
        Search = search ?? new(settings, events);
        Search.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(SearchSetupViewModel.IsBusy) or nameof(SearchSetupViewModel.CanContinue)) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(IsIdle)); Changed(); } };
        Connections.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ConnectionsSetupViewModel.IsBusy)) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(IsIdle)); Changed(); } };
        AiModels = [.. DownloadCatalog.LanguageModels.Select(item => new ModelChoice(item, library))];
        VoiceModels = [.. DownloadCatalog.Voices.Select(item => new ModelChoice(item, library))];
        Installed = new();
        _cudaRuntime = new ModelChoice(DownloadCatalog.CudaVoiceRuntime, library);
        Recognizer = new(DownloadCatalog.Recognizer, library); WakeWord = new(DownloadCatalog.WakeWord, library);
        InitializeAsrChoices(handy ?? new());
        InitializeMicrophones(microphones, microphoneProbe);
        _selectedAi = AiModels[0]; _selectedVoice = VoiceModels[0];
        _aiDevice = AiDevices[0]; _voiceDevice = VoiceDevices[0];
        _downloadAi = new(parameter => { if (parameter is ModelChoice choice) SelectedAi = choice; _ = RunAsync(() => DownloadAsync(SelectedAi!)); }, parameter => !IsBusy && (parameter as ModelChoice ?? SelectedAi) is { IsInstalled: false });
        _useAi = new(parameter => { if (parameter is ModelChoice choice) SelectedAi = choice; _ = RunAsync(SaveAiAsync); }, parameter => !IsBusy && (parameter is ModelChoice choice ? choice.IsInstalled : CanContinueAi));
        _downloadVoice = new(_ => _ = RunAsync(() => DownloadAsync(SelectedVoice!)), _ => !IsBusy && !UseApi && SelectedVoice is { IsInstalled: false } && (SelectedVoice.NeedsUpdate || !_folders.IsInstalled(SelectedVoice.Id)));
        _test = new(_ => _ = RunAsync(TestAsync), _ => !IsBusy && CanContinueVoice);
        _delete = new(parameter => _ = RunAsync(() => DeleteAsync((ModelChoice)parameter!)), parameter => !IsBusy && parameter is ModelChoice { CanDelete: true });
        _saveVoice = new(_ => _ = RunAsync(SaveVoiceAsync), _ => !IsBusy && CanContinueVoice);
        _downloadRecognizer = new(_ => _ = RunAsync(() => DownloadAsync(SelectedAsr.Model!)), _ => !IsBusy && SelectedAsr.Model is { IsInstalled: false } && !IsRecognizerInstalled);
        _downloadWakeWord = new(_ => _ = RunAsync(() => DownloadAsync(WakeWord)), _ => !IsBusy && !IsWakeWordInstalled);
        _saveControl = new(_ => _ = RunAsync(SaveVoiceControlAsync), _ => CanContinueVoiceControl);
        _testRecognizer = new(_ => _ = RunAsync(TestRecognizerAsync), _ => CanTestRecognizer);
        BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => { _job?.Cancel(); Connections.CancelCommand.Execute(null); Search.CancelCommand.Execute(null); });
    }

    public IReadOnlyList<ModelChoice> AiModels { get; }
    public ConnectionsSetupViewModel Connections { get; }
    public bool CanContinueConnections => !IsBusy;
    public SearchSetupViewModel Search { get; }
    public bool CanContinueSearch => !IsBusy && Search.CanContinue;
    public IReadOnlyList<ModelChoice> VoiceModels { get; }
    public ObservableCollection<ModelChoice> Installed { get; }
    public ModelChoice Recognizer { get; }
    public ModelChoice WakeWord { get; }
    public bool EnableVoiceControl { get => _voiceControl; set { if (Set(ref _voiceControl, value)) { Changed(); ApplyVoiceControlAtOnce(switched: true); } } }
    public bool EnableWakeWord { get => _wakeEnabled; set { if (Set(ref _wakeEnabled, value)) { Changed(); ApplyVoiceControlAtOnce(); } } }
    public bool IsRecognizerInstalled => SelectedAsr.Id switch { "handy" => Handy.Installed && Handy.ModelInstalled && Handy.PasteCompatible, "windows" => WindowsRecognizerAvailable, _ => SelectedAsr.Model?.IsInstalled == true || SelectedAsr.Id == Recognizer.Id && _folders.IsInstalled(Recognizer.Id) };
    public bool CanTestRecognizer => !IsBusy && _recognizer is not null && IsRecognizerInstalled && AsrDevice.Available;
    public bool IsWakeWordInstalled => WakeWord.IsInstalled || _folders.IsInstalled(WakeWord.Id);
    public bool CanContinueVoiceControl => !IsBusy && IsVoiceControlComplete;

    // Whether what is chosen for listening can be used as it stands: off, or a recognizer that is there, and the wake word's model when that is on.
    private bool IsVoiceControlComplete => !EnableVoiceControl || IsRecognizerInstalled && AsrDevice.Available && (!EnableWakeWord || IsWakeWordInstalled);

    /// <summary>The saving of a change made to listening in Settings, or a finished task; for tests.</summary>
    internal Task VoiceControlApplying { get; private set; } = Task.CompletedTask;

    // In Settings, a change to listening (on or off, another recognizer or device, the wake word) is used as soon as it is complete: there is no button to
    // remember. What is not complete yet (a recognizer still to download) waits for its download, which applies it; and while listening is off, what is
    // chosen for it is only looked at, and is saved when it is turned on.
    private void ApplyVoiceControlAtOnce(bool switched = false)
    {
        if (_appliesVoiceControl && !IsBusy && IsVoiceControlComplete && (EnableVoiceControl || switched))
        {
            VoiceControlApplying = RunAsync(SaveVoiceControlAsync);
        }
    }
    public ObservableCollection<ComputeDevice> AiDevices { get; } = [new(null, "Automatic · best available GPU"), new("cpu", "CPU")];
    public ObservableCollection<ComputeDevice> VoiceDevices { get; } = [new("cpu", "CPU", true, "Runs locally on the processor.")];
    public ModelChoice? SelectedAi { get => _selectedAi; set { if (Set(ref _selectedAi, value)) { if (value is not null) CustomModelPath = ""; Changed(); } } }
    public ModelChoice? SelectedVoice { get => _selectedVoice; set { if (Set(ref _selectedVoice, value)) { Benchmark = ""; Changed(); } } }
    public ComputeDevice? AiDevice { get => _aiDevice; set => Set(ref _aiDevice, value); }

    /// <summary>
    /// Whether the chosen model is loaded without its vision projector until a picture is asked about (<see cref="ModelSettings.VisionOnDemand"/>).
    /// It is saved with the model: by Use model, and by going on from this page of setup.
    /// </summary>
    public bool VisionOnDemand
    {
        get => _visionOnDemand;
        set
        {
            if (Set(ref _visionOnDemand, value))
            {
                Changed();

                // In Settings the choice is the user's as soon as it is made; during setup it is saved with the rest of the page.
                if (_savesVisionChoice)
                {
                    _ = RunAsync(SaveVisionChoiceAsync);
                }
            }
        }
    }

    // The choice alone, for a model that is already in use: saved, and the model loaded again as the choice says.
    private async Task SaveVisionChoiceAsync()
    {
        var wanted = VisionOnDemand;
        if ((await _settings.LoadAsync()).Model.VisionOnDemand == wanted)
        {
            return;
        }

        await _model.UnloadAsync();
        await SaveAsync(settings => settings with { Model = settings.Model with { VisionOnDemand = wanted } });
        StartPreload((wanted ? "Picture reading is now loaded only when it is needed. " : "Picture reading now stays loaded. ") + PreloadingStatus);
    }

    /// <summary>Whether the chosen model reads pictures, so that there is something to load only when it is needed.</summary>
    public bool OffersVisionOnDemand => !HasCustomModel && SelectedAi?.Model.Projector is not null;

    /// <summary>What the choice costs and saves, for the chosen model.</summary>
    public string VisionOnDemandDetail => SelectedAi?.Model is { Projector: not null } model
        ? string.Create(CultureInfo.CurrentCulture, $"Saves about {model.VisionMemoryBytes / 1073741824d:0.0} GB of memory. A question with a picture takes a few seconds longer.")
        : "";
    public ComputeDevice? VoiceDevice { get => _voiceDevice; set { if (Set(ref _voiceDevice, value)) Changed(); } }
    public string CustomModelPath { get => _customPath; private set { if (Set(ref _customPath, value)) Changed(); } }
    public bool HasCustomModel => !string.IsNullOrEmpty(CustomModelPath);
    public bool UseApi { get => _api; set { if (Set(ref _api, value)) { Benchmark = ""; Changed(); } } }
    public string ApiEndpoint { get => _endpoint; set { if (Set(ref _endpoint, value)) Changed(); } }
    public string ApiModel { get => _apiModel; set { if (Set(ref _apiModel, value)) Changed(); } }
    public string ApiVoice { get => _apiVoice; set { if (Set(ref _apiVoice, value)) Changed(); } }
    public bool AllowRemoteSpeech { get => _remoteSpeech; set { if (Set(ref _remoteSpeech, value)) Changed(); } }
    public bool IsRemoteApi => Uri.TryCreate(ApiEndpoint, UriKind.Absolute, out var uri) && !uri.IsLoopback;
    // Only held while typing, then handed directly to Windows Credential Manager. Never serialized.
    internal string? PendingApiKey { get; set; }
    public string Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); OnPropertyChanged(nameof(FooterStatus)); } }

    /// <summary>
    /// What setup's own footer says, above Back and Next: how a download is going, and what went wrong. What only says that all is well (the model is
    /// loading, it is loaded) is left out there; Settings, which has no other place for it, shows <see cref="Status"/> as it is.
    /// </summary>
    public string FooterStatus => _notice.Length > 0 && Status == _notice ? string.Empty : Status;

    // The last thing said that was no more than good news.
    private string _notice = "";
    public bool HasStatus => Status.Length > 0;
    public string Benchmark { get => _benchmark; private set => Set(ref _benchmark, value); }
    public string AsrBenchmark { get => _asrBenchmark; private set => Set(ref _asrBenchmark, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsBusy { get => _busy || Connections.IsBusy || Search.IsBusy; private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(IsModelBusy)); Changed(); } } }
    public bool IsModelBusy => _busy;
    public bool IsIdle => !IsBusy;
    public bool CanContinueAi => !IsBusy && (SelectedAi?.IsInstalled == true || HasCustomModel && File.Exists(CustomModelPath));
    /// <summary>
    /// Whether setup goes on without a voice: nothing is downloaded and nothing is chosen, and answers are written and not spoken until a voice is set up
    /// in Settings, under Voice. Choosing or downloading a voice after all takes it back.
    /// </summary>
    public bool SkipVoice { get => _skipVoice; set { if (Set(ref _skipVoice, value)) Changed(); } }

    /// <summary>Whether setup can be finished as far as the voice goes: one is ready, or it was skipped.</summary>
    public bool CanFinishVoice => SkipVoice ? !IsBusy : CanContinueVoice;
    public bool CanContinueVoice => !IsBusy && (UseApi
        ? SpeechApiAddress.IsValid(ApiEndpoint) && !string.IsNullOrWhiteSpace(ApiModel) && !string.IsNullOrWhiteSpace(ApiVoice) && (!IsRemoteApi || AllowRemoteSpeech)
        : SelectedVoice is not null && !SelectedVoice.NeedsUpdate && (SelectedVoice.IsInstalled || _folders.IsInstalled(SelectedVoice.Id)) && VoiceDevice?.Available == true);
    public string HardwareSummary { get; private set; } = "Detecting hardware…";
    public string VoiceDeviceNote => VoiceDevice?.Note ?? "";
    public string VoiceInstallState => SelectedVoice?.NeedsUpdate == true ? "Update available · download to get the correct af_heart voice" : SelectedVoice is not null && _folders.IsInstalled(SelectedVoice.Id) ? "Installed on this PC" : SelectedVoice?.State ?? "";
    public ICommand DownloadAiCommand => _downloadAi;
    public ICommand UseAiCommand => _useAi;
    public ICommand DownloadVoiceCommand => _downloadVoice;
    public ICommand TestCommand => _test;
    public ICommand DeleteCommand => _delete;
    public ICommand SaveVoiceCommand => _saveVoice;
    public ICommand BrowseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DownloadRecognizerCommand => _downloadRecognizer;
    public ICommand DownloadWakeWordCommand => _downloadWakeWord;
    public ICommand SaveVoiceControlCommand => _saveControl;
    public ICommand TestRecognizerCommand => _testRecognizer;

    public async Task InitializeAsync(bool detectHardware = true)
    {
        if (IsBusy) return;
        await Connections.InitializeAsync();
        await Search.InitializeAsync();
        _appliesVoiceControl = false;
        var saved = await _settings.LoadAsync();
        var managed = AiModels.FirstOrDefault(item => string.Equals(Path.Combine(_library.FolderOf(item.Model), "model.gguf"), saved.Model.ModelFilePath, StringComparison.OrdinalIgnoreCase));
        _selectedAi = managed ?? (saved.Model.ModelFilePath is null ? AiModels[0] : null);
        CustomModelPath = managed is null ? saved.Model.ModelFilePath ?? "" : "";
        _savesVisionChoice = false;
        VisionOnDemand = saved.Model.VisionOnDemand;
        _savesVisionChoice = saved.Ui.FirstRunCompleted;
        _selectedVoice = VoiceModels.FirstOrDefault(item => item.Id == saved.Voice.TextToSpeechModelId) ?? VoiceModels[0];
        UseApi = saved.Voice.TextToSpeechModelId == TextToSpeechModels.CustomApi.Id;
        ApiEndpoint = saved.Voice.SpeechApiEndpoint; ApiModel = saved.Voice.SpeechApiModel; ApiVoice = saved.Voice.SpeechApiVoice;
        AllowRemoteSpeech = !saved.Privacy.LocalOnly;
        SelectedAsr = AsrModels.FirstOrDefault(item => item.Id == saved.Voice.SpeechRecognitionModelId) ?? AsrModels.Last();
        AsrDevice = AsrDevices.FirstOrDefault(item => item.Id == saved.Voice.SpeechRecognitionDevice && item.Available) ?? AsrDevices[0];
        if (detectHardware)
        {
            await RefreshHandyAsync();
            if (!saved.Ui.FirstRunCompleted && saved.Voice.SpeechRecognitionModelId == "speech-recognition")
                SelectedAsr = Handy.Installed ? AsrModels[0] : AsrModels.First(item => item.Id == "asr-parakeet-v3");
        }
        _appliesVoiceControl = false;
        EnableVoiceControl = saved.Voice.VoiceInputEnabled && IsRecognizerInstalled;
        EnableWakeWord = saved.Voice.WakeWordEnabled;
        ShowMicrophone(saved.Voice);
        ShowGameMode(saved.GameMode);
        ShowCleanup(saved.Cleanup);
        StartOnBoot = saved.LaunchAtLogin.Enabled;
        OnPropertyChanged(nameof(SelectedAi)); OnPropertyChanged(nameof(SelectedVoice));
        // In Settings what is chosen for listening is used as soon as it can be; during setup it is saved when the page is left.
        _appliesVoiceControl = saved.Ui.FirstRunCompleted;
        if (!detectHardware) { Refresh(); return; }
        var devices = await InferenceDevices.ListAsync();
        AiDevices.Clear(); foreach (var device in devices) AiDevices.Add(device);
        var wanted = saved.Model.UseGpuAcceleration ? saved.Model.GpuDeviceId : "cpu";
        AiDevice = AiDevices.FirstOrDefault(device => device.Id == wanted) ?? AiDevices[0];
        if (wanted is not null && wanted != "cpu" && !AiDevices.Any(device => device.Id == wanted)) Status = "The saved GPU is unavailable. Choose a device before applying the model.";
        var (hardware, npus) = await Task.Run(() => (_hardware.Current, NpuDevices.List()));
        HardwareSummary = $"{hardware.Memory.TotalGiB:0.#} GB system memory · {hardware.Gpus.Count} graphics adapter(s)";
        OnPropertyChanged(nameof(HardwareSummary));
        VoiceDevices.Clear(); VoiceDevices.Add(new("cpu", "CPU", true, "CPU is supported by the bundled voice runtime."));
        var nvidia = await InferenceDevices.ListNvidiaAsync();
        foreach (var device in nvidia) VoiceDevices.Add(device);
        foreach (var gpu in hardware.Gpus.Where(gpu => gpu.Kind == GpuAdapterKind.Hardware && (gpu.VendorId != 0x10DE || nvidia.Count == 0)))
            VoiceDevices.Add(new("gpu", gpu.Name + " · GPU (unsupported)", false, "This GPU has no supported voice backend. Choose CPU or a detected NVIDIA GPU."));
        foreach (var npu in npus) VoiceDevices.Add(new("npu", npu + " · NPU (unsupported)", false, "Detected NPU. This build does not include an OpenVINO NPU runtime. Choose CPU or a supported GPU."));
        VoiceDevice = VoiceDevices.FirstOrDefault(device => device.Id == saved.Voice.TextToSpeechDevice) ?? VoiceDevices[0];
        Refresh();
    }

    public async Task SaveAiAsync()
    {
        if (!(SelectedAi?.IsInstalled == true || HasCustomModel && File.Exists(CustomModelPath))) throw new IOException("Download a model or choose an existing GGUF file first.");
        var file = HasCustomModel ? CustomModelPath : Path.Combine(_library.FolderOf(SelectedAi!.Model), "model.gguf");
        var projector = !HasCustomModel && SelectedAi!.Model.Files.Any(item => item.Name == "projector.gguf") ? Path.Combine(_library.FolderOf(SelectedAi.Model), "projector.gguf") : null;
        await _model.UnloadAsync();
        var saved = await _settings.UpdateAsync(settings => settings with { Model = settings.Model with { ModelFilePath = file,
            ProjectorFilePath = HasCustomModel && string.Equals(file, settings.Model.ModelFilePath, StringComparison.OrdinalIgnoreCase) ? settings.Model.ProjectorFilePath : projector,
            ChatTemplateFilePath = HasCustomModel && string.Equals(file, settings.Model.ModelFilePath, StringComparison.OrdinalIgnoreCase) ? settings.Model.ChatTemplateFilePath : null,
            VisionOnDemand = HasCustomModel ? settings.Model.VisionOnDemand : VisionOnDemand && projector is not null,
            UseGpuAcceleration = AiDevice?.Id != "cpu", GpuDeviceId = AiDevice?.Id == "cpu" ? null : AiDevice?.Id } });
        await _events.PublishAsync(new SettingsSaved(saved));

        // During setup the model loads once setup is finished, so that it does not take the memory the voice is measured with; chosen in
        // Settings, it loads at once. Either way the first question does not wait for it.
        if (saved.Ui.FirstRunCompleted)
        {
            StartPreload("Model selected. " + PreloadingStatus);
        }
        else
        {
            Status = string.Empty;
        }
    }

    /// <summary>What the status line says while the chosen model loads ahead of the first question.</summary>
    public const string PreloadingStatus = "Loading it now, so your first question is answered right away.";

    /// <summary>What the status line says once the chosen model has loaded.</summary>
    public const string PreloadedStatus = "Your model is loaded and ready.";

    /// <summary>The load of the chosen model that runs in the background, or a finished task; for tests.</summary>
    internal Task Preloading { get; private set; } = Task.CompletedTask;

    // Loads the chosen model in the background, so that the first question does not wait for it, and says how it went in the status line unless
    // something else has been said there since. A load that fails is not an error of setup: the first question tries again, and says why.
    private void StartPreload(string status)
    {
        if (_preloader is null)
        {
            return;
        }

        _notice = status;
        Status = status;
        Preloading = PreloadAsync(status);
    }

    private async Task PreloadAsync(string status)
    {
        string outcome;
        try
        {
            outcome = await _preloader!.PreloadAsync() is null ? "" : PreloadedStatus;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome = "The model could not be loaded yet. It loads again when you ask your first question.";
        }

        if (Status == status)
        {
            _notice = outcome == PreloadedStatus ? outcome : "";
            Status = outcome;
        }
    }

    public async Task SaveVoiceAsync()
    {
        if (UseApi && (!SpeechApiAddress.IsValid(ApiEndpoint) || string.IsNullOrWhiteSpace(ApiModel) || string.IsNullOrWhiteSpace(ApiVoice))) throw new IOException("Enter a valid endpoint, model and voice.");
        if (UseApi && IsRemoteApi && !AllowRemoteSpeech) throw new IOException("Allow remote speech, or use an endpoint on this PC.");
        if (UseApi && PendingApiKey is { } typedKey && (typedKey.Length > SecretNames.MaxSecretLength || typedKey.Any(char.IsControl))) throw new IOException("Enter an API key of at most 1,024 characters, without line breaks.");
        if (!UseApi && (SelectedVoice is null || SelectedVoice.NeedsUpdate || !_folders.IsInstalled(SelectedVoice.Id) || VoiceDevice?.Available != true)) throw new IOException("Download the current voice and select a supported device first.");
        if (!UseApi && VoiceDevice?.Id?.StartsWith("cuda:", StringComparison.Ordinal) == true)
        {
            var progress = DownloadProgress("NVIDIA runtime · ");
            foreach (var package in DownloadCatalog.CudaPackages)
                await _library.DownloadAsync(package, progress, _job?.Token ?? CancellationToken.None);
        }
        if (UseApi && PendingApiKey is { Length: > 0 } key) { await _secrets.SetAsync(ApiTextToSpeechEngine.SecretNameFor(ApiEndpoint), key); PendingApiKey = null; }
        await _speech.UnloadAsync();
        await SaveAsync(settings => settings with { Privacy = UseApi && IsRemoteApi ? settings.Privacy with { LocalOnly = !AllowRemoteSpeech } : settings.Privacy,
            Voice = settings.Voice with { TextToSpeechModelId = UseApi ? TextToSpeechModels.CustomApi.Id : SelectedVoice!.Id,
            SpeechApiEndpoint = ApiEndpoint, SpeechApiModel = ApiModel, SpeechApiVoice = ApiVoice, TextToSpeechDevice = UseApi ? "cpu" : VoiceDevice!.Id! } });
        await _speech.SelectEngineAsync(UseApi ? TextToSpeechModels.CustomApi.Id : SelectedVoice!.Id);
        _job?.Token.ThrowIfCancellationRequested();
        if (!_speech.Status.IsReady) throw new IOException(_speech.Status.Engine.Message ?? "The voice could not be loaded. Check its files and try again.");
        SkipVoice = false;
        Status = string.Empty;
    }

    public async Task<bool> FinishAsync()
    {
        if (!CanContinueAi || !CanFinishVoice || !CanContinueVoiceControl || !CanContinueSearch || HasCleanupProblem) { Status = HasCleanupProblem ? CleanupProblem : "Complete model, voice and search setup, or turn off optional features, before finishing."; return false; }
        var successful = await RunAsync(async () => { await SaveAiAsync(); if (!SkipVoice) await SaveVoiceAsync(); await SaveVoiceControlAsync(); await SaveGameModeAsync(); await SaveCleanupAsync(); if (!await Search.ApplyAsync()) throw new IOException(Search.Status); await SaveAsync(settings => settings with { Ui = settings.Ui with { FirstRunCompleted = true } }); });

        // Setup is done: the model loads now, while the user reads the last page, rather than on the first question.
        if (successful)
        {
            StartPreload(PreloadingStatus);
        }

        return successful;
    }

    public async Task<bool> ApplyAiAsync() => await RunAsync(SaveAiAsync);
    public async Task<bool> ApplyVoiceAsync() => await RunAsync(SaveVoiceAsync);
    public async Task<bool> ApplyVoiceControlAsync() => await RunAsync(SaveVoiceControlAsync);

    public async Task SaveVoiceControlAsync()
    {
        if (EnableVoiceControl && (!IsRecognizerInstalled || !AsrDevice.Available || EnableWakeWord && !IsWakeWordInstalled)) throw new IOException(UsesHandy ? HandyStatus : UsesWindowsAsr ? AsrInstallState : "Download the recognizer and any enabled wake-word model, and choose a supported device first.");
        if (EnableVoiceControl && _recognizer is not null)
        {
            var voice = (await _settings.LoadAsync()).Voice with { SpeechRecognitionModelId = SelectedAsr.Id, SpeechRecognitionDevice = AsrDevice.Id! };
            await _recognizer.ConfigureAsync(voice, _job?.Token ?? CancellationToken.None);
            await _recognizer.WarmUpAsync(_job?.Token ?? CancellationToken.None);
            if (!_recognizer.Status.IsReady) throw new IOException(_recognizer.Status.Message ?? "The recognizer could not load.");
        }
        if (EnableVoiceControl && EnableWakeWord && _wakeWord is not null)
        {
            await _wakeWord.WarmUpAsync(_job?.Token ?? CancellationToken.None);
            if (!_wakeWord.Status.IsReady) throw new IOException(_wakeWord.Status.Message ?? "The wake-word model could not load.");
        }
        await SaveAsync(settings => settings with { Voice = settings.Voice with { VoiceInputEnabled = EnableVoiceControl, WakeWordEnabled = EnableVoiceControl && EnableWakeWord,
            SpeechRecognitionModelId = SelectedAsr.Id, SpeechRecognitionDevice = AsrDevice.Id! } });
        Status = string.Empty;
    }

    private IProgress<ModelDownloadProgress> DownloadProgress(string prefix = "") => new Progress<ModelDownloadProgress>(value =>
    {
        Progress = value.Percent;
        Status = prefix + $"{value.Stage} {value.Percent:0}%" + (value.BytesPerSecond > 0 ? $" · {value.BytesPerSecond / 1_000_000d:0.0} MB/s" : "")
            + $" · {value.ReceivedBytes / 1_000_000d:0} / {value.TotalBytes / 1_000_000d:0} MB";
    });

    private async Task DownloadAsync(ModelChoice choice)
    {
        var progress = DownloadProgress();
        if (choice.Model.Kind == DownloadKind.Voice && choice.NeedsUpdate) await _speech.UnloadAsync();
        await _library.DownloadAsync(choice.Model, progress, _job!.Token);
        Refresh();
        if (choice.Model.Kind == DownloadKind.Language) await SaveAiAsync();
        else if (choice.Model.Kind == DownloadKind.Voice) { await SaveVoiceAsync(); }
        else if (choice.Model.Kind is DownloadKind.SpeechRecognition or DownloadKind.WakeWord && _appliesVoiceControl && IsVoiceControlComplete) await SaveVoiceControlAsync();
        Status = string.Empty;
    }

    private async Task DeleteAsync(ModelChoice choice)
    {
        var saved = await _settings.LoadAsync();
        if (choice.Model.Kind == DownloadKind.Language)
        {
            await _model.UnloadAsync();
            if (string.Equals(saved.Model.ModelFilePath, Path.Combine(_library.FolderOf(choice.Model), "model.gguf"), StringComparison.OrdinalIgnoreCase))
                await SaveAsync(settings => settings with { Model = settings.Model with { ModelFilePath = null, ProjectorFilePath = null, ChatTemplateFilePath = null } });
        }
        else if (choice.Model.Kind is DownloadKind.SpeechRecognition or DownloadKind.WakeWord)
        {
            var deletingActiveAsr = choice.Model.Kind == DownloadKind.SpeechRecognition && saved.Voice.SpeechRecognitionModelId == choice.Id;
            var deletingWake = choice.Model.Kind == DownloadKind.WakeWord;
            if (deletingWake || deletingActiveAsr) EnableWakeWord = false;
            if (deletingActiveAsr) EnableVoiceControl = false;
            if (deletingActiveAsr || deletingWake)
                await SaveAsync(settings => settings with { Voice = settings.Voice with { WakeWordEnabled = false, VoiceInputEnabled = deletingActiveAsr ? false : settings.Voice.VoiceInputEnabled } });
            if (deletingActiveAsr) _recognizer?.Unload();
            if (deletingActiveAsr || deletingWake) _wakeWord?.Unload();
        }
        else { await _speech.UnloadAsync(); }
        if (choice.Model.Kind == DownloadKind.Runtime)
            foreach (var dependency in DownloadCatalog.CudaDependencies.Where(_library.IsInstalled)) await _library.DeleteAsync(dependency);
        await _library.DeleteAsync(choice.Model);
        if (choice.Model.Kind == DownloadKind.SpeechRecognition) AsrBenchmark = "";
        Refresh(); Status = string.Empty;
    }

    private async Task TestAsync()
    {
        Benchmark = "Loading and measuring…";
        await SaveVoiceAsync();
        var measured = await _speech.BenchmarkAsync(_job!.Token);
        if (!measured.HasSamples) throw new IOException(measured.Failure ?? "The voice could not be tested.");
        var response = _speech.BeginResponse();
        response.Append("You're all set. I'm Kiri, and this is how I sound."); response.Complete();
        Benchmark = string.Create(CultureInfo.CurrentCulture, $"First audio {measured.ShortestTimeToFirstAudio.TotalMilliseconds:0} ms · {measured.SpeedMultiple:0.0}× real time")
            + (measured.LoadTime is { } load ? $" · load {load.TotalSeconds:0.0} s" : "") + (UseApi ? " · API" : VoiceDevice?.Id?.StartsWith("cuda:", StringComparison.Ordinal) == true ? " · NVIDIA GPU" : " · CPU");
        Status = string.Empty;
    }

    private async Task TestRecognizerAsync()
    {
        if (_recognizer is null) throw new IOException("Speech recognition is unavailable in this build.");
        if (!IsRecognizerInstalled) throw new IOException(AsrInstallState);
        AsrBenchmark = "Loading and measuring…";
        var saved = await _settings.LoadAsync();
        await _recognizer.ConfigureAsync(saved.Voice with { SpeechRecognitionModelId = SelectedAsr.Id, SpeechRecognitionDevice = AsrDevice.Id! }, _job!.Token);
        var measured = await _recognizer.BenchmarkAsync(AsrSpeedTestAudio.Load(), _job.Token);
        if (!measured.HasSamples)
        {
            AsrBenchmark = measured.Failure ?? "The recognizer could not be tested.";
            Status = AsrBenchmark;
            return;
        }
        var load = measured.LoadTime is { } loadTime ? $"load {loadTime.TotalSeconds:0.0} s · " : "";
        AsrBenchmark = string.Create(CultureInfo.CurrentCulture,
            $"{load}recognition {measured.RecognitionTime!.Value.TotalSeconds:0.00} s · {measured.SpeedMultiple:0.0}× real time · {measured.AudioDuration.TotalSeconds:0.0} s sample");
        Status = string.Empty;
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog { Title = "Select a custom model", Filter = "GGUF models (*.gguf)|*.gguf", CheckFileExists = true };
        if (dialog.ShowDialog() == true) { SelectedAi = null; CustomModelPath = dialog.FileName; }
    }

    private async Task SaveAsync(Func<AppSettings, AppSettings> change)
    {
        var saved = await _settings.UpdateAsync(change);
        await _events.PublishAsync(new SettingsSaved(saved));
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        if (IsBusy) return false;
        _job = new(); IsBusy = true; Progress = 0;
        try { await action(); return true; }
        catch (OperationCanceledException) { Status = "Canceled. Click Download to resume the saved progress."; return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or SettingsValidationException or SecretStoreException or VoiceEngineException or ModelHostException)
        { Status = ex is HttpRequestException ? "The download could not be reached. Check your connection and try again." : ex.Message; Benchmark = ""; return false; }
        finally { _job.Dispose(); _job = null; IsBusy = false; Refresh(); }
    }

    private void Refresh()
    {
        Installed.Clear();
        foreach (var item in AiModels.Concat(VoiceModels).Concat(AsrModels.Where(option => option.Model is not null).Select(option => option.Model!)).Concat(new[] { WakeWord, _cudaRuntime })) { item.Refresh(); if (item.CanDelete) Installed.Add(item); }
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(CanContinueAi)); OnPropertyChanged(nameof(CanContinueVoice)); OnPropertyChanged(nameof(CanFinishVoice)); OnPropertyChanged(nameof(HasCustomModel));
        OnPropertyChanged(nameof(OffersVisionOnDemand)); OnPropertyChanged(nameof(VisionOnDemandDetail));
        OnPropertyChanged(nameof(CanContinueVoiceControl)); OnPropertyChanged(nameof(IsRecognizerInstalled)); OnPropertyChanged(nameof(IsWakeWordInstalled)); OnPropertyChanged(nameof(CanTestRecognizer));
        AsrChanged();
        GameModeChanged();
        CleanupChanged();
        OnPropertyChanged(nameof(CanContinueConnections));
        OnPropertyChanged(nameof(CanContinueSearch));
        OnPropertyChanged(nameof(VoiceDeviceNote)); OnPropertyChanged(nameof(VoiceInstallState));
        OnPropertyChanged(nameof(IsRemoteApi));
        _downloadAi?.RaiseCanExecuteChanged(); _useAi?.RaiseCanExecuteChanged(); _downloadVoice?.RaiseCanExecuteChanged();
        _test?.RaiseCanExecuteChanged(); _delete?.RaiseCanExecuteChanged(); _saveVoice?.RaiseCanExecuteChanged();
        _downloadRecognizer?.RaiseCanExecuteChanged(); _downloadWakeWord?.RaiseCanExecuteChanged(); _saveControl?.RaiseCanExecuteChanged(); _testRecognizer?.RaiseCanExecuteChanged();
    }
}
