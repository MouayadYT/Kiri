using System.Diagnostics;
using Assistant.Core.ModelHosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>The real host executable, started and owned the way the app does it.</summary>
public sealed class ModelHostProcessTests
{
    /// <summary>The host executable, copied next to the tests by the project reference, as it is next to the app.</summary>
    internal static string ExecutablePath { get; } = Path.Combine(AppContext.BaseDirectory, ModelHostProcess.ExecutableName);

    private static ModelHostLaunchOptions Options => ModelHostLaunchOptions.InDirectory(AppContext.BaseDirectory);

    [Fact]
    public async Task Start_LaunchesAHiddenHost_AndPingsIt()
    {
        var host = await ModelHostProcess.StartAsync(Options, NullLoggerFactory.Instance, TestPipes.Timeout());
        await using (host)
        {
            var report = await host.Client.PingAsync(TestPipes.Timeout());

            Assert.Equal(host.ProcessId, report.ProcessId);
            Assert.Equal(typeof(Program).Assembly.GetName().Version, report.HostVersion);
            Assert.Equal(ModelRuntimeState.Ready, report.Runtime);
            using var process = Process.GetProcessById(host.ProcessId);
            Assert.Equal("Assistant.ModelHost", process.ProcessName);
            Assert.Equal(IntPtr.Zero, process.MainWindowHandle);
        }

        Assert.True(host.HasExited);
    }

    [Fact]
    public async Task Dispose_ShutsTheHostDownCleanly()
    {
        var host = await ModelHostProcess.StartAsync(Options, NullLoggerFactory.Instance, TestPipes.Timeout());

        await host.DisposeAsync();

        Assert.True(host.HasExited);
        Assert.Equal(0, host.ExitCode);
        Assert.True(host.Client.Completion.IsCompleted);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(host.ProcessId).Dispose());
    }

    [Fact]
    public async Task HostExits_WhenTheConnectionCloses()
    {
        await using var host = await ModelHostProcess.StartAsync(Options, NullLoggerFactory.Instance, TestPipes.Timeout());

        await host.Client.DisposeAsync();

        await host.WaitForExitAsync(TestPipes.Timeout());
        Assert.Equal(0, host.ExitCode);
    }

    [Fact]
    public async Task HostExits_WhenItsOwnerExits()
    {
        // Host A stands in for an app that crashes. Host B is owned by A and never gets a connection.
        await using var ownerStandIn = await ModelHostProcess.StartAsync(Options, NullLoggerFactory.Instance, TestPipes.Timeout());
        var arguments = new ModelHostArguments(TestPipes.NewName(), ownerStandIn.ProcessId);
        var startInfo = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments.ToCommandLine())
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var owned = Process.Start(startInfo)!;
        await Task.Delay(500);
        Assert.False(owned.HasExited);

        using (var owner = Process.GetProcessById(ownerStandIn.ProcessId))
        {
            owner.Kill();
        }

        await owned.WaitForExitAsync(TestPipes.Timeout());
        Assert.Equal(0, owned.ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--pipe|Assistant.ModelHost.x")]
    [InlineData(@"--pipe|\\.\pipe\x|--owner|1")]
    public async Task HostStartedWithoutItsOwnersArguments_ExitsAtOnce(string commandLine)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in ModelHostArgumentsTests.Split(commandLine))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync(TestPipes.Timeout());

        Assert.Equal(Program.InvalidArgumentsExitCode, process.ExitCode);
    }

    [Fact]
    public async Task MissingExecutable_FailsToStart()
    {
        var options = ModelHostLaunchOptions.InDirectory(Path.Combine(AppContext.BaseDirectory, "no-such-folder"));

        var exception = await Assert.ThrowsAsync<ModelHostException>(
            () => ModelHostProcess.StartAsync(options, NullLoggerFactory.Instance, TestPipes.Timeout()));

        Assert.Null(exception.Code);
    }

    [Fact]
    public void Executable_IsBuiltForTheWindowsSubsystem_SoItNeverShowsAConsole()
    {
        const ushort WindowsGui = 2;
        using var reader = new BinaryReader(File.OpenRead(ExecutablePath));
        reader.BaseStream.Position = 0x3C;
        var peHeader = reader.ReadInt32();

        // PE signature (4 bytes), COFF header (20 bytes), then the optional header, whose Subsystem is at offset 68.
        reader.BaseStream.Position = peHeader + 4 + 20 + 68;

        Assert.Equal(WindowsGui, reader.ReadUInt16());
    }
}
