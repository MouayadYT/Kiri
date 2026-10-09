using System.IO;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Assets;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Hardware;
using Assistant.Core.ModelHosting;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// Model (PROJECT_SPEC §5.6, §5.10): the model profiles, which one is used, where the models are kept and whether the
/// chosen one is there, the local model's status, what this PC offers a model and which profile and window it recommends
/// (step 124), the hardware preset, how much of its context window a conversation may
/// use (the normal and the heavy limit), and, under Advanced, a context window of the user's own. A number that may need
/// a lot of memory is warned about and still saved, because the user may know better; only a number outside what the
/// engine can be given is refused. A model file the user picked by hand overrides the profiles, and the page says so.
/// The recommendation is only a default: choosing a profile, a preset or a window oneself always wins over it.
/// A model that came packaged with the Assistant says whether its files match the package's checksums (step 123).
/// The model in memory is not changed by what is chosen here: it applies the next time the model is loaded.
/// </summary>
public sealed class ModelPage : SettingsPage, IDisposable
{
    private readonly IModelProfileCatalog _catalog;
    private readonly IModelProfileResolver _resolver;
    private readonly IHardwareInfoProvider _hardware;
    private readonly IModelLifecycle _lifecycle;
    private readonly AppPaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _subscription;
    private readonly IDisposable? _assetSubscription;
    private readonly IHardwareProfileService? _hardwareProfile;
    private readonly IModelRecommender? _recommender;
    private readonly IPackagedAssets? _assets;
    private readonly RelayCommand _useProfileInstead;
    private readonly RelayCommand _refreshHardware;
    private ModelStatusChanged _status;
    private ModelProfileItem? _selectedProfile;
    private bool _automaticProfile;
    private bool _readingHardware;
    private string _processorText = string.Empty;
    private string _systemMemoryText = string.Empty;
    private string _graphicsText = string.Empty;
    private string _recommendationText = string.Empty;
    private string _recommendationWhy = string.Empty;
    private ModelProfile? _recommendedProfile;
    private PresetChoice _selectedPreset;
    private string _ownFilePath = string.Empty;
    private string _modelsFolder = string.Empty;
    private string _windowSummary = string.Empty;
    private bool _useCustomWindow;
    private bool _useGpu;
    private ModelProfile? _effectiveProfile;
    private HardwarePreset? _effectivePreset;
    private int _effectiveWindow = ModelFiles.DefaultContextLength;
    private bool _disposed;

    internal ModelPage(
        SettingsViewModel root, IModelProfileCatalog catalog, IModelProfileResolver resolver, IHardwareInfoProvider hardware,
        IModelLifecycle lifecycle, IAppEventBus events, AppPaths paths, Dispatcher dispatcher,
        IHardwareProfileService? hardwareProfile = null, IModelRecommender? recommender = null, IPackagedAssets? assets = null)
        : base(root, SettingsSection.Model)
    {
        _catalog = catalog;
        _resolver = resolver;
        _hardware = hardware;
        _lifecycle = lifecycle;
        _paths = paths;
        _dispatcher = dispatcher;
        _hardwareProfile = hardwareProfile;
        _recommender = recommender;
        _assets = assets;
        _status = lifecycle.Current;
        _refreshHardware = new RelayCommand(RefreshHardware, _ => _hardwareProfile is not null && !_readingHardware);
        Profiles = [.. catalog.Profiles.Select(profile => new ModelProfileItem(profile, ReferenceEquals(profile, catalog.Default), CheckProfile))];
        var automatic = HardwarePresets.Select(hardware.Get());
        Presets =
        [
            new PresetChoice(null, $"Automatic ({PresetName(automatic)} for this PC)"),
            .. HardwarePresets.All.Select(preset => new PresetChoice(preset.Id, preset.DisplayName)),
        ];
        _selectedPreset = Presets[0];
        _useProfileInstead = new RelayCommand(UseProfileInstead);
        NormalLimit = new NumberField(
            tokens => ContextAdvisor.AssessLimit(tokens, SettingsLimits.MaxContextTokens),
            tokens => SetContextLimit(tokens, heavy: false));
        HeavyLimit = new NumberField(
            tokens => ContextAdvisor.AssessHeavyLimit(Current.ContextLimits.NormalContextTokens, tokens, SettingsLimits.MaxContextTokens),
            tokens => SetContextLimit(tokens, heavy: true));
        CustomWindow = new NumberField(
            tokens => ContextAdvisor.AssessWindow(tokens, _effectiveProfile, hardware.Get(), _effectivePreset),
            tokens => Commit(settings => settings with { Model = settings.Model with { ContextLength = tokens } }));
        _subscription = events.Subscribe<ModelPage, ModelStatusChanged>(
            this, static (page, status, _) => page.OnStatusChanged(status));
        _assetSubscription = assets is null
            ? null
            : events.Subscribe<ModelPage, AssetStateChanged>(this, static (page, changed, _) => page.OnAssetChanged(changed));
        if (hardwareProfile is not null)
        {
            ShowHardware(hardwareProfile.Current);
        }
    }

    /// <summary>The model profiles, the default first.</summary>
    public IReadOnlyList<ModelProfileItem> Profiles { get; }

    /// <summary>
    /// The profile that will be used. Choosing one saves it, for the next time the model is loaded, and turns off
    /// <see cref="AutomaticProfile"/>: a profile the user chose is used whatever is recommended.
    /// </summary>
    public ModelProfileItem? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (Set(ref _selectedProfile, value) && value is not null)
            {
                // Without a recommender nothing is recommended, so "no choice" and the default profile are the same thing and the default is saved as none.
                var saved = _recommender is null && value.IsDefault ? null : value.Id;
                Commit(settings => settings with { Model = settings.Model with { ProfileId = saved } });
            }
        }
    }

    /// <summary>Whether this PC has a recommendation to follow: the page offers it, and a profile that was not chosen is the recommended one.</summary>
    public bool HasRecommendation => _recommender is not null;

    /// <summary>
    /// Whether the Assistant chooses the profile for this PC, from its hardware. Turning it off keeps the profile in use now as the user's choice;
    /// turning it on forgets the choice, and the recommended one (or the nearest installed) is used again.
    /// </summary>
    public bool AutomaticProfile
    {
        get => _automaticProfile;
        set
        {
            if (Set(ref _automaticProfile, value))
            {
                var keep = _selectedProfile?.Id ?? _catalog.Default.Id;
                Commit(settings => settings with { Model = settings.Model with { ProfileId = value ? null : keep } });
            }
        }
    }

    /// <summary>Whether the hardware could be read at all, so that the page shows what this PC offers.</summary>
    public bool HasHardwareProfile => _hardwareProfile is not null;

    /// <summary>The processor, in words.</summary>
    public string ProcessorText
    {
        get => _processorText;
        private set => Set(ref _processorText, value);
    }

    /// <summary>The memory and how much of it is free, in words.</summary>
    public string SystemMemoryText
    {
        get => _systemMemoryText;
        private set => Set(ref _systemMemoryText, value);
    }

    /// <summary>The graphics cards with the memory each has of its own, one on each line.</summary>
    public string GraphicsText
    {
        get => _graphicsText;
        private set => Set(ref _graphicsText, value);
    }

    /// <summary>The profile and window recommended for this PC, in a sentence.</summary>
    public string RecommendationText
    {
        get => _recommendationText;
        private set => Set(ref _recommendationText, value);
    }

    /// <summary>Why, in a sentence or two.</summary>
    public string RecommendationWhy
    {
        get => _recommendationWhy;
        private set => Set(ref _recommendationWhy, value);
    }

    /// <summary>Whether the hardware is being read again.</summary>
    public bool IsReadingHardware
    {
        get => _readingHardware;
        private set
        {
            if (Set(ref _readingHardware, value))
            {
                _refreshHardware.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Reads the hardware again, which is what changes when other programs use the memory and the graphics card.</summary>
    public ICommand RefreshHardwareCommand => _refreshHardware;

    /// <summary>Whether the user picked a model file by hand, which is used instead of any profile.</summary>
    public bool UsesOwnFile => _ownFilePath.Length > 0;

    /// <summary>Whether a profile can be chosen: not while a model file the user picked is used instead.</summary>
    public bool CanChooseProfile => !UsesOwnFile;

    /// <summary>The model file the user picked by hand, or empty.</summary>
    public string OwnFilePath
    {
        get => _ownFilePath;
        private set
        {
            if (Set(ref _ownFilePath, value))
            {
                OnPropertyChanged(nameof(UsesOwnFile));
                OnPropertyChanged(nameof(CanChooseProfile));
            }
        }
    }

    /// <summary>Stops using the model file picked by hand, so the chosen profile is used.</summary>
    public ICommand UseProfileInsteadCommand => _useProfileInstead;

    /// <summary>Where the models are kept.</summary>
    public string ModelsFolder
    {
        get => _modelsFolder;
        private set => Set(ref _modelsFolder, value);
    }

    /// <summary>The hardware presets, the automatic choice first.</summary>
    public IReadOnlyList<PresetChoice> Presets { get; }

    /// <summary>The preset the model is run with. Automatic picks the one that suits this PC's memory.</summary>
    public PresetChoice SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is not null && Set(ref _selectedPreset, value))
            {
                Commit(settings => settings with { Model = settings.Model with { HardwarePresetId = value.Id } });
            }
        }
    }

    /// <summary>How much memory this PC has, in words.</summary>
    public string MemoryText
    {
        get
        {
            var hardware = _hardware.Get();
            return hardware.IsMemoryKnown
                ? $"This PC has {hardware.TotalMemoryGiB.ToString("0.#", CultureInfo.CurrentCulture)} GB of memory."
                : "This PC's memory could not be read.";
        }
    }

    /// <summary>The local model's status in plain words.</summary>
    public string StatusText => ModelStatusText.Describe(_status.Status, _status.Failure);

    /// <summary>What is loaded, when a model is: its identifier and the window it was loaded with. Otherwise empty.</summary>
    public string LoadedText => _lifecycle.Model is { } model
        ? $"Loaded: {model.Id}, with a window of {NumberField.Format(model.ContextLength)} tokens."
        : string.Empty;

    /// <summary>Whether the model is loading or unloading.</summary>
    public bool IsBusy => _status.Status is ModelStatus.Loading or ModelStatus.Unloading;

    /// <summary>Whether the model failed, so the status is shown as a problem.</summary>
    public bool IsFailed => _status.Status == ModelStatus.Failed;

    /// <summary>Whether the model is loaded and ready.</summary>
    public bool IsReady => _status.Status == ModelStatus.Ready;

    /// <summary>The most of the window an ordinary conversation may use, in tokens, or 0 for no limit.</summary>
    public NumberField NormalLimit { get; }

    /// <summary>The most of the window a conversation with attached or retrieved context may use, in tokens.</summary>
    public NumberField HeavyLimit { get; }

    private void SetContextLimit(int tokens, bool heavy) => Commit(settings =>
    {
        var limits = heavy ? settings.ContextLimits with { HeavyContextTokens = tokens } : settings.ContextLimits with { NormalContextTokens = tokens };

        // The two limits are the two windows the model is loaded with (ContextWindowPlan): no single window is written down beside them, so the
        // model has the ordinary one, and the larger only while a conversation carries files. It is put away so that its next use loads it as set.
        return settings with { ContextLimits = limits, Model = settings.Model with { ContextLength = null } };
    }, async () => { await _lifecycle.UnloadAsync(); return true; }, "The model could not apply the new context size. Try again.");

    /// <summary>Whether the user sets the context window the model is loaded with, instead of leaving it automatic.</summary>
    public bool UseCustomWindow
    {
        get => _useCustomWindow;
        set
        {
            if (Set(ref _useCustomWindow, value))
            {
                var window = _effectiveWindow;
                Commit(settings => settings with { Model = settings.Model with { ContextLength = value ? window : null } });
            }
        }
    }

    /// <summary>The context window to load the model with, in tokens, while <see cref="UseCustomWindow"/> is on.</summary>
    public NumberField CustomWindow { get; }

    /// <summary>What window the model is given, and why, in words.</summary>
    public string WindowSummary
    {
        get => _windowSummary;
        private set => Set(ref _windowSummary, value);
    }

    /// <summary>Whether the model uses the graphics card when there is one.</summary>
    public bool UseGpuAcceleration
    {
        get => _useGpu;
        set
        {
            if (Set(ref _useGpu, value))
            {
                Commit(settings => settings with { Model = settings.Model with { UseGpuAcceleration = value } });

                // A model that is loaded keeps the devices it started on: put it away, so the next question loads it as chosen.
                if (!IsApplying && _lifecycle.Model is not null)
                {
                    _ = _lifecycle.UnloadAsync();
                }
            }
        }
    }

    internal override void Apply(AppSettings settings, bool fresh)
    {
        var model = settings.Model;
        OwnFilePath = string.IsNullOrWhiteSpace(model.ModelFilePath) ? string.Empty : model.ModelFilePath;
        ModelsFolder = !string.IsNullOrWhiteSpace(model.ModelsDirectory) && Path.IsPathFullyQualified(model.ModelsDirectory)
            ? model.ModelsDirectory
            : _paths.ModelsDirectory;

        ShowProfiles(model);

        // With no profile chosen the one in use is whichever the resolver picks: the recommended one, or the nearest installed.
        var resolved = _resolver.Resolve(model);
        var chosen = model.ProfileId is null ? resolved?.Profile ?? _catalog.Default : _catalog.Find(model.ProfileId);
        SelectedProfile = Profiles.FirstOrDefault(item => ReferenceEquals(item.Profile, chosen));
        AutomaticProfile = model.ProfileId is null && _recommender is not null;
        SelectedPreset = Presets.FirstOrDefault(preset => preset.Id == model.HardwarePresetId) ?? Presets[0];
        UseGpuAcceleration = model.UseGpuAcceleration;

        _effectiveProfile = UsesOwnFile ? null : resolved?.Profile;
        _effectivePreset = resolved?.Preset ?? HardwarePresets.Select(_hardware.Get());
        _effectiveWindow = WindowFor(settings, resolved, documents: false);
        UseCustomWindow = model.ContextLength is not null;
        WindowSummary = Summarize(settings, resolved);

        Show(NormalLimit, settings.ContextLimits.NormalContextTokens, fresh);
        Show(HeavyLimit, settings.ContextLimits.HeavyContextTokens, fresh);
        Show(CustomWindow, _effectiveWindow, fresh);
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LoadedText));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _subscription.Dispose();
            _assetSubscription?.Dispose();
        }
    }

    // Each profile's files and where the package says they stand (no file is read: that is what Check files is for), and which one is recommended.
    private void ShowProfiles(ModelSettings model)
    {
        foreach (var item in Profiles)
        {
            item.Show(_resolver.Resolve(model with { ProfileId = item.Id }), _assets?.Peek(AssetKind.Model, item.Id));
            item.IsRecommended = _recommendedProfile is not null && ReferenceEquals(item.Profile, _recommendedProfile);
        }
    }

    private void ShowHardware(HardwareProfile profile)
    {
        var culture = CultureInfo.CurrentCulture;
        var cpu = profile.Cpu;
        ProcessorText = profile.Unreadable.HasFlag(HardwareParts.Processor)
            ? "The processor could not be read."
            : cpu.PhysicalCores > 0
                ? $"{(cpu.Name.Length > 0 ? cpu.Name : "Processor")} · {cpu.PhysicalCores} cores, {cpu.LogicalProcessors} threads"
                : $"{(cpu.Name.Length > 0 ? cpu.Name : "Processor")} · {cpu.LogicalProcessors} threads";
        SystemMemoryText = profile.Memory.IsKnown
            ? $"{Gigabytes(profile.Memory.TotalGiB, culture)} of memory, {Gigabytes(profile.Memory.AvailableGiB, culture)} free"
            : "The memory could not be read.";
        GraphicsText = Graphics(profile, culture);

        var recommendation = _recommender?.Recommend(profile);
        _recommendedProfile = recommendation?.Profile;
        RecommendationText = recommendation is null
            ? string.Empty
            : $"Recommended for this PC: {recommendation.Profile.DisplayName}, with a window of {NumberField.Format(recommendation.ContextTokens)} tokens.";
        RecommendationWhy = recommendation is null ? string.Empty : string.Join(' ', recommendation.Reasons);
    }

    private static string Graphics(HardwareProfile profile, CultureInfo culture)
    {
        if (profile.Unreadable.HasFlag(HardwareParts.Graphics))
        {
            return "The graphics cards could not be read.";
        }

        // A software renderer is not a card: it never runs a model, so it is not listed.
        var cards = profile.Gpus.Where(gpu => gpu.Kind == GpuAdapterKind.Hardware).ToArray();
        if (cards.Length == 0)
        {
            return "No graphics card was found, so the model runs on the processor.";
        }

        return string.Join('\n', cards.Select(card => card.HasDedicatedMemory
            ? $"{card.Name} · {Gigabytes(card.DedicatedVideoMemoryGiB, culture)} of its own memory"
              + (card.AvailableDedicatedVideoMemoryGiB is { } free ? $", {Gigabytes(free, culture)} free" : string.Empty)
            : $"{card.Name} · shares the PC's memory"));
    }

    private static string Gigabytes(double gibibytes, CultureInfo culture) => $"{gibibytes.ToString("0.#", culture)} GB";

    private void RefreshHardware(object? parameter)
    {
        if (_hardwareProfile is null || _readingHardware)
        {
            return;
        }

        IsReadingHardware = true;
        _ = RefreshHardwareAsync();
    }

    // Reading the graphics cards is a call into the drivers, so it is not made on the UI thread.
    private async Task RefreshHardwareAsync()
    {
        try
        {
            var profile = await Task.Run(() => _hardwareProfile!.Refresh()).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                ShowHardware(profile);
                ShowProfiles(Current.Model);
                OnPropertyChanged(nameof(MemoryText));
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsReadingHardware = false);
        }
    }

    private void CheckProfile(ModelProfileItem item)
    {
        if (_assets is not null)
        {
            _ = CheckProfileAsync(_assets, item);
        }
    }

    // Every file is read in full, whatever was checked before. The state is shown as it changes (OnAssetChanged), so nothing is shown here.
    private static async Task CheckProfileAsync(IPackagedAssets assets, ModelProfileItem item)
    {
        try
        {
            await assets.VerifyAsync(AssetKind.Model, item.Id, AssetCheckMode.Reverify).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // The asset's own state says that it could not be read.
        }
    }

    // Called on the thread that finished or began a check.
    private Task OnAssetChanged(AssetStateChanged changed) =>
        changed.Kind != AssetKind.Model
            ? Task.CompletedTask
            : _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    ShowProfiles(Current.Model);
                }
            }).Task;

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

    private void UseProfileInstead() =>
        Commit(settings => settings with
        {
            Model = settings.Model with { ModelFilePath = null, ProjectorFilePath = null, ChatTemplateFilePath = null },
        });

    // The window the model is loaded with for a conversation with or without files, as the model service works it out.
    private int WindowFor(AppSettings settings, ResolvedModel? resolved, bool documents) => ContextWindowPlan.Window(
        settings.Model.ContextLength, settings.ContextLimits, documents, UsesOwnFile ? null : resolved?.Files.ContextLength,
        UsesOwnFile ? null : resolved?.Profile, _hardware.Get(),
        ceiling: UsesOwnFile || settings.Model.HardwarePresetId is null ? null : resolved?.Preset.MaxContextTokens,
        modelFileBytes: OwnFileBytes(settings));

    // How long the files of a model the user picked are, which is what its memory is worked out from; null for a profile's model.
    private long? OwnFileBytes(AppSettings settings) =>
        UsesOwnFile ? ContextWindowPlan.FileBytes(settings.Model.ModelFilePath, settings.Model.ProjectorFilePath) : null;

    // What window the model has and when, with about how much memory each takes.
    private string Summarize(AppSettings settings, ResolvedModel? resolved)
    {
        var profile = UsesOwnFile ? null : resolved?.Profile;
        var ownFile = OwnFileBytes(settings);
        string Memory(int tokens) =>
            (ContextWindowPlan.EstimateMemoryBytes(profile, ownFile, tokens) / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
        var ordinary = WindowFor(settings, resolved, documents: false);
        if (settings.Model.ContextLength is not null)
        {
            return $"The model is loaded with a window of {NumberField.Format(ordinary)} tokens, as it was set (about {Memory(ordinary)} of memory).";
        }

        var files = WindowFor(settings, resolved, documents: true);
        var summary = $"The model is loaded with {NumberField.Format(ordinary)} tokens (about {Memory(ordinary)} of memory).";
        if (files == ordinary)
        {
            return summary + HeldNote(settings, resolved, ordinary, files);
        }

        summary += $" When a conversation carries files it is loaded again with {NumberField.Format(files)} (about {Memory(files)}), which takes a moment, and goes back afterwards.";
        return summary + HeldNote(settings, resolved, ordinary, files);
    }

    // Why a window is less than the limit set for it: the preset the user chose, or what this PC's memory would hold.
    private string HeldNote(AppSettings settings, ResolvedModel? resolved, int ordinary, int files)
    {
        var limits = settings.ContextLimits;
        var askedFiles = Math.Max(limits.HeavyContextTokens, limits.NormalContextTokens);
        if (!UsesOwnFile && settings.Model.HardwarePresetId is not null && resolved is not null
            && (ordinary < limits.NormalContextTokens || files < askedFiles))
        {
            return $" It is held to what the {PresetName(resolved.Preset)} preset allows.";
        }

        return files < askedFiles ? $" That is less than the {NumberField.Format(askedFiles)} set for files, which this PC's memory would not hold." : string.Empty;
    }

    private static string PresetName(HardwarePreset preset)
    {
        var open = preset.DisplayName.IndexOf(" (", StringComparison.Ordinal);
        return open > 0 ? preset.DisplayName[..open] : preset.DisplayName;
    }

    // Called on the lifecycle's publishing thread.
    private Task OnStatusChanged(ModelStatusChanged status) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _status == status)
            {
                return;
            }

            _status = status;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LoadedText));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsReady));
        }).Task;
}
