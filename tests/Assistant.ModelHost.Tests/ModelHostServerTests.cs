using System.Diagnostics;
using System.IO.Pipes;
using Assistant.Core.Ipc;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>The host's pipe and its owner, in this process.</summary>
public sealed class ModelHostServerTests
{
    [Fact]
    public async Task ServesItsOwner_UntilShutdown()
    {
        var name = TestPipes.NewName();
        var run = CreateServer(new ModelHostOptions(name, Environment.ProcessId)).RunAsync(TestPipes.Timeout());

        await using (var client = await ConnectClientAsync(name))
        {
            var report = await client.PingAsync(TestPipes.Timeout());
            Assert.Equal(Environment.ProcessId, report.ProcessId);
            await client.ShutdownAsync(TestPipes.Timeout());
        }

        Assert.Equal(ModelHostExitReason.ShutdownRequested, await run.WaitAsync(TestPipes.Timeout()));
    }

    [Fact]
    public async Task OwnerThatNeverConnects_TimesOut()
    {
        var options = new ModelHostOptions(TestPipes.NewName(), null) { ConnectTimeout = TimeSpan.FromMilliseconds(200) };

        var reason = await CreateServer(options).RunAsync(TestPipes.Timeout());

        Assert.Equal(ModelHostExitReason.ConnectTimedOut, reason);
    }

    [Fact]
    public async Task OwnerThatHasExited_EndsTheHostBeforeItConnects()
    {
        using var gone = Process.Start(new ProcessStartInfo(ModelHostProcessTests.ExecutablePath) { CreateNoWindow = true })!;
        await gone.WaitForExitAsync(TestPipes.Timeout());

        var reason = await CreateServer(new ModelHostOptions(TestPipes.NewName(), gone.Id)).RunAsync(TestPipes.Timeout());

        Assert.Equal(ModelHostExitReason.OwnerExited, reason);
    }

    [Fact]
    public async Task PipeNameAlreadyTaken_IsRefused()
    {
        var name = TestPipes.NewName();
        await using var squatter = new NamedPipeServerStream(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, LocalPipe.Options);

        var reason = await CreateServer(new ModelHostOptions(name, null)).RunAsync(TestPipes.Timeout());

        Assert.Equal(ModelHostExitReason.PipeUnavailable, reason);
    }

    [Fact]
    public async Task OnlyOneConnection_IsAccepted()
    {
        var name = TestPipes.NewName();
        using var stop = new CancellationTokenSource();
        var run = CreateServer(new ModelHostOptions(name, null)).RunAsync(stop.Token);
        await using var owner = await ConnectClientAsync(name);
        await owner.PingAsync(TestPipes.Timeout());

        await using var intruder = new NamedPipeClientStream(".", name, PipeDirection.InOut, LocalPipe.Options);
        await Assert.ThrowsAsync<TimeoutException>(() => intruder.ConnectAsync(300));

        stop.Cancel();
        Assert.Equal(ModelHostExitReason.Stopped, await run.WaitAsync(TestPipes.Timeout()));
    }

    [Fact]
    public async Task OwnerDisconnecting_EndsTheHost()
    {
        var name = TestPipes.NewName();
        var run = CreateServer(new ModelHostOptions(name, null)).RunAsync(TestPipes.Timeout());

        var owner = await ConnectClientAsync(name);
        await owner.PingAsync(TestPipes.Timeout());
        await owner.DisposeAsync();

        Assert.Equal(ModelHostExitReason.OwnerDisconnected, await run.WaitAsync(TestPipes.Timeout()));
    }

    private static ModelHostServer CreateServer(ModelHostOptions options) =>
        new(
            options,
            new ModelHostSession(TestHandlers.Create(new NullModelController()), NullLogger<ModelHostSession>.Instance),
            TimeProvider.System,
            NullLogger<ModelHostServer>.Instance);

    private static async Task<ModelHostClient> ConnectClientAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, LocalPipe.Options);
        await pipe.ConnectAsync(TestPipes.Timeout());
        return new ModelHostClient(pipe, NullLogger<ModelHostClient>.Instance);
    }
}
