using Assistant.Core.Hardware;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// The profile and window chosen for a PC whose settings choose none (step 124), and the models packaged with the Assistant (step 123): what
/// the resolver does with them, and what it never changes of what the user chose.
/// </summary>
public sealed class ModelProfileResolverAutomaticTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "assistant-profile-automatic-tests");
    private static readonly AppPaths Paths = new(Root);
    private static readonly string Packaged = Path.Combine(Root, "install", "assets", "models");

    private static string Own(string profile, string file = "model.gguf") => Path.Combine(Paths.ModelsDirectory, profile, file);

    private static string Shipped(string profile, string file = "model.gguf") => Path.Combine(Packaged, profile, file);

    private static HardwareProfile Pc(double memoryGiB, double cardGiB = 0) =>
        new(
            new CpuInfo("Test", 8, 16, "X64", 3000),
            new SystemMemoryInfo((long)(memoryGiB * GiB), (long)(memoryGiB * GiB / 2)),
            cardGiB > 0 ? [new GpuAdapterInfo("Card", 0x10DE, GpuAdapterKind.Hardware, (long)(cardGiB * GiB), 0, null)] : []);

    private static ModelProfileResolver Resolver(
        HardwareProfile pc, IEnumerable<string>? existing = null, bool recommend = true, bool packaged = true)
    {
        var files = new HashSet<string>(existing ?? [], StringComparer.OrdinalIgnoreCase);
        var hardware = new FixedProfile(pc);
        var catalog = new ModelProfileCatalog();
        return new ModelProfileResolver(
            catalog,
            new HardwareProfileInfoProvider(hardware, new FixedInfo()),
            Paths,
            files.Contains,
            recommend ? new ModelRecommender(catalog, hardware) : null,
            packaged ? Packaged : null);
    }

    [Fact]
    public void WithNoChoice_ThePCsRecommendedProfileIsUsed_WhenItIsInstalled()
    {
        var resolved = Resolver(Pc(32, 12), [Own("chat-4b"), Own("chat-9b")]).Resolve(new ModelSettings());

        Assert.Equal("chat-9b", resolved!.Profile.Id);
        Assert.True(resolved.IsAutomatic);
        Assert.Equal("chat-9b", resolved.Recommendation!.Profile.Id);
        Assert.Equal(Own("chat-9b"), resolved.Files.ModelPath);
    }

    [Fact]
    public void WhenTheRecommendedProfileIsNotInstalled_TheNearestInstalledOneIsUsed_ASmallerFirst()
    {
        var onlyStandard = Resolver(Pc(32, 12), [Own("chat-4b")]).Resolve(new ModelSettings());
        var onlyLarge = Resolver(Pc(32), [Own("chat-9b")]).Resolve(new ModelSettings());
        var neither = Resolver(Pc(32, 12)).Resolve(new ModelSettings());

        Assert.Equal("chat-4b", onlyStandard!.Profile.Id);
        Assert.True(onlyStandard.IsInstalled);
        Assert.Equal("chat-9b", onlyStandard.Recommendation!.Profile.Id);

        // A PC that recommends the standard model, with only the large one installed, still has a model to use.
        Assert.Equal("chat-9b", onlyLarge!.Profile.Id);
        Assert.True(onlyLarge.IsInstalled);

        // Nothing installed: the recommended profile, reported as not installed, which is where its files would go.
        Assert.Equal("chat-9b", neither!.Profile.Id);
        Assert.False(neither.IsInstalled);
    }

    [Fact]
    public void AProfileTheUserChose_IsNeverReplacedByTheRecommendation()
    {
        var resolver = Resolver(Pc(32, 12), [Own("chat-4b"), Own("chat-9b")]);

        var chosen = resolver.Resolve(new ModelSettings { ProfileId = "chat-4b" });

        Assert.Equal("chat-4b", chosen!.Profile.Id);
        Assert.False(chosen.IsAutomatic);
        Assert.Null(chosen.Recommendation);
    }

    [Fact]
    public void WithoutARecommender_NoChoiceStillMeansTheDefaultProfile()
    {
        var resolved = Resolver(Pc(32, 24), [Own("chat-4b"), Own("chat-9b")], recommend: false).Resolve(new ModelSettings());

        Assert.Equal(ModelProfileCatalog.DefaultProfileId, resolved!.Profile.Id);
        Assert.False(resolved.IsAutomatic);
        Assert.Equal(32768, resolved.Files.ContextLength);
    }

    [Fact]
    public void TheWindow_IsTheRecommendedOne_UnlessTheUserOrAPresetSaysOtherwise()
    {
        var resolver = Resolver(Pc(64, 24), [Own("chat-9b")]);

        var automatic = resolver.Resolve(new ModelSettings());
        var own = resolver.Resolve(new ModelSettings { ContextLength = 6000 });
        var preset = resolver.Resolve(new ModelSettings { HardwarePresetId = "balanced" });
        var profileChosen = resolver.Resolve(new ModelSettings { ProfileId = "chat-9b" });

        Assert.Equal(32768, automatic!.Files.ContextLength);
        Assert.Equal(6000, own!.Files.ContextLength);

        // A preset the user picked holds the profile's window to what that preset allows, as it always has.
        Assert.Equal(16384, preset!.Files.ContextLength);

        // Choosing the profile does not turn the recommended window off: that is the user's choice of a window or a preset.
        Assert.Equal(32768, profileChosen!.Files.ContextLength);
    }

    [Fact]
    public void TheHardwarePresetsOptions_StillComeFromTheMemory()
    {
        var resolved = Resolver(Pc(8), [Own("chat-4b")]).Resolve(new ModelSettings());

        Assert.Equal("compact", resolved!.Preset.Id);
        Assert.Equal(4096, resolved.Files.ContextLength);
        Assert.Equal(["--batch-size", "512", "--ubatch-size", "256"], resolved.Files.RuntimeArguments);
    }

    [Fact]
    public void AModelOnlyInThePackagedFolder_IsUsedFromThere()
    {
        var resolved = Resolver(Pc(32), [Shipped("chat-4b"), Shipped("chat-4b", "mmproj.gguf")]).Resolve(new ModelSettings());

        Assert.Equal("chat-4b", resolved!.Profile.Id);
        Assert.True(resolved.IsInstalled);
        Assert.True(resolved.IsPackaged);
        Assert.Equal(Shipped("chat-4b"), resolved.Files.ModelPath);
        Assert.Equal(Shipped("chat-4b", "mmproj.gguf"), resolved.Files.ProjectorPath);
    }

    [Fact]
    public void AModelInTheUsersOwnFolder_IsUsedInsteadOfThePackagedOne()
    {
        var resolved = Resolver(Pc(32), [Own("chat-4b"), Shipped("chat-4b"), Shipped("chat-4b", "mmproj.gguf")]).Resolve(new ModelSettings());

        Assert.False(resolved!.IsPackaged);
        Assert.Equal(Own("chat-4b"), resolved.Files.ModelPath);

        // The files of one profile are never mixed from the two folders: its projector is the user's folder's, which has none here.
        Assert.Null(resolved.Files.ProjectorPath);
    }

    [Fact]
    public void WithoutAPackagedFolder_NothingIsLookedForThere()
    {
        var resolved = Resolver(Pc(32), [Shipped("chat-4b")], packaged: false).Resolve(new ModelSettings());

        Assert.False(resolved!.IsInstalled);
        Assert.False(resolved.IsPackaged);
        Assert.Equal(Own("chat-4b"), resolved.Files.ModelPath);
    }

    [Fact]
    public void ThePackagedProfile_CountsAsInstalledWhenChoosingAutomatically()
    {
        var resolved = Resolver(Pc(32, 12), [Shipped("chat-4b")]).Resolve(new ModelSettings());

        // The large model is recommended and not there; the standard one is, in the packaged folder.
        Assert.Equal("chat-4b", resolved!.Profile.Id);
        Assert.True(resolved.IsInstalled);
        Assert.True(resolved.IsPackaged);
        Assert.Equal("chat-9b", resolved.Recommendation!.Profile.Id);
    }

    [Fact]
    public void ATemplate_MustBeThereToo_InWhicheverFolderTheModelIs()
    {
        var catalog = new ModelProfileCatalog([ModelProfileCatalog.Standard with { ChatTemplatePath = Path.Combine("chat-4b", "chat.jinja") }]);
        var hardware = new FixedProfile(Pc(32));
        var resolver = new ModelProfileResolver(
            catalog, new HardwareProfileInfoProvider(hardware, new FixedInfo()), Paths,
            new HashSet<string>([Shipped("chat-4b")], StringComparer.OrdinalIgnoreCase).Contains, null, Packaged);

        var resolved = resolver.Resolve(new ModelSettings());

        Assert.False(resolved!.IsInstalled);
        Assert.Equal(Shipped("chat-4b", "chat.jinja"), resolved.Files.ChatTemplatePath);
    }

    private sealed class FixedProfile(HardwareProfile profile) : IHardwareProfileService
    {
        public HardwareProfile Current => profile;

        public HardwareProfile Refresh() => profile;
    }

    private sealed class FixedInfo : IHardwareInfoProvider
    {
        public HardwareInfo Get() => new(0, 4);
    }
}
