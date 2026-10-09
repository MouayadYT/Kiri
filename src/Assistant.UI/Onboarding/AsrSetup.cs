using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Assistant.Core.Models;
using Assistant.UI.Voice;
using Assistant.Windows.Hardware;

namespace Assistant.UI.Onboarding;

public sealed record AsrChoice(string Id, string Name, string Detail, string Ram, string Size, ModelChoice? Model = null)
{
    public bool SupportsGpu => Id.StartsWith("asr-whisper-", StringComparison.Ordinal);
    public bool HasDownload => Model is not null;

    /// <summary>How big the recognizer's download is ("488 MB"), shown beside its name; empty for one that downloads nothing.</summary>
    public string FileSize => Model?.Model.DownloadSize ?? "";
}

public sealed partial class SetupViewModel
{
    private HandyIntegration _handy = null!;
    private AsrChoice _selectedAsr = null!;
    private ComputeDevice _asrDevice = new("cpu", "CPU", true);
    public IReadOnlyList<AsrChoice> AsrModels { get; private set; } = [];
    public ObservableCollection<ComputeDevice> AsrDevices { get; } = [new("cpu", "CPU", true)];
    public HandyInstallation Handy { get; private set; } = new(null, "Detecting Handy…", false, "Managed in Handy");
    public bool WindowsRecognizerAvailable { get; private set; }
    public ICommand OpenHandyCommand { get; private set; } = null!;
    public ICommand RefreshHandyCommand { get; private set; } = null!;
    public AsrChoice SelectedAsr
    {
        get => _selectedAsr;
        set { if (value is not null && Set(ref _selectedAsr, value)) { AsrBenchmark = ""; PopulateAsrDevices(); Changed(); ApplyVoiceControlAtOnce(); } }
    }
    public ComputeDevice AsrDevice { get => _asrDevice; set { if (value is not null && Set(ref _asrDevice, value)) { AsrBenchmark = ""; Changed(); ApplyVoiceControlAtOnce(); } } }
    public bool UsesHandy => SelectedAsr.Id == "handy";
    public bool UsesWindowsAsr => SelectedAsr.Id == "windows";
    public bool UsesLocalAsr => SelectedAsr.Model is not null;
    public string HandyButtonLabel => Handy.Installed ? "Open Handy" : "Get Handy";
    public string HandyStatus => Handy.Installed ? !Handy.PasteCompatible ? "Handy detected · choose Ctrl+V paste and disable automatic submission in Handy" : Handy.ModelInstalled ? "Handy detected · " + Handy.ModelName : "Handy detected · select and download a model in Handy" : "Handy isn't installed. Get it to share its recognizer.";
    public string AsrInstallState => IsRecognizerInstalled ? UsesHandy ? "Ready · shared with Handy" : "Ready on this PC" : UsesWindowsAsr ? "Install a speech language in Windows Settings → Time & language → Speech, then refresh." : "Download a model to enable speech recognition.";
    public string AsrDeviceNote => UsesHandy ? Handy.Device + ". Choose a different device in Handy." : AsrDevice.Note ?? "";
    private void InitializeAsrChoices(HandyIntegration handy)
    {
        _handy = handy;
        AsrModels = [
            new("handy", "Use Handy's recognizer", "Recommended if you use Handy · one shared ASR model", "No additional ASR model memory in Kiri", "No additional model download"),
            new("windows", "Windows speech recognition", "Built-in local Windows recognizer · installed speech language", "0.1–0.3 GB RAM estimated", "No Kiri model download"),
            .. DownloadCatalog.SpeechRecognizers.Select(model => new AsrChoice(model.Id, model.Name, model.Detail, model.RamEstimate + " estimated", model.DownloadSize + " download", model.Id == Recognizer.Id ? Recognizer : new(model, _library)))
        ];
        _selectedAsr = AsrModels.Last();
        OpenHandyCommand = new Assistant.UI.ViewModels.RelayCommand(_ => _ = RunAsync(() => { _handy.Open(); return Task.CompletedTask; }), _ => IsIdle);
        RefreshHandyCommand = new Assistant.UI.ViewModels.RelayCommand(_ => _ = RunAsync(RefreshHandyAsync), _ => IsIdle);
    }
    private async Task RefreshHandyAsync()
    {
        Handy = await Task.Run(_handy.Detect);
        WindowsRecognizerAvailable = await Task.Run(WindowsSpeechRecognizer.IsAvailable);
        OnPropertyChanged(nameof(Handy)); OnPropertyChanged(nameof(WindowsRecognizerAvailable));
        PopulateAsrDevices(); Changed();
    }
    private void PopulateAsrDevices()
    {
        var wanted = _asrDevice.Id;
        AsrDevices.Clear();
        AsrDevices.Add(new("cpu", UsesHandy ? "Managed in Handy" : "CPU", true, "Runs locally on the processor. RAM estimates include recognition buffers for a short utterance."));
        if (SelectedAsr.SupportsGpu)
        {
            var available = NativeLibrary.TryLoad("vulkan-1.dll", out var handle);
            if (available) NativeLibrary.Free(handle);
            AsrDevices.Add(new("vulkan", "GPU · Vulkan (automatic device)", available, available ? "Uses the graphics driver's default Vulkan GPU. Some memory moves to VRAM; the total depends on the model and audio length." : "Install a graphics driver with Vulkan support, or choose CPU."));
        }
        _asrDevice = AsrDevices.FirstOrDefault(device => device.Id == wanted && device.Available) ?? AsrDevices[0];
        OnPropertyChanged(nameof(AsrDevice));
    }
    private void AsrChanged()
    {
        foreach (var name in new[] { nameof(UsesHandy), nameof(UsesWindowsAsr), nameof(UsesLocalAsr), nameof(HandyStatus), nameof(HandyButtonLabel), nameof(AsrDeviceNote), nameof(AsrInstallState) }) OnPropertyChanged(name);
    }
}
