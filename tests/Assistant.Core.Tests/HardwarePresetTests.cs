using Assistant.Core.ModelProfiles;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The hardware presets and which one a machine gets.</summary>
public sealed class HardwarePresetTests
{
    private const long GiB = 1024L * 1024 * 1024;

    // What .NET reports for machines sold with these sizes: a little under.
    [Theory]
    [InlineData(7.7, "compact")]
    [InlineData(10.5, "compact")]
    [InlineData(11.7, "balanced")]
    [InlineData(15.7, "balanced")]
    [InlineData(21.5, "balanced")]
    [InlineData(23.7, "performance")]
    [InlineData(31.7, "performance")]
    [InlineData(127, "performance")]
    public void TheMachinesMemory_PicksThePreset(double gibibytes, string expected) =>
        Assert.Equal(expected, HardwarePresets.Select(new HardwareInfo((long)(gibibytes * GiB), 8)).Id);

    [Fact]
    public void AMachineWhoseMemoryIsUnknown_GetsTheCompactPreset()
    {
        Assert.Equal("compact", HardwarePresets.Select(new HardwareInfo(0, 8)).Id);
        Assert.False(new HardwareInfo(0, 8).IsMemoryKnown);
    }

    [Fact]
    public void ThePresets_AreValid_AndOnlyEverHoldAModelBack()
    {
        Assert.All(HardwarePresets.All, preset => Assert.Null(preset.Validate()));
        Assert.Equal(
            HardwarePresets.All.OrderBy(preset => preset.MinimumMemoryGiB), HardwarePresets.All);
        Assert.Equal(
            HardwarePresets.All.OrderBy(preset => preset.MaxContextTokens), HardwarePresets.All);
        Assert.Equal(0, HardwarePresets.Compact.MinimumMemoryGiB);
    }

    [Fact]
    public void ThePresets_AreFoundById()
    {
        Assert.Same(HardwarePresets.Balanced, HardwarePresets.Find("balanced"));
        Assert.Null(HardwarePresets.Find("turbo"));
        Assert.Null(HardwarePresets.Find(null));
    }

    [Fact]
    public void APreset_MustBeValid()
    {
        Assert.NotNull(new HardwarePreset("Bad Id", "Bad", 0, 4096).Validate());
        Assert.NotNull(new HardwarePreset("ok", "", 0, 4096).Validate());
        Assert.NotNull(new HardwarePreset("ok", "Ok", -1, 4096).Validate());
        Assert.NotNull(new HardwarePreset("ok", "Ok", 0, 10).Validate());
        Assert.NotNull(new HardwarePreset("ok", "Ok", 0, 4096) { RuntimeArguments = ["--port", "80"] }.Validate());
        Assert.Null(new HardwarePreset("ok", "Ok", 0, 4096) { RuntimeArguments = ["--threads", "4"] }.Validate());
    }

    [Fact]
    public void TheHardwareOfThisMachine_HasItsMemoryAndProcessors()
    {
        var hardware = new EnvironmentHardwareInfoProvider().Get();

        Assert.True(hardware.IsMemoryKnown);
        Assert.True(hardware.TotalMemoryGiB >= 1);
        Assert.True(hardware.LogicalProcessors >= 1);
    }
}
