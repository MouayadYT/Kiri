using System.Globalization;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What the Settings window says about a context value (PROJECT_SPEC §5.6): only nonsense is an error, and a heavy value is a warning the user may ignore.</summary>
public sealed class ContextAdvisorTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly ModelProfile Standard = ModelProfileCatalog.Standard;
    private static readonly HardwareInfo Sixteen = new(16 * GiB, 8);
    private static readonly HardwareInfo Eight = new(8 * GiB, 4);
    private static readonly HardwareInfo Unknown = new(0, 4);

    [Fact]
    public void TheWindowThePresetGivesNeedsNoAdvice() =>
        InvariantCulture(() => Assert.Equal(ContextAdviceLevel.None, ContextAdvisor.AssessWindow(8192, Standard, Sixteen, HardwarePresets.Balanced).Level));

    [Theory]
    [InlineData(255)]
    [InlineData(0)]
    [InlineData(-4096)]
    [InlineData(1_048_577)]
    public void ANumberOutsideTheRangeIsTheOnlyThingThatCannotBeChosen(int tokens)
    {
        var advice = ContextAdvisor.AssessWindow(tokens, Standard, Sixteen, HardwarePresets.Balanced);

        Assert.True(advice.IsError);
        Assert.Contains("256", advice.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(1_048_576)]
    public void TheEdgesOfTheRangeCanBeChosen(int tokens) =>
        Assert.False(ContextAdvisor.AssessWindow(tokens, Standard, Sixteen, HardwarePresets.Balanced).IsError);

    [Fact]
    public void AHugeWindowIsAWarningThatSaysHowMuchMemoryItMayNeedNotAnError() => InvariantCulture(() =>
    {
        var advice = ContextAdvisor.AssessWindow(262_144, Standard, Sixteen, HardwarePresets.Balanced);

        Assert.Equal(ContextAdviceLevel.Warning, advice.Level);
        Assert.False(advice.IsError);
        Assert.Contains("may need about", advice.Message, StringComparison.Ordinal);
        Assert.Contains("16.0 GB", advice.Message, StringComparison.Ordinal);
        Assert.Contains("You can still use it", advice.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void AWindowThatTakesALargeShareOfMemoryWarnsThatOtherAppsMaySlowDown() => InvariantCulture(() =>
    {
        // About 12.7 GB of 16.
        var advice = ContextAdvisor.AssessWindow(65_536, Standard, Sixteen, HardwarePresets.Balanced);

        Assert.Equal(ContextAdviceLevel.Warning, advice.Level);
        Assert.Contains("other apps may slow down", advice.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void TheSameWindowIsFineOnAMachineWithRoomForIt() =>
        InvariantCulture(() => Assert.NotEqual(
            ContextAdviceLevel.Warning,
            ContextAdvisor.AssessWindow(65_536, Standard, new HardwareInfo(64 * GiB, 16), HardwarePresets.Performance).Level));

    [Fact]
    public void AnEightGigabyteMachineIsWarnedEarlier() =>
        InvariantCulture(() => Assert.Equal(ContextAdviceLevel.Warning, ContextAdvisor.AssessWindow(16_384, Standard, Eight, HardwarePresets.Compact).Level));

    [Fact]
    public void WithoutKnownMemoryOnlyALargeCacheIsWarnedAbout() => InvariantCulture(() =>
    {
        Assert.Equal(ContextAdviceLevel.Note, ContextAdvisor.AssessWindow(16_384, Standard, Unknown, HardwarePresets.Compact).Level);
        var large = ContextAdvisor.AssessWindow(65_536, Standard, Unknown, HardwarePresets.Compact);

        Assert.Equal(ContextAdviceLevel.Warning, large.Level);
        Assert.Contains("not known", large.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void AboveThePresetsCapIsANoteNamingThePreset() => InvariantCulture(() =>
    {
        var advice = ContextAdvisor.AssessWindow(16_384, Standard, new HardwareInfo(128 * GiB, 32), HardwarePresets.Compact);

        Assert.Equal(ContextAdviceLevel.Note, advice.Level);
        Assert.Contains("Compact preset", advice.Message, StringComparison.Ordinal);
        Assert.Contains("4,096", advice.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void PastWhatMostModelsWereTrainedForIsANote() => InvariantCulture(() =>
    {
        var advice = ContextAdvisor.AssessWindow(40_000, Standard, new HardwareInfo(128 * GiB, 32), null);

        Assert.Equal(ContextAdviceLevel.Note, advice.Level);
        Assert.Contains("trained", advice.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void AVerySmallWindowIsANote() =>
        InvariantCulture(() => Assert.Contains("forgets", ContextAdvisor.AssessWindow(512, Standard, Sixteen, HardwarePresets.Balanced).Message, StringComparison.Ordinal));

    [Fact]
    public void AModelPickedByFileIsJudgedAsAnEightBillionParameterOne() => InvariantCulture(() =>
    {
        var advice = ContextAdvisor.AssessWindow(131_072, null, Sixteen, null);

        Assert.Equal(ContextAdviceLevel.Warning, advice.Level);
        Assert.Contains("about 8 billion parameters", advice.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void TheEstimatesGrowWithTheWindowAndTheModel()
    {
        var cache = ContextAdvisor.EstimateCacheBytes(4, 8192);

        // About 136 KiB a token: a little over a gigabyte for 8192.
        Assert.InRange(cache / (double)GiB, 1.0, 1.2);
        Assert.True(ContextAdvisor.EstimateCacheBytes(4, 16_384) > cache);
        Assert.True(ContextAdvisor.EstimateCacheBytes(9, 8192) > cache);
        Assert.Equal(0, ContextAdvisor.EstimateCacheBytes(4, -5));
        Assert.True(ContextAdvisor.EstimateMemoryBytes(ModelProfileCatalog.Large, 8192) > ContextAdvisor.EstimateMemoryBytes(Standard, 8192));
        Assert.True(ContextAdvisor.EstimateMemoryBytes(Standard, 8192) > cache);
    }

    [Fact]
    public void ALimitOfZeroMeansNoLimitAndSaysSo() => Assert.Contains("No limit", ContextAdvisor.AssessLimit(0, 8192).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(1_048_577)]
    public void ALimitOutsideTheRangeCannotBeChosen(int tokens) => Assert.True(ContextAdvisor.AssessLimit(tokens, 8192).IsError);

    [Fact]
    public void ALimitAboveTheWindowIsANoteThatTheWindowIsTheCeiling() => InvariantCulture(() =>
    {
        var advice = ContextAdvisor.AssessLimit(16_384, 8192);

        Assert.Equal(ContextAdviceLevel.Note, advice.Level);
        Assert.Contains("8,192", advice.Message, StringComparison.Ordinal);
        Assert.Contains("Advanced", advice.Message, StringComparison.Ordinal);
        Assert.Equal(ContextAdviceLevel.None, ContextAdvisor.AssessLimit(8192, 8192).Level);
    });

    [Fact]
    public void AHeavyLimitBelowTheNormalOneIsAWarning()
    {
        Assert.Equal(ContextAdviceLevel.Warning, ContextAdvisor.AssessHeavyLimit(8192, 4096, 16_384).Level);
        Assert.Equal(ContextAdviceLevel.None, ContextAdvisor.AssessHeavyLimit(4096, 8192, 16_384).Level);
        Assert.Equal(ContextAdviceLevel.None, ContextAdvisor.AssessHeavyLimit(0, 4096, 16_384).Level);
        Assert.True(ContextAdvisor.AssessHeavyLimit(4096, 100, 16_384).IsError);
    }

    private static void InvariantCulture(Action action)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            action();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }
}
