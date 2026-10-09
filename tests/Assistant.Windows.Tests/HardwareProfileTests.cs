using Assistant.Core.Hardware;
using Assistant.Windows.Hardware;
using Xunit;
using Xunit.Abstractions;

namespace Assistant.Windows.Tests;

/// <summary>The hardware profile read from Windows (step 124): each part on its own, and the real readers on this PC.</summary>
public sealed class HardwareProfileTests(ITestOutputHelper output)
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly CpuInfo SomeCpu = new("Test CPU", 8, 16, "X64", 3000);
    private static readonly SystemMemoryInfo SomeMemory = new(32 * GiB, 20 * GiB);
    private static readonly GpuAdapterInfo SomeGpu = new("Test GPU", 0x10DE, GpuAdapterKind.Hardware, 12 * GiB, 16 * GiB, 10 * GiB);

    private static WindowsHardwareProfileService Service(
        ICpuInfoSource? cpu = null, IMemoryInfoSource? memory = null, IGpuInfoSource? gpu = null) =>
        new(cpu ?? new FakeCpu(SomeCpu), memory ?? new FakeMemory(SomeMemory), gpu ?? new FakeGpu([SomeGpu]));

    [Fact]
    public void TheProfile_IsMadeOfWhatTheSourcesSay_AndNothingIsUnreadable()
    {
        var profile = Service().Current;

        Assert.Equal(SomeCpu, profile.Cpu);
        Assert.Equal(SomeMemory, profile.Memory);
        Assert.Equal([SomeGpu], profile.Gpus);
        Assert.Equal(HardwareParts.None, profile.Unreadable);
        Assert.Same(SomeGpu, profile.BestGpu);
    }

    [Fact]
    public void ThePartThatCannotBeRead_IsNamed_AndTheOthersStayIntact()
    {
        var graphics = Service(gpu: new FakeGpu(throws: new InvalidOperationException())).Current;
        var memory = Service(memory: new FakeMemory(throws: new InvalidOperationException())).Current;
        var processor = Service(cpu: new FakeCpu(throws: new InvalidOperationException())).Current;

        Assert.Equal(HardwareParts.Graphics, graphics.Unreadable);
        Assert.Equal(SomeCpu, graphics.Cpu);
        Assert.Equal(SomeMemory, graphics.Memory);
        Assert.Empty(graphics.Gpus);

        Assert.Equal(HardwareParts.Memory, memory.Unreadable);
        Assert.False(memory.Memory.IsKnown);
        Assert.Equal([SomeGpu], memory.Gpus);

        Assert.Equal(HardwareParts.Processor, processor.Unreadable);
        Assert.Equal(SomeMemory, processor.Memory);
    }

    [Fact]
    public void AMemoryOfZero_CountsAsUnreadable()
    {
        var profile = Service(memory: new FakeMemory(new SystemMemoryInfo(0, 0))).Current;

        Assert.Equal(HardwareParts.Memory, profile.Unreadable);
    }

    [Fact]
    public void CurrentIsReadOnce_AndRefreshReadsAgain()
    {
        var memory = new FakeMemory(SomeMemory);
        var service = Service(memory: memory);

        var first = service.Current;
        memory.Next = new SystemMemoryInfo(32 * GiB, 5 * GiB);
        Assert.Same(first, service.Current);
        Assert.Equal(1, memory.Reads);

        var refreshed = service.Refresh();

        Assert.Equal(5 * GiB, refreshed.Memory.AvailableBytes);
        Assert.Same(refreshed, service.Current);
        Assert.Equal(2, memory.Reads);
    }

    [Fact]
    public void TheBestGpu_IsTheRealAdapterWithTheMostMemoryOfItsOwn()
    {
        var software = new GpuAdapterInfo("Basic Render Driver", 0x1414, GpuAdapterKind.Software, 0, 8 * GiB, null);
        var shared = new GpuAdapterInfo("Built-in graphics", 0x8086, GpuAdapterKind.Hardware, 0, 16 * GiB, null);
        var small = new GpuAdapterInfo("Small card", 0x10DE, GpuAdapterKind.Hardware, 8 * GiB, 0, 7 * GiB);
        var large = new GpuAdapterInfo("Large card", 0x10DE, GpuAdapterKind.Hardware, 12 * GiB, 0, 11 * GiB);

        var profile = Service(gpu: new FakeGpu([software, shared, small, large])).Current;

        Assert.Same(large, profile.BestGpu);
        Assert.Null(Service(gpu: new FakeGpu([software, shared])).Current.BestGpu);
        Assert.Null(Service(gpu: new FakeGpu([])).Current.BestGpu);
    }

    [Fact]
    public void TheProcessorsNameIsTidied()
    {
        Assert.Equal("Intel(R) Core(TM) i5-14600K", NativeCpuInfoSource.Tidy("  Intel(R)  Core(TM) i5-14600K   "));
        Assert.Equal(string.Empty, NativeCpuInfoSource.Tidy(null));
    }

    // The real readers, on whatever PC runs the tests: what must hold everywhere, and the numbers for the person reading the log.
    [Fact]
    public void TheRealReaders_ReportThisPC()
    {
        var profile = new WindowsHardwareProfileService().Current;

        output.WriteLine($"CPU: {profile.Cpu.Name} | {profile.Cpu.PhysicalCores} cores, {profile.Cpu.LogicalProcessors} threads, {profile.Cpu.MaxClockMegahertz} MHz, {profile.Cpu.Architecture}");
        output.WriteLine($"Memory: {profile.Memory.TotalGiB:0.0} GiB, {profile.Memory.AvailableGiB:0.0} GiB free");
        foreach (var gpu in profile.Gpus)
        {
            output.WriteLine(
                $"GPU: {gpu.Name} | {gpu.Kind} | vendor 0x{gpu.VendorId:X4} | dedicated {gpu.DedicatedVideoMemoryGiB:0.0} GiB | " +
                $"shared {gpu.SharedSystemMemoryBytes / (double)GiB:0.0} GiB | free {gpu.AvailableDedicatedVideoMemoryGiB:0.0} GiB");
        }

        Assert.Equal(HardwareParts.None, profile.Unreadable);
        Assert.True(profile.Cpu.LogicalProcessors >= Environment.ProcessorCount);
        Assert.True(profile.Cpu.PhysicalCores > 0);
        Assert.True(profile.Cpu.PhysicalCores <= profile.Cpu.LogicalProcessors);
        Assert.False(string.IsNullOrWhiteSpace(profile.Cpu.Name));
        Assert.True(profile.Memory.TotalBytes >= 1 * GiB);
        Assert.InRange(profile.Memory.AvailableBytes, 1, profile.Memory.TotalBytes);

        // The runtime's own figure for the memory is the same one, so the preset that is picked from either agrees.
        Assert.InRange(profile.Memory.TotalBytes / (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 0.95, 1.05);
        Assert.All(profile.Gpus, gpu =>
        {
            Assert.False(string.IsNullOrWhiteSpace(gpu.Name));
            Assert.True(gpu.DedicatedVideoMemoryBytes >= 0);
            if (gpu.AvailableDedicatedVideoMemoryBytes is { } free)
            {
                Assert.InRange(free, 0, Math.Max(gpu.DedicatedVideoMemoryBytes, free));
            }
        });
    }

    [Fact]
    public void TheRealReaders_RefreshReadsTheFreeMemoryAgain()
    {
        var service = new WindowsHardwareProfileService();
        var first = service.Current;

        var second = service.Refresh();

        Assert.Equal(first.Cpu, second.Cpu);
        Assert.Equal(first.Memory.TotalBytes, second.Memory.TotalBytes);
        Assert.Equal(first.Gpus.Select(gpu => gpu.Name), second.Gpus.Select(gpu => gpu.Name));
    }

    private sealed class FakeCpu(CpuInfo? info = null, Exception? throws = null) : ICpuInfoSource
    {
        public CpuInfo Read() => throws is null ? info! : throw throws;
    }

    private sealed class FakeMemory(SystemMemoryInfo? info = null, Exception? throws = null) : IMemoryInfoSource
    {
        public SystemMemoryInfo? Next { get; set; } = info;

        public int Reads { get; private set; }

        public SystemMemoryInfo Read()
        {
            Reads++;
            return throws is null ? Next! : throw throws;
        }
    }

    private sealed class FakeGpu(IReadOnlyList<GpuAdapterInfo>? adapters = null, Exception? throws = null) : IGpuInfoSource
    {
        public IReadOnlyList<GpuAdapterInfo> Read() => throws is null ? adapters! : throw throws;
    }
}
