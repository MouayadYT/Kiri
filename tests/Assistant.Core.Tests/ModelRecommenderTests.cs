using System.Globalization;
using Assistant.Core.Hardware;
using Assistant.Core.ModelProfiles;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The model profile and context window recommended for a PC (step 124), by the documented thresholds.</summary>
public sealed class ModelRecommenderTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly CpuInfo Cpu = new("Test CPU", 8, 16, "X64", 3000);

    private static GpuAdapterInfo Card(double gib, string name = "Test Card") =>
        new(name, 0x10DE, GpuAdapterKind.Hardware, (long)(gib * GiB), 16 * GiB, (long)(gib * GiB));

    private static HardwareProfile Pc(double memoryGiB, params GpuAdapterInfo[] gpus) =>
        new(Cpu, new SystemMemoryInfo((long)(memoryGiB * GiB), (long)(memoryGiB * GiB / 2)), gpus);

    // The built-in profiles as they were with a usual window of 8,192 tokens, replaced in place: the rules about a window held back by memory or let out by a roomy card
    // are about the gap between the model's usual window and the machine's, and are tested with it. The shipped profiles are tested below with their own.
    private static ModelProfileCatalog UsualWindowOf8192() => new(
    [
        ModelProfileCatalog.Standard with { Context = new ModelContextDefaults(8192) },
        ModelProfileCatalog.Large with { Context = new ModelContextDefaults(8192) },
    ]);

    private static ModelRecommender Recommender(IModelProfileCatalog? catalog = null) =>
        new(catalog ?? UsualWindowOf8192(), new FixedProfile(Pc(16)));

    [Theory]
    [InlineData(64, 12, "chat-9b", 32768)]    // the dev PC: a 12 GB card and 64 GB of memory
    [InlineData(32, 6, "chat-4b", 32768)]
    [InlineData(16, 12, "chat-9b", 16384)]    // 16 GB of memory holds the window to what that class of machine gets
    [InlineData(12, 0, "chat-4b", 16384)]
    [InlineData(8, 0, "chat-4b", 4096)]
    public void TheShippedProfilesAskForARoomyWindow_HeldOnlyByThePCsMemory(double memoryGiB, double cardGiB, string profile, int window)
    {
        var gpus = cardGiB > 0 ? new[] { Card(cardGiB) } : [];

        var recommendation = new ModelRecommender(new ModelProfileCatalog(), new FixedProfile(Pc(memoryGiB, gpus))).Recommend(Pc(memoryGiB, gpus));

        Assert.Equal(profile, recommendation.Profile.Id);
        Assert.Equal(window, recommendation.ContextTokens);
    }

    private static T InEnglish<T>(Func<T> action)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // The thresholds, exactly: the profile and the window each PC gets.
    [Theory]
    [InlineData(32, 12, "chat-9b", 8192)]      // a card with 12 GB and room in the PC: the large model
    [InlineData(32, 10, "chat-9b", 8192)]      // exactly the video memory the large model likes
    [InlineData(32, 9.6, "chat-9b", 8192)]     // reports a little under 10: still counts (tolerance)
    [InlineData(32, 9, "chat-4b", 8192)]       // one gigabyte short: the standard model
    [InlineData(32, 8, "chat-4b", 8192)]
    [InlineData(32, 6, "chat-4b", 8192)]
    [InlineData(32, 4, "chat-4b", 8192)]       // a card too small for the standard model: it runs on the processor
    [InlineData(32, 24, "chat-9b", 32768)]     // a large card and plenty of memory: a larger window too
    [InlineData(32, 16, "chat-9b", 32768)]
    [InlineData(32, 15.6, "chat-9b", 32768)]
    [InlineData(32, 15, "chat-9b", 8192)]
    [InlineData(16, 24, "chat-9b", 16384)]     // 16 GB of memory holds the window to what that class of machine gets
    [InlineData(15.7, 12, "chat-9b", 8192)]    // what .NET reports for a 16 GB machine counts as 16
    [InlineData(12, 24, "chat-4b", 16384)]     // not enough memory for the large model, whatever the card has
    [InlineData(32, 0, "chat-4b", 8192)]       // no card at all
    public void ThePCsMemoryAndItsBestCard_DecideTheProfileAndTheWindow(
        double memoryGiB, double cardGiB, string profile, int window)
    {
        var gpus = cardGiB > 0 ? new[] { Card(cardGiB) } : [];

        var recommendation = Recommender().Recommend(Pc(memoryGiB, gpus));

        Assert.Equal(profile, recommendation.Profile.Id);
        Assert.Equal(window, recommendation.ContextTokens);
    }

    [Theory]
    [InlineData(6, 4096)]
    [InlineData(10.5, 4096)]
    [InlineData(11.7, 8192)]
    [InlineData(31, 8192)]
    public void WithNoCard_TheWindowFollowsTheMemoryButNeverPassesTheModelsUsualOne(double memoryGiB, int window) =>
        Assert.Equal(window, Recommender().Recommend(Pc(memoryGiB)).ContextTokens);

    [Fact]
    public void TheCardWithTheMostMemory_IsTheOneThatCounts_AndASoftwareAdapterNeverDoes()
    {
        var small = Card(8, "Small");
        var large = Card(12, "Large");
        var software = new GpuAdapterInfo("Basic Render Driver", 0x1414, GpuAdapterKind.Software, 24 * GiB, 0, null);
        var integrated = new GpuAdapterInfo("Built-in", 0x8086, GpuAdapterKind.Hardware, 0, 32 * GiB, null);

        Assert.Equal("chat-9b", Recommender().Recommend(Pc(32, small, large)).Profile.Id);
        Assert.Equal("chat-9b", Recommender().Recommend(Pc(32, large, small)).Profile.Id);
        Assert.Equal("chat-4b", Recommender().Recommend(Pc(32, software, integrated)).Profile.Id);
    }

    [Fact]
    public void TwoSmallCards_AreNotAddedTogether()
    {
        Assert.Equal("chat-4b", Recommender().Recommend(Pc(64, Card(8, "A"), Card(8, "B"))).Profile.Id);
    }

    [Fact]
    public void WhatIsKnown_SaysWhatDecidedIt()
    {
        Assert.Equal(RecommendationBasis.Graphics, Recommender().Recommend(Pc(32, Card(12))).Basis);
        Assert.Equal(RecommendationBasis.Graphics, Recommender().Recommend(Pc(32, Card(6))).Basis);
        Assert.Equal(RecommendationBasis.Processor, Recommender().Recommend(Pc(32, Card(4))).Basis);
        Assert.Equal(RecommendationBasis.Processor, Recommender().Recommend(Pc(32)).Basis);
        Assert.Equal(RecommendationBasis.Unknown, Recommender().Recommend(HardwareProfile.Unknown).Basis);
    }

    [Fact]
    public void APCThatCouldNotBeRead_GetsTheDefaultProfileAndTheSmallestWindow()
    {
        var recommendation = Recommender().Recommend(HardwareProfile.Unknown);

        Assert.Equal(ModelProfileCatalog.DefaultProfileId, recommendation.Profile.Id);
        Assert.Equal(4096, recommendation.ContextTokens);
        Assert.Contains(recommendation.Reasons, reason => reason.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReasons_NameTheCardAndTheNumbersTheyWentBy() => InEnglish(() =>
    {
        var large = Recommender().Recommend(Pc(64, Card(12, "NVIDIA Test 5070"))).Reasons;
        var small = Recommender().Recommend(Pc(32, Card(8, "NVIDIA Test 2080"))).Reasons;
        var none = Recommender().Recommend(Pc(32)).Reasons;
        var tiny = Recommender().Recommend(Pc(32, Card(4, "Tiny Card"))).Reasons;

        Assert.Contains("NVIDIA Test 5070 has 12 GB of memory of its own, enough for the Large (8-9B class) model, and this PC has 64 GB of memory.", large);
        Assert.Contains("NVIDIA Test 2080 has 8 GB of memory of its own, enough for the Standard (4B class) model, but the Large (8-9B class) model needs about 10 GB.", small);
        Assert.Contains("No graphics card with memory of its own was found, so the Standard (4B class) model, which is quick on the processor alone, is recommended.", none);
        Assert.Contains("Tiny Card has 4 GB of memory of its own, less than the 6 GB the Standard (4B class) model likes, so it will run mostly on the processor.", tiny);
        Assert.Contains("A window of 8,192 tokens, the model's usual one.", large);
        return 0;
    });

    [Fact]
    public void AWindowThatMemoryHoldsBack_SaysSo() => InEnglish(() =>
    {
        var reasons = Recommender().Recommend(Pc(8)).Reasons;

        Assert.Contains("A window of 4,096 tokens: this PC's 8 GB of memory holds it back from the usual 8,192.", reasons);
        return 0;
    });

    [Fact]
    public void ALargerWindow_SaysTheCardHasRoomForIt() => InEnglish(() =>
    {
        var reasons = Recommender().Recommend(Pc(64, Card(24))).Reasons;

        Assert.Contains("A window of 32,768 tokens: the graphics card has room for more than the usual 8,192.", reasons);
        return 0;
    });

    [Fact]
    public void RecommendContext_FollowsWhicheverProfileIsInUse()
    {
        // A 6 GB card holds the standard model, so a window of 8192 is fine for it; a profile that needs 10 GB does not fit that card at all.
        var recommender = new ModelRecommender(new ModelProfileCatalog(), new FixedProfile(Pc(64, Card(24))));
        var tight = new ModelRecommender(new ModelProfileCatalog(), new FixedProfile(Pc(64, Card(6))));

        Assert.Equal(32768, recommender.RecommendContext(ModelProfileCatalog.Standard));
        Assert.Equal(32768, recommender.RecommendContext(ModelProfileCatalog.Large));
        Assert.Equal(32768, tight.RecommendContext(ModelProfileCatalog.Standard));
        Assert.Equal(32768, tight.RecommendContext(ModelProfileCatalog.Large));
    }

    [Fact]
    public void AProfileAddedToTheCatalog_IsConsideredWithoutAChangeHere()
    {
        var huge = new ModelProfile("chat-30b", "Huge", Path.Combine("chat-30b", "model.gguf"), "Q4_K_M", new ModelContextDefaults(8192))
        {
            ParametersBillions = 30, RecommendedMemoryGiB = 48, RecommendedVideoMemoryGiB = 24,
        };
        var catalog = new ModelProfileCatalog([huge]);

        Assert.Equal("chat-30b", Recommender(catalog).Recommend(Pc(64, Card(24))).Profile.Id);
        Assert.Equal("chat-9b", Recommender(catalog).Recommend(Pc(64, Card(12))).Profile.Id);
        Assert.Equal("chat-9b", Recommender(catalog).Recommend(Pc(32, Card(24))).Profile.Id);
        Assert.Equal("chat-4b", Recommender(catalog).Recommend(Pc(64, Card(8))).Profile.Id);
    }

    [Fact]
    public void AProfileThatNeedsNoCard_IsNeverRecommendedForTheSakeOfOne()
    {
        var noCard = new ModelProfile("chat-20b", "Big", Path.Combine("chat-20b", "model.gguf"), "Q4_K_M", new ModelContextDefaults(8192))
        {
            ParametersBillions = 20, RecommendedMemoryGiB = 24, RecommendedVideoMemoryGiB = 0,
        };

        Assert.Equal("chat-9b", Recommender(new ModelProfileCatalog([noCard])).Recommend(Pc(64, Card(24))).Profile.Id);
    }

    [Fact]
    public void TheRecommenderReadsThePCFromTheService_EachTime()
    {
        var service = new FixedProfile(Pc(32));
        var recommender = new ModelRecommender(new ModelProfileCatalog(), service);

        Assert.Equal("chat-4b", recommender.Recommend().Profile.Id);

        service.Profile = Pc(32, Card(12));

        Assert.Equal("chat-9b", recommender.Recommend().Profile.Id);
    }

    [Fact]
    public void TheBuiltInProfiles_HaveVideoMemoryNeedsThatGrowWithTheirSize()
    {
        Assert.Equal(6, ModelProfileCatalog.Standard.RecommendedVideoMemoryGiB);
        Assert.Equal(10, ModelProfileCatalog.Large.RecommendedVideoMemoryGiB);
        Assert.Null(ModelProfileCatalog.Standard.Validate());
        Assert.Null(ModelProfileCatalog.Large.Validate());
        Assert.NotNull((ModelProfileCatalog.Standard with { RecommendedVideoMemoryGiB = -1 }).Validate());
    }

    [Fact]
    public void TheHardwareProviderForThePresets_ComesFromTheProfile_AndFallsBackWhenMemoryIsUnknown()
    {
        var fallback = new FixedHardware(new HardwareInfo(8 * GiB, 4));

        var known = new HardwareProfileInfoProvider(new FixedProfile(Pc(32)), fallback).Get();
        var unknown = new HardwareProfileInfoProvider(new FixedProfile(HardwareProfile.Unknown), fallback).Get();

        Assert.Equal(new HardwareInfo(32 * GiB, 16), known);
        Assert.Equal(new HardwareInfo(8 * GiB, 4), unknown);
    }

    private sealed class FixedProfile(HardwareProfile profile) : IHardwareProfileService
    {
        public HardwareProfile Profile { get; set; } = profile;

        public HardwareProfile Current => Profile;

        public HardwareProfile Refresh() => Profile;
    }

    private sealed class FixedHardware(HardwareInfo info) : IHardwareInfoProvider
    {
        public HardwareInfo Get() => info;
    }
}
