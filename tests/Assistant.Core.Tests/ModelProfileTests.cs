using Assistant.Core.ModelProfiles;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The model profiles, their validation and the catalog that holds them.</summary>
public sealed class ModelProfileTests
{
    private static ModelProfile Valid(string id = "chat-custom") =>
        new(id, "Custom", @"C:\models\custom.gguf", "Q5_K_M", new ModelContextDefaults(8192))
        {
            ParametersBillions = 7,
            RecommendedMemoryGiB = 12,
        };

    [Fact]
    public void TheBuiltInProfiles_AreValid_ADefault4BClassAndAnOptionalLargerOne()
    {
        var catalog = new ModelProfileCatalog();

        Assert.All(catalog.Profiles, profile => Assert.Null(profile.Validate()));
        Assert.Equal(ModelProfileCatalog.DefaultProfileId, catalog.Default.Id);
        Assert.Same(catalog.Default, catalog.Profiles[0]);
        Assert.Equal(4, catalog.Default.ParametersBillions);
        var large = Assert.IsType<ModelProfile>(catalog.Find(ModelProfileCatalog.LargeProfileId));
        Assert.InRange(large.ParametersBillions, 8, 9);
        Assert.True(large.RecommendedMemoryGiB > catalog.Default.RecommendedMemoryGiB);
    }

    [Fact]
    public void TheBuiltInProfiles_StoreWhatAModelNeeds()
    {
        foreach (var profile in new ModelProfileCatalog().Profiles)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.ModelPath));
            Assert.False(Path.IsPathFullyQualified(profile.ModelPath));
            Assert.False(string.IsNullOrWhiteSpace(profile.Quantization));
            Assert.True(profile.Context.LoadTokens >= 4096);
            Assert.True(profile.SupportsVision);
            Assert.False(string.IsNullOrWhiteSpace(profile.ProjectorPath));
        }
    }

    [Fact]
    public void TheBuiltInProfiles_KeepTheirFilesInFoldersOfTheirOwn_SoNoneNamesAParticularModel()
    {
        var catalog = new ModelProfileCatalog();

        Assert.Equal(catalog.Profiles.Count, catalog.Profiles.Select(profile => Path.GetDirectoryName(profile.ModelPath)).Distinct().Count());
        Assert.All(catalog.Profiles, profile => Assert.Equal("model.gguf", Path.GetFileName(profile.ModelPath)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Chat")]
    [InlineData("chat_4b")]
    [InlineData("-chat")]
    [InlineData("chat-")]
    [InlineData("chat 4b")]
    public void AnInvalidId_IsRefused(string id) => Assert.NotNull(Valid(id).Validate());

    [Fact]
    public void AProfile_NeedsItsRequiredFields()
    {
        Assert.NotNull((Valid() with { DisplayName = " " }).Validate());
        Assert.NotNull((Valid() with { ModelPath = "" }).Validate());
        Assert.NotNull((Valid() with { Quantization = "" }).Validate());
        Assert.NotNull((Valid() with { Context = null! }).Validate());
        Assert.NotNull((Valid() with { ParametersBillions = 0 }).Validate());
        Assert.NotNull((Valid() with { RecommendedMemoryGiB = 0 }).Validate());
    }

    [Fact]
    public void VisionAndAProjector_GoTogether()
    {
        Assert.NotNull((Valid() with { SupportsVision = true }).Validate());
        Assert.NotNull((Valid() with { ProjectorPath = "mmproj.gguf" }).Validate());
        Assert.Null((Valid() with { SupportsVision = true, ProjectorPath = "mmproj.gguf" }).Validate());
    }

    [Fact]
    public void RuntimeArguments_MustBeAllowedOnes()
    {
        Assert.Null((Valid() with { RuntimeArguments = ["--threads", "6"] }).Validate());
        Assert.NotNull((Valid() with { RuntimeArguments = ["--host", "0.0.0.0"] }).Validate());
    }

    [Fact]
    public void ANullRuntimeArgumentList_IsNone() =>
        Assert.Empty((Valid() with { RuntimeArguments = null! }).RuntimeArguments);

    [Fact]
    public void ContextDefaults_MustBeInOrderAndFitTheWindow()
    {
        Assert.Null(new ModelContextDefaults(8192).Validate());
        Assert.NotNull(new ModelContextDefaults(100).Validate());
        Assert.NotNull(new ModelContextDefaults(4096) { HeavyTokens = 8192 }.Validate());
        Assert.NotNull(new ModelContextDefaults(8192) { NormalTokens = 6000, HeavyTokens = 4000 }.Validate());
        Assert.NotNull(new ModelContextDefaults(8192) { ReservedOutputTokens = 3000 }.Validate());
        Assert.NotNull(new ModelContextDefaults(8192) { ReservedOutputTokens = 0 }.Validate());
    }

    [Fact]
    public void ContextDefaults_GiveContextLimits_HeldToTheWindowTheModelGot()
    {
        var defaults = new ModelContextDefaults(8192) { NormalTokens = 4096, HeavyTokens = 8192, ReservedOutputTokens = 1024 };
        var basis = new Assistant.Core.Settings.ContextLimitSettings { MaxAttachedFiles = 3 };

        var roomy = defaults.ApplyTo(basis, 8192);
        var tight = defaults.ApplyTo(basis, 2048);

        Assert.Equal((4096, 8192, 1024), (roomy.NormalContextTokens, roomy.HeavyContextTokens, roomy.ReservedOutputTokens));
        Assert.Equal((2048, 2048, 1024), (tight.NormalContextTokens, tight.HeavyContextTokens, tight.ReservedOutputTokens));
        Assert.Equal(3, roomy.MaxAttachedFiles);
    }

    [Fact]
    public void TheCatalog_FindsProfilesById_AndOnlyExactlyThose()
    {
        var catalog = new ModelProfileCatalog();

        Assert.Same(catalog.Default, catalog.Find("chat-4b"));
        Assert.Null(catalog.Find("CHAT-4B"));
        Assert.Null(catalog.Find("nothing"));
        Assert.Null(catalog.Find(null));
    }

    [Fact]
    public void TheCatalog_TakesMoreProfiles_AndLetsOneReplaceABuiltInInPlace()
    {
        var replacement = Valid(ModelProfileCatalog.DefaultProfileId);
        var catalog = new ModelProfileCatalog([Valid("mine"), replacement]);

        Assert.Same(replacement, catalog.Default);
        Assert.Equal(["chat-4b", "chat-9b", "mine"], catalog.Profiles.Select(profile => profile.Id));
    }

    [Fact]
    public void TheCatalog_RefusesAnInvalidProfile_OrTwoWithOneId()
    {
        Assert.Throws<ArgumentException>(() => new ModelProfileCatalog([Valid() with { Quantization = "" }]));
        Assert.Throws<ArgumentException>(() => new ModelProfileCatalog([Valid("same"), Valid("same")]));
    }

    [Fact]
    public void ToString_HoldsNoPath()
    {
        var text = (Valid() with { ModelPath = @"C:\Users\PRIVATE-NAME-77aa\model.gguf", ProjectorPath = @"C:\Users\PRIVATE-NAME-77aa\mm.gguf" }).ToString();

        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        Assert.Contains("chat-custom", text, StringComparison.Ordinal);
    }
}
