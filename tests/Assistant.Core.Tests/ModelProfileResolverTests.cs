using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>Turning the settings' choice of profile into files, a context and engine options.</summary>
public sealed class ModelProfileResolverTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "assistant-profile-tests");
    private static readonly AppPaths Paths = new(Root);

    private static string Model(string profile, string file = "model.gguf") => Path.Combine(Paths.ModelsDirectory, profile, file);

    private static ModelProfileResolver Resolver(
        long memoryGiB = 16,
        IEnumerable<string>? existing = null,
        IModelProfileCatalog? catalog = null)
    {
        var files = new HashSet<string>(existing ?? [], StringComparer.OrdinalIgnoreCase);
        return new ModelProfileResolver(
            catalog ?? new ModelProfileCatalog(), new FixedHardware(new HardwareInfo(memoryGiB * GiB, 8)), Paths, files.Contains);
    }

    private static string[] Installed(string profile = "chat-4b", bool projector = true) =>
        projector ? [Model(profile), Model(profile, "mmproj.gguf")] : [Model(profile)];

    [Fact]
    public void WithNoChoice_ItResolvesTheDefaultProfile_InTheModelsFolder()
    {
        var resolved = Resolver(existing: Installed()).Resolve(new ModelSettings());

        Assert.NotNull(resolved);
        Assert.Equal(ModelProfileCatalog.DefaultProfileId, resolved.Profile.Id);
        Assert.Equal(Model("chat-4b"), resolved.Files.ModelPath);
        Assert.Equal(Model("chat-4b", "mmproj.gguf"), resolved.Files.ProjectorPath);
        Assert.Equal("chat-4b", resolved.Files.ModelId);
        Assert.Equal("chat-4b", resolved.Files.DeriveModelId());
        Assert.True(resolved.IsInstalled);
        Assert.True(resolved.ReadsImages);
        Assert.False(resolved.ProjectorMissing);
    }

    [Fact]
    public void AChosenProfile_IsUsed_AndAnUnknownOneOrPresetIsNotGuessedAt()
    {
        var resolver = Resolver(existing: Installed("chat-9b"));

        Assert.Equal("chat-9b", resolver.Resolve(new ModelSettings { ProfileId = "chat-9b" })!.Profile.Id);
        Assert.Null(resolver.Resolve(new ModelSettings { ProfileId = "chat-70b" }));
        Assert.Null(resolver.Resolve(new ModelSettings { HardwarePresetId = "turbo" }));
    }

    [Fact]
    public void AProfileWhoseFilesAreNotThere_IsNotInstalled_ButStillDescribed()
    {
        var resolved = Resolver().Resolve(new ModelSettings());

        Assert.NotNull(resolved);
        Assert.False(resolved.IsInstalled);
        Assert.False(resolved.ReadsImages);
        Assert.True(resolved.ProjectorMissing);
        Assert.Equal(Model("chat-4b"), resolved.Files.ModelPath);
    }

    [Fact]
    public void AMissingProjector_LeavesTheModelInstalled_ButTextOnly()
    {
        var resolved = Resolver(existing: Installed(projector: false)).Resolve(new ModelSettings());

        Assert.NotNull(resolved);
        Assert.True(resolved.IsInstalled);
        Assert.Null(resolved.Files.ProjectorPath);
        Assert.True(resolved.ProjectorMissing);
        Assert.False(resolved.ReadsImages);
    }

    [Fact]
    public void AMissingChatTemplate_MeansNotInstalled()
    {
        var catalog = new ModelProfileCatalog([ModelProfileCatalog.Standard with { ChatTemplatePath = Path.Combine("chat-4b", "chat.jinja") }]);

        var without = Resolver(existing: Installed(), catalog: catalog).Resolve(new ModelSettings());
        var with = Resolver(existing: [.. Installed(), Model("chat-4b", "chat.jinja")], catalog: catalog).Resolve(new ModelSettings());

        Assert.False(without!.IsInstalled);
        Assert.True(with!.IsInstalled);
        Assert.Equal(Model("chat-4b", "chat.jinja"), with.Files.ChatTemplatePath);
    }

    [Fact]
    public void ThePickedModelsFolder_ReplacesTheDefaultOne_IfItIsAFullPath()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere-models");
        var resolver = Resolver(existing: [Path.Combine(elsewhere, "chat-4b", "model.gguf")]);

        var picked = resolver.Resolve(new ModelSettings { ModelsDirectory = elsewhere });
        var relative = resolver.Resolve(new ModelSettings { ModelsDirectory = "models" });

        Assert.True(picked!.IsInstalled);
        Assert.Equal(Path.Combine(elsewhere, "chat-4b", "model.gguf"), picked.Files.ModelPath);
        Assert.Equal(Model("chat-4b"), relative!.Files.ModelPath);
    }

    [Fact]
    public void AProfilesFullyQualifiedPaths_AreUsedAsTheyAre()
    {
        var mine = Path.Combine(Path.GetTempPath(), "mine", "custom.gguf");
        var catalog = new ModelProfileCatalog([
            new ModelProfile("mine", "Mine", mine, "Q8_0", new ModelContextDefaults(8192)) { ParametersBillions = 3, RecommendedMemoryGiB = 8 }]);

        var resolved = Resolver(existing: [mine], catalog: catalog).Resolve(new ModelSettings { ProfileId = "mine" });

        Assert.Equal(mine, resolved!.Files.ModelPath);
        Assert.True(resolved.IsInstalled);
        Assert.False(resolved.Profile.SupportsVision);
        Assert.Null(resolved.Files.ProjectorPath);
    }

    [Theory]
    [InlineData(8, "compact", 4096)]
    [InlineData(16, "balanced", 16384)]
    [InlineData(32, "performance", 32768)]
    public void TheContext_IsTheProfilesHeldToWhatThePresetAllows(int memoryGiB, string preset, int context)
    {
        var resolved = Resolver(memoryGiB).Resolve(new ModelSettings());

        Assert.Equal(preset, resolved!.Preset.Id);
        Assert.Equal(context, resolved.Files.ContextLength);
    }

    [Fact]
    public void APresetOnlyHoldsAModelBack_ItNeverGivesItMoreThanItsProfileWants()
    {
        var resolved = Resolver().Resolve(new ModelSettings { HardwarePresetId = "performance" });

        Assert.Equal(ModelProfileCatalog.Standard.Context.LoadTokens, resolved!.Files.ContextLength);
    }

    [Fact]
    public void AContextTheUserSets_IsUsedAsItIs()
    {
        var resolved = Resolver(8).Resolve(new ModelSettings { ContextLength = 12000 });

        Assert.Equal(12000, resolved!.Files.ContextLength);
    }

    [Fact]
    public void TheChosenPreset_WinsOverTheOneThatSuitsTheMachine()
    {
        var resolved = Resolver(64).Resolve(new ModelSettings { HardwarePresetId = "compact" });

        Assert.Equal("compact", resolved!.Preset.Id);
        Assert.Equal(4096, resolved.Files.ContextLength);
    }

    [Fact]
    public void EngineOptions_ComeFromThePreset_AndTheProfilesWin()
    {
        var profile = ModelProfileCatalog.Standard with { RuntimeArguments = ["--ubatch-size", "128", "--threads", "6"] };
        var catalog = new ModelProfileCatalog([profile]);

        var compact = Resolver(existing: Installed(), catalog: catalog).Resolve(new ModelSettings { HardwarePresetId = "compact" });
        var balanced = Resolver(existing: Installed(), catalog: catalog).Resolve(new ModelSettings { HardwarePresetId = "balanced" });

        Assert.Equal(["--batch-size", "512", "--ubatch-size", "128", "--threads", "6"], compact!.Files.RuntimeArguments);
        Assert.Equal(["--ubatch-size", "128", "--threads", "6"], balanced!.Files.RuntimeArguments);
    }

    [Fact]
    public void TheResolvedFiles_AreWellFormedForTheHost()
    {
        var resolved = Resolver(existing: Installed()).Resolve(new ModelSettings { HardwarePresetId = "compact" });

        Assert.All(
            new[] { resolved!.Files.ModelPath, resolved.Files.ProjectorPath! },
            path => Assert.True(Path.IsPathFullyQualified(path)));
        Assert.True(Assistant.Core.Contracts.EngineArguments.IsValid(resolved.Files.RuntimeArguments));
    }

    [Theory]
    [InlineData(16, "chat-9b", false)]
    [InlineData(15.7, "chat-9b", false)]
    [InlineData(12, "chat-9b", true)]
    [InlineData(7.7, "chat-4b", false)]
    [InlineData(4, "chat-4b", true)]
    public void AProfileBeyondTheMachinesMemory_IsFlagged_ButStillResolved(double memoryGiB, string profile, bool below)
    {
        var resolver = new ModelProfileResolver(
            new ModelProfileCatalog(), new FixedHardware(new HardwareInfo((long)(memoryGiB * GiB), 8)), Paths, _ => true);

        var resolved = resolver.Resolve(new ModelSettings { ProfileId = profile });

        Assert.Equal(below, resolved!.BelowRecommendedMemory);
    }

    [Fact]
    public void AMachineWhoseMemoryIsUnknown_IsNeverFlagged()
    {
        var resolver = new ModelProfileResolver(
            new ModelProfileCatalog(), new FixedHardware(new HardwareInfo(0, 8)), Paths, _ => true);

        Assert.False(resolver.Resolve(new ModelSettings { ProfileId = "chat-9b" })!.BelowRecommendedMemory);
    }

    [Fact]
    public void TheSettings_HoldTheProfileChoice_WithoutPaths()
    {
        var text = new ModelSettings { ProfileId = "chat-9b", HardwarePresetId = "compact", ModelFilePath = @"C:\Users\PRIVATE-NAME-1c\m.gguf" }.ToString();

        Assert.Contains("chat-9b", text, StringComparison.Ordinal);
        Assert.Contains("compact", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }

    private sealed class FixedHardware(HardwareInfo info) : IHardwareInfoProvider
    {
        public HardwareInfo Get() => info;
    }
}
