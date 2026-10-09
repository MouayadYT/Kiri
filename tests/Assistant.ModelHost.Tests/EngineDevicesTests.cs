using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>Choosing the devices the engine offloads a model to.</summary>
public sealed class EngineDevicesTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    private static readonly EngineDevice Big = new("Vulkan0", 11 * Gibibyte);
    private static readonly EngineDevice Small = new("Vulkan1", 7 * Gibibyte);

    [Fact]
    public void Parse_ReadsTheIdAndFreeMemoryOfEachDevice_AndIgnoresOtherLines()
    {
        const string Output = """
            0.00.000.896 I srv  llama_server: initializing ...
            Available devices:
              Vulkan0: NVIDIA GeForce RTX 5070 (11943 MiB, 11175 MiB free)
              Vulkan1: NVIDIA GeForce RTX 2080 (8203 MiB, 7296 MiB free)
            """;

        var devices = EngineDevices.Parse(Output);

        Assert.Equal(
            [new EngineDevice("Vulkan0", 11175L * 1024 * 1024, "NVIDIA GeForce RTX 5070"), new EngineDevice("Vulkan1", 7296L * 1024 * 1024, "NVIDIA GeForce RTX 2080")],
            devices);
        Assert.Empty(EngineDevices.Parse("ggml_vulkan: No devices found.\nAvailable devices:\n"));
    }

    [Fact]
    public void Choose_TakesTheSingleRoomiestDevice_WhenTheModelFitsOnIt()
    {
        var chosen = EngineDevices.Choose([Small, Big], EngineDevices.RequiredBytes(5 * Gibibyte, 0));

        Assert.Equal(["Vulkan0"], chosen);
    }

    [Fact]
    public void Choose_AddsDevicesRoomiestFirst_OnlyWhenOneCannotHoldTheModel()
    {
        var chosen = EngineDevices.Choose([Small, Big], EngineDevices.RequiredBytes(14 * Gibibyte, 0));

        Assert.Equal(["Vulkan0", "Vulkan1"], chosen);
    }

    [Fact]
    public void Choose_TakesEveryDevice_WhenEvenTheyCannotHoldTheModel()
    {
        var chosen = EngineDevices.Choose([Small, Big], EngineDevices.RequiredBytes(40 * Gibibyte, 0));

        Assert.Equal(["Vulkan0", "Vulkan1"], chosen);
    }

    [Fact]
    public void Choose_HasNothingToChoose_WithoutDevices() => Assert.Null(EngineDevices.Choose([], 1));

    // A laptop with both: the graphics built into the processor report the PC's own memory as free, more than the card has.
    [Theory]
    [InlineData("Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 4060 Laptop GPU")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "AMD Radeon RX 7600M XT")]
    [InlineData("AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 3050")]
    [InlineData("AMD Radeon(TM) 780M", "AMD Radeon RX 7800 XT")]
    [InlineData("Intel(R) Arc(TM) Graphics", "Intel(R) Arc(TM) A770 Graphics")]
    public void Choose_TakesACardOfItsOwnBeforeBuiltInGraphics_HoweverMuchMemoryTheseSayIsFree(string builtIn, string card)
    {
        var integrated = new EngineDevice("Vulkan0", 16 * Gibibyte, builtIn);
        var discrete = new EngineDevice("Vulkan1", 6 * Gibibyte, card);

        Assert.Equal((0, 2), (integrated.Rank, discrete.Rank));
        Assert.Equal(["Vulkan1"], EngineDevices.Choose([integrated, discrete], EngineDevices.RequiredBytes(3 * Gibibyte, 0)));

        // A model the card cannot hold whole still goes to the card alone (the engine keeps the rest on the processor), never to both.
        Assert.Equal(["Vulkan1"], EngineDevices.Choose([integrated, discrete], EngineDevices.RequiredBytes(12 * Gibibyte, 0)));
    }

    [Fact]
    public void Choose_UsesBuiltInGraphics_WhenThereIsNothingElse_AndTreatsACardItCannotNameAsBefore()
    {
        var integrated = new EngineDevice("Vulkan0", 16 * Gibibyte, "Intel(R) UHD Graphics 770");
        Assert.Equal(["Vulkan0"], EngineDevices.Choose([integrated], EngineDevices.RequiredBytes(3 * Gibibyte, 0)));

        // Two cards of the same kind: the roomiest, as before.
        var first = new EngineDevice("Vulkan0", 8 * Gibibyte, "NVIDIA GeForce RTX 2080");
        var second = new EngineDevice("Vulkan1", 11 * Gibibyte, "NVIDIA GeForce RTX 5070");
        Assert.Equal(["Vulkan1"], EngineDevices.Choose([first, second], EngineDevices.RequiredBytes(3 * Gibibyte, 0)));
        Assert.Equal(1, new EngineDevice("Vulkan0", Gibibyte).Rank);
    }

    [Fact]
    public void RequiredBytes_CountsTheProjector_AndRoomForTheContext()
    {
        var required = EngineDevices.RequiredBytes(10 * Gibibyte, Gibibyte);

        // Weights 10, projector 1, a tenth of the weights, and a gibibyte of working memory.
        Assert.InRange(required, (13 * Gibibyte) - (Gibibyte / 100), (13 * Gibibyte) + (Gibibyte / 100));
    }

    [Fact]
    public void Arguments_NameTheDevices_OrNoneForTheCpuAlone_OrLeaveTheChoiceToTheEngine()
    {
        var model = new ModelProcessLaunch(@"C:\models\m.gguf");

        Assert.DoesNotContain("--device", LlamaServerCommand.Arguments(model, @"C:\s\e.sock"));
        Assert.Equal(
            "Vulkan0,Vulkan1",
            ValueAfter(LlamaServerCommand.Arguments(model with { Devices = ["Vulkan0", "Vulkan1"] }, @"C:\s\e.sock"), "--device"));
        Assert.Equal("none", ValueAfter(LlamaServerCommand.Arguments(model with { Devices = [] }, @"C:\s\e.sock"), "--device"));
    }

    [Fact]
    public async Task Start_OffloadsToTheChosenDevice()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var probe = new FakeProbe([Small, Big]);
        await using var manager = CreateManager(setup, probe);

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());

        Assert.Equal("Vulkan0", ValueAfter(setup.Report(1).Arguments, "--device"));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Start_LeavesTheChoiceToTheEngine_WhenNoDeviceIsFound()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var manager = CreateManager(setup, new FakeProbe([]));

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());

        Assert.DoesNotContain("--device", setup.Report(1).Arguments);
    }

    [Fact]
    public async Task Start_RetriesAnEmptyProbe_BeforeChoosingAutomaticDevices()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var probe = new FakeProbe([]) { LaterDevices = [Big] };
        await using var manager = CreateManager(setup, probe);

        await manager.StartAsync(setup.Launch, TestPipes.Timeout());

        Assert.Equal(2, probe.Calls);
        Assert.Equal("Vulkan0", ValueAfter(setup.Report(1).Arguments, "--device"));
    }

    [Fact]
    public async Task Start_KeepsTheDevicesALaunchNames_WithoutListingAny()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        var probe = new FakeProbe([Big]);
        await using var manager = CreateManager(setup, probe);

        await manager.StartAsync(setup.Launch with { Devices = [] }, TestPipes.Timeout());

        Assert.Equal("none", ValueAfter(setup.Report(1).Arguments, "--device"));
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_RetriesTheGpu_WhenTheFirstLoadFails(bool explicitDevice)
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario
        {
            StartupFailure = "model",
            StartupFailureOnDevicesOnly = true,
            StartupFailureThroughLaunch = 1,
        });
        await using var manager = CreateManager(setup, new FakeProbe([Big]));

        await manager.StartAsync(explicitDevice ? setup.Launch with { Devices = ["Vulkan0"] } : setup.Launch, TestPipes.Timeout());

        Assert.Equal(ModelProcessState.Running, manager.State);
        Assert.Equal(2, setup.Launches);
        Assert.Equal("Vulkan0", ValueAfter(setup.Report(1).Arguments, "--device"));
        Assert.Equal("Vulkan0", ValueAfter(setup.Report(2).Arguments, "--device"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_ReportsPersistentGpuFailure_WithoutSilentlyLoadingOnCpu(bool explicitDevice)
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario
        {
            StartupFailure = "model",
            StartupFailureOnDevicesOnly = true,
        });
        await using var manager = CreateManager(setup, new FakeProbe([Big]));

        var failure = await Assert.ThrowsAsync<ModelProcessException>(() => manager.StartAsync(
            explicitDevice ? setup.Launch with { Devices = ["Vulkan0"] } : setup.Launch, TestPipes.Timeout()));

        Assert.Equal(ModelProcessFailure.ModelLoadFailed, failure.Failure);
        Assert.Equal(ModelProcessState.Failed, manager.State);
        Assert.Equal(2, setup.Launches);
        Assert.Equal("Vulkan0", ValueAfter(setup.Report(1).Arguments, "--device"));
        Assert.Equal("Vulkan0", ValueAfter(setup.Report(2).Arguments, "--device"));
        Assert.Null(manager.ProcessId);
    }

    [Fact]
    public async Task Start_DoesNotRetry_WhenTheEngineFailedWithoutDevices()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { StartupFailure = "model" });
        await using var manager = CreateManager(setup, new FakeProbe([]));

        var failed = await Assert.ThrowsAsync<ModelProcessException>(() => manager.StartAsync(setup.Launch, TestPipes.Timeout()));

        Assert.Equal(ModelProcessFailure.ModelLoadFailed, failed.Failure);
        Assert.Equal(1, setup.Launches);
    }

    private static ModelProcessManager CreateManager(FakeEngineSetup setup, IEngineDeviceProbe probe) =>
        new(setup.Locator, setup.Options(), TimeProvider.System, NullLogger<ModelProcessManager>.Instance, probe);

    private static string? ValueAfter(IEnumerable<string> arguments, string option)
    {
        var list = arguments.ToList();
        var index = list.IndexOf(option);
        return index >= 0 && index + 1 < list.Count ? list[index + 1] : null;
    }

    private sealed class FakeProbe(IReadOnlyList<EngineDevice> devices) : IEngineDeviceProbe
    {
        public int Calls { get; private set; }
        public IReadOnlyList<EngineDevice>? LaterDevices { get; init; }

        public Task<IReadOnlyList<EngineDevice>> ListAsync(ModelRuntime runtime, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Calls > 1 ? LaterDevices ?? devices : devices);
        }
    }
}
