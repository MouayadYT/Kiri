using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using Assistant.Core.Assets;
using Assistant.Core.Events;
using Assistant.Core.Hardware;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.UI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The Model page's hardware, recommendation and packaged files (steps 123 and 124), and the Voice page's engines ------------------------

    private static HardwareProfile TestPc(double memoryGiB, params GpuAdapterInfo[] gpus) =>
        new(
            new CpuInfo("Test CPU", 8, 16, "X64", 3000),
            new SystemMemoryInfo((long)(memoryGiB * SettingsGiB), (long)(memoryGiB * SettingsGiB / 2)), gpus);

    private static GpuAdapterInfo TestCard(string name, double gib, double? freeGib = null) =>
        new(name, 0x10DE, GpuAdapterKind.Hardware, (long)(gib * SettingsGiB), 16 * SettingsGiB, freeGib is { } free ? (long)(free * SettingsGiB) : null);

    // The Settings window's view model with this PC's hardware, the recommender, and packaged assets in a folder of their own.
    private static ModelKit CreateModelKit(HardwareProfile pc, AppSettings? saved = null, bool withAssets = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-model-kit-" + Guid.NewGuid().ToString("N"));
        var install = new PackagedAssetPaths(Path.Combine(root, "install"));
        var paths = new AppPaths(Path.Combine(root, "data"));
        var settings = new InMemorySettingsService();
        if (saved is not null)
        {
            settings.SaveAsync(saved).GetAwaiter().GetResult();
        }

        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var hardwareProfile = new SettableProfile(pc);
        var catalog = new ModelProfileCatalog();
        var hardware = new HardwareProfileInfoProvider(hardwareProfile, new FixedHardware(new HardwareInfo(0, 4)));
        var recommender = new ModelRecommender(catalog, hardwareProfile);
        var resolver = new ModelProfileResolver(catalog, hardware, paths, recommender: recommender, packagedModelsDirectory: install.ModelsDirectory);
        var assets = new PackagedAssets(install, NoAssetCheckCache.Instance, bus, NullLogger<PackagedAssets>.Instance);
        var voices = new TextToSpeechAssets(assets);
        var model = new SettingsViewModel(
            settings, catalog, resolver, hardware, new SettingsLifecycle(), bus, paths, null, System.Windows.Threading.Dispatcher.CurrentDispatcher,
            hardwareProfile: hardwareProfile, recommender: recommender, packagedAssets: withAssets ? assets : null, voices: withAssets ? voices : null);
        model.LoadAsync().GetAwaiter().GetResult();
        return new ModelKit(model, settings, install, paths, hardwareProfile, assets, root);
    }

    // Puts the files of a group in the packaged folder of a kind and lists them in its manifest.
    private static void PackageAsset(ModelKit kit, AssetKind kind, string id, params (string Name, string Content)[] files)
    {
        var folder = Path.Combine(kit.Install.DirectoryOf(kind), id);
        Directory.CreateDirectory(folder);
        var entries = new List<AssetFile>();
        foreach (var (name, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            entries.Add(new AssetFile(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        var manifestPath = kit.Install.ManifestOf(kind);
        var groups = File.Exists(manifestPath)
            ? [.. AssetManifestReader.Parse(File.ReadAllBytes(manifestPath)).Groups.Where(group => group.Id != id)]
            : new List<AssetGroup>();
        groups.Add(new AssetGroup(id, entries));
        File.WriteAllText(manifestPath, new AssetManifest(groups).ToJson());
        kit.Model.LoadAsync().GetAwaiter().GetResult();
    }

    // Waits, pumping the UI thread the page's updates arrive on, until the condition holds.
    private static void SettingsUntil(Func<bool> condition, string what)
    {
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            Pump();
            Thread.Sleep(5);
        }

        Assert.True(condition(), what);
    }

    private sealed record ModelKit(
        SettingsViewModel Model, InMemorySettingsService Settings, PackagedAssetPaths Install, AppPaths Paths, SettableProfile Hardware,
        PackagedAssets Assets, string Root) : IDisposable
    {
        public AppSettings Saved => Settings.LoadAsync().GetAwaiter().GetResult();

        public void Settle() => Model.WhenSavedAsync().GetAwaiter().GetResult();

        public void Dispose()
        {
            Model.Dispose();
            Assets.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class SettableProfile(HardwareProfile profile) : IHardwareProfileService
    {
        public HardwareProfile Next { get; set; } = profile;

        public int Refreshes { get; private set; }

        public HardwareProfile Current { get; private set; } = profile;

        public HardwareProfile Refresh()
        {
            Refreshes++;
            return Current = Next;
        }
    }

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of the Model page with this PC, its recommendation and a packaged model, and of the Voice page with its engines.
    [Fact]
    public void RenderTheHardwareAndPackagedAssetPagesWhenAskedTo() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var software = new GpuAdapterInfo("Microsoft Basic Render Driver", 0x1414, GpuAdapterKind.Software, 0, 8 * SettingsGiB, null);
        using var kit = CreateModelKit(TestPc(64, TestCard("NVIDIA GeForce RTX 5070", 11.7, 10.9), TestCard("NVIDIA GeForce RTX 2080", 7.8, 7.1), software));
        PackageAsset(kit, AssetKind.Model, "chat-4b", ("model.gguf", "small model"), ("mmproj.gguf", "small projector"));
        PackageAsset(kit, AssetKind.Voice, "kitten-tts-mini", ("voice.onnx", "voice"));
        PackageAsset(kit, AssetKind.Voice, "piper", ("voice.onnx", "voice"));
        File.WriteAllText(Path.Combine(kit.Install.VoicesDirectory, "piper", "voice.onnx"), "VOICE");
        kit.Model.Model.Profiles[0].CheckCommand.Execute(null);
        kit.Model.Voice.CheckFilesCommand.Execute(null);
        SettingsUntil(() => !kit.Model.Voice.IsChecking && kit.Assets.Peek(AssetKind.Model, "chat-4b").IsVerified, "the files were checked");

        var (window, _, _) = CreateSettingsWindow(new SettingsKit(kit.Model, kit.Settings, new SettingsLifecycle(), new AppEventBus(NullLogger<AppEventBus>.Instance), []));
        try
        {
            window.Height = 1700;
            window.Show();
            Pump();
            foreach (var section in new[] { SettingsSection.Model, SettingsSection.Voice })
            {
                kit.Model.SelectedSection = kit.Model.Sections.Single(item => item.Section == section);
                window.UpdateLayout();
                Pump();
                RenderFixture(Named<Grid>(window, "Root"), $"settings-hardware-{section.ToString().ToLowerInvariant()}.png", 1);
            }
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    [Fact]
    public void ThisPCShowsTheProcessorTheMemoryAndTheCardsWithTheirOwnMemory() => RunSta(() => WithCulture("en-US", () =>
    {
        var software = new GpuAdapterInfo("Basic Render Driver", 0x1414, GpuAdapterKind.Software, 0, 8 * SettingsGiB, null);
        var shared = new GpuAdapterInfo("Built-in graphics", 0x8086, GpuAdapterKind.Hardware, 0, 16 * SettingsGiB, null);
        using var kit = CreateModelKit(TestPc(64, TestCard("Card A", 12, 10.5), TestCard("Card B", 8), shared, software));
        var page = kit.Model.Model;

        Assert.True(page.HasHardwareProfile);
        Assert.Equal("Test CPU · 8 cores, 16 threads", page.ProcessorText);
        Assert.Equal("64 GB of memory, 32 GB free", page.SystemMemoryText);
        Assert.Equal(
            "Card A · 12 GB of its own memory, 10.5 GB free\nCard B · 8 GB of its own memory\nBuilt-in graphics · shares the PC's memory",
            page.GraphicsText);
        Assert.DoesNotContain("Basic Render", page.GraphicsText, StringComparison.Ordinal);
    }));

    [Fact]
    public void WithNoCardOrAnUnreadablePart_ThePageSaysSo() => RunSta(() => WithCulture("en-US", () =>
    {
        using var none = CreateModelKit(TestPc(32));
        using var unreadable = CreateModelKit(HardwareProfile.Unknown);

        Assert.Equal("No graphics card was found, so the model runs on the processor.", none.Model.Model.GraphicsText);
        Assert.Equal("The processor could not be read.", unreadable.Model.Model.ProcessorText);
        Assert.Equal("The memory could not be read.", unreadable.Model.Model.SystemMemoryText);
        Assert.Equal("The graphics cards could not be read.", unreadable.Model.Model.GraphicsText);
    }));

    [Fact]
    public void TheRecommendationIsShownWithItsReasons_AndMarksTheRecommendedProfile() => RunSta(() => WithCulture("en-US", () =>
    {
        using var kit = CreateModelKit(TestPc(64, TestCard("Card A", 12)));
        var page = kit.Model.Model;

        Assert.True(page.HasRecommendation);
        Assert.Equal("Recommended for this PC: Large (8-9B class), with a window of 32,768 tokens.", page.RecommendationText);
        Assert.Contains("Card A has 12 GB of memory of its own, enough for the Large (8-9B class) model", page.RecommendationWhy, StringComparison.Ordinal);
        Assert.False(page.Profiles[0].IsRecommended);
        Assert.True(page.Profiles[1].IsRecommended);
    }));

    [Fact]
    public void WithNoChoiceMade_TheProfileIsChosenAutomatically_AndNothingIsSaved() => RunSta(() => WithCulture("en-US", () =>
    {
        using var kit = CreateModelKit(TestPc(64, TestCard("Card A", 12)));
        var page = kit.Model.Model;
        PackageAsset(kit, AssetKind.Model, "chat-4b", ("model.gguf", "small"));
        PackageAsset(kit, AssetKind.Model, "chat-9b", ("model.gguf", "large"));

        Assert.True(page.AutomaticProfile);
        Assert.Same(page.Profiles[1], page.SelectedProfile);
        Assert.Null(kit.Saved.Model.ProfileId);
        Assert.Contains("The model is loaded with 8,000 tokens", page.WindowSummary, StringComparison.Ordinal);
    }));

    [Fact]
    public void WhenTheRecommendedProfileIsNotInstalled_TheNearestInstalledOneIsShownAsInUse() => RunSta(() =>
    {
        using var kit = CreateModelKit(TestPc(64, TestCard("Card A", 12)));
        PackageAsset(kit, AssetKind.Model, "chat-4b", ("model.gguf", "small"));
        var page = kit.Model.Model;

        Assert.True(page.AutomaticProfile);
        Assert.Same(page.Profiles[0], page.SelectedProfile);
        Assert.True(page.Profiles[1].IsRecommended);
    });

    [Fact]
    public void ChoosingAProfileSavesItAsTheUsersChoice_EvenTheDefault_AndTurnsAutomaticOff() => RunSta(() =>
    {
        using var kit = CreateModelKit(TestPc(64, TestCard("Card A", 12)));
        var page = kit.Model.Model;

        page.SelectedProfile = page.Profiles[0];
        kit.Settle();

        Assert.Equal("chat-4b", kit.Saved.Model.ProfileId);
        Assert.False(page.AutomaticProfile);
        Assert.Same(page.Profiles[0], page.SelectedProfile);
        Assert.True(page.Profiles[1].IsRecommended);

        // The recommendation does not take it back.
        PackageAsset(kit, AssetKind.Model, "chat-4b", ("model.gguf", "small"));
        PackageAsset(kit, AssetKind.Model, "chat-9b", ("model.gguf", "large"));
        Assert.Same(page.Profiles[0], page.SelectedProfile);
    });

    [Fact]
    public void TurningAutomaticOffKeepsTheProfileInUse_AndTurningItOnForgetsTheChoice() => RunSta(() =>
    {
        using var kit = CreateModelKit(TestPc(64, TestCard("Card A", 12)));
        PackageAsset(kit, AssetKind.Model, "chat-9b", ("model.gguf", "large"));
        var page = kit.Model.Model;
        Assert.Same(page.Profiles[1], page.SelectedProfile);

        page.AutomaticProfile = false;
        kit.Settle();
        Assert.Equal("chat-9b", kit.Saved.Model.ProfileId);
        Assert.False(page.AutomaticProfile);

        page.AutomaticProfile = true;
        kit.Settle();
        Assert.Null(kit.Saved.Model.ProfileId);
        Assert.True(page.AutomaticProfile);
    });

    [Fact]
    public void AProfileSavedEarlier_IsTheUsersChoice_NotAutomatic() => RunSta(() =>
    {
        using var kit = CreateModelKit(TestPc(64, TestCard("Card A", 12)), new AppSettings { Model = new ModelSettings { ProfileId = "chat-4b" } });
        var page = kit.Model.Model;

        Assert.False(page.AutomaticProfile);
        Assert.Same(page.Profiles[0], page.SelectedProfile);
    });

    [Fact]
    public void ThePageWithoutHardwareOrARecommender_ShowsNoneOfIt() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Model;

        Assert.False(page.HasHardwareProfile);
        Assert.False(page.HasRecommendation);
        Assert.False(page.AutomaticProfile);
        Assert.Equal(string.Empty, page.RecommendationText);
        Assert.All(page.Profiles, profile => Assert.False(profile.IsRecommended));
    });

    [Fact]
    public void CheckAgain_ReadsTheHardwareAgain_AndShowsWhatIsFreeNow() => RunSta(() => WithCulture("en-US", () =>
    {
        using var kit = CreateModelKit(TestPc(64, TestCard("Card A", 12, 11)));
        var page = kit.Model.Model;
        Assert.Contains("11 GB free", page.GraphicsText, StringComparison.Ordinal);
        kit.Hardware.Next = TestPc(64, TestCard("Card A", 12, 2));

        page.RefreshHardwareCommand.Execute(null);
        Assert.True(page.IsReadingHardware);
        Assert.False(page.RefreshHardwareCommand.CanExecute(null));
        SettingsUntil(() => !page.IsReadingHardware, "the hardware was read again");

        Assert.Equal(1, kit.Hardware.Refreshes);
        Assert.Contains("2 GB free", page.GraphicsText, StringComparison.Ordinal);
        Assert.True(page.RefreshHardwareCommand.CanExecute(null));
    }));

    [Fact]
    public void APackagedModelSaysItsFilesAreCheckedBeforeFirstUse_ThenThatTheyMatch() => RunSta(() => WithCulture("en-US", () =>
    {
        using var kit = CreateModelKit(TestPc(16));
        PackageAsset(kit, AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        var standard = kit.Model.Model.Profiles[0];

        Assert.True(standard.IsInstalled);
        Assert.Equal("Installed with the Assistant. Its files are checked before it is first used. Its projector file is missing, so it can't read images.", standard.Status);
        Assert.True(standard.CanCheck);
        Assert.Equal(Path.Combine(kit.Install.ModelsDirectory, "chat-4b"), standard.Location);

        standard.CheckCommand.Execute(null);
        SettingsUntil(
            () => standard.Status.StartsWith("Installed with the Assistant, and its files match", StringComparison.Ordinal), "the files were checked");

        Assert.True(kit.Assets.Peek(AssetKind.Model, "chat-4b").IsVerified);
    }));

    [Fact]
    public void APackagedModelWhoseFileChanged_SaysSoAndNamesTheFile() => RunSta(() => WithCulture("en-US", () =>
    {
        using var kit = CreateModelKit(TestPc(16));
        PackageAsset(kit, AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        File.WriteAllText(Path.Combine(kit.Install.ModelsDirectory, "chat-4b", "model.gguf"), "MODEL BYTES");
        var standard = kit.Model.Model.Profiles[0];

        standard.CheckCommand.Execute(null);
        SettingsUntil(() => standard.Status.Contains("do not match", StringComparison.Ordinal), "the damage was found");

        Assert.Equal("Its model files do not match what was packaged (model.gguf). Reinstall the Assistant to restore them.", standard.Status);
        Assert.True(standard.IsInstalled);
    }));

    [Fact]
    public void APackagedModelThatIsMissing_SaysSo_AndAModelNobodyPackagedKeepsTheOldWords() => RunSta(() => WithCulture("en-US", () =>
    {
        using var kit = CreateModelKit(TestPc(16));
        PackageAsset(kit, AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        File.Delete(Path.Combine(kit.Install.ModelsDirectory, "chat-4b", "model.gguf"));
        kit.Model.LoadAsync().GetAwaiter().GetResult();
        var page = kit.Model.Model;

        Assert.False(page.Profiles[0].IsInstalled);
        Assert.Equal("Not installed with this copy of the Assistant.", page.Profiles[0].Status);
        Assert.False(page.Profiles[0].CanCheck);
        Assert.StartsWith("Not installed. Put its model file in this folder", page.Profiles[1].Status, StringComparison.Ordinal);
    }));

    [Fact]
    public void TheVoicePageListsTheThreeEngines_AndSaysWhichAreInstalled() => RunSta(() =>
    {
        using var kit = CreateModelKit(TestPc(16));
        var page = kit.Model.Voice;

        Assert.True(page.HasEngines);
        Assert.Equal(["kitten-tts-mini", "kokoro-82m-onnx", "piper"], page.Engines.Select(engine => engine.Model.Id));
        Assert.All(page.Engines, engine => Assert.False(engine.IsInstalled));
        Assert.Equal("No voice is installed on this PC yet.", page.EnginesSummary);
        Assert.All(page.Engines, engine => Assert.Equal("Not installed with this copy of the Assistant.", engine.Status));
        Assert.Equal(Path.Combine(kit.Install.VoicesDirectory, "piper"), page.Engines[2].Location);

        PackageAsset(kit, AssetKind.Voice, "kitten-tts-mini", ("voice.onnx", "voice"));
        PackageAsset(kit, AssetKind.Voice, "piper", ("voice.onnx", "voice"));

        Assert.True(page.Engines[0].IsInstalled);
        Assert.False(page.Engines[0].IsAvailable);
        Assert.Equal("Installed. Its files are checked before it is first used.", page.Engines[0].Status);
        Assert.False(page.Engines[1].IsInstalled);
        Assert.Equal("2 of 3 voices are installed on this PC.", page.EnginesSummary);
    });

    [Fact]
    public void CheckingTheVoiceFiles_MakesTheInstalledEnginesAvailable_AndFlagsADamagedOne() => RunSta(() =>
    {
        using var kit = CreateModelKit(TestPc(16));
        PackageAsset(kit, AssetKind.Voice, "kitten-tts-mini", ("voice.onnx", "voice"));
        PackageAsset(kit, AssetKind.Voice, "piper", ("voice.onnx", "voice"));
        File.WriteAllText(Path.Combine(kit.Install.VoicesDirectory, "piper", "voice.onnx"), "VOICE");
        var page = kit.Model.Voice;

        page.CheckFilesCommand.Execute(null);
        Assert.True(page.IsChecking);
        SettingsUntil(() => !page.IsChecking, "the voices were checked");

        Assert.True(page.Engines[0].IsAvailable);
        Assert.Equal("Installed, and its files match what was packaged.", page.Engines[0].Status);
        Assert.False(page.Engines[2].IsAvailable);
        Assert.True(page.Engines[2].NeedsAttention);
        Assert.Contains("do not match", page.Engines[2].Status, StringComparison.Ordinal);
        Assert.Equal("1 of 3 voices are installed on this PC. 1 needs attention.", page.EnginesSummary);
    });

    [Fact]
    public void TheVoicePageWithoutTheAssetService_ListsNoEnginesAndKeepsItsOtherControls() => RunSta(() =>
    {
        using var kit = CreateModelKit(TestPc(16), withAssets: false);
        var page = kit.Model.Voice;

        Assert.False(page.HasEngines);
        Assert.Equal(TextToSpeechModels.All, page.Models);
        Assert.False(page.CheckFilesCommand.CanExecute(null));
    });
}
