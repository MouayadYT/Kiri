using System.Text;
using Assistant.Core.Domain;
using Assistant.Core.Ipc;
using Assistant.Core.ModelHosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>The app's end of the connection, against a host played by hand.</summary>
public sealed class ModelHostClientTests
{
    [Fact]
    public async Task HostOfAnotherVersion_FailsThePing()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);

        var ping = client.PingAsync(TestPipes.Timeout());
        var request = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        var reply = $$$"""{"v":2,"id":{{{request!.Id}}},"type":"healthReport","body":{"somethingNew":true}}""";
        await IpcFraming.WriteFrameAsync(host, Encoding.UTF8.GetBytes(reply), TestPipes.Timeout());

        var exception = await Assert.ThrowsAsync<ModelHostException>(() => ping);
        Assert.Equal(ModelHostErrorCode.UnsupportedProtocolVersion, exception.Code);
    }

    [Fact]
    public async Task ErrorReply_FailsTheRequestWithItsCode()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);

        var ping = client.PingAsync(TestPipes.Timeout());
        var request = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        await hostChannel.SendAsync(request!.Id, new ModelHostError(ModelHostErrorCode.Busy), TestPipes.Timeout());

        var exception = await Assert.ThrowsAsync<ModelHostException>(() => ping);
        Assert.Equal(ModelHostErrorCode.Busy, exception.Code);
    }

    [Fact]
    public async Task RepliesOutOfOrder_ReachTheirRequests()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);

        var first = client.PingAsync(TestPipes.Timeout());
        var firstId = (await hostChannel.ReceiveAsync(TestPipes.Timeout()))!.Id;
        var second = client.PingAsync(TestPipes.Timeout());
        var secondId = (await hostChannel.ReceiveAsync(TestPipes.Timeout()))!.Id;
        await hostChannel.SendAsync(secondId, new HealthReport(new Version(2, 0), 222, TimeSpan.Zero), TestPipes.Timeout());
        await hostChannel.SendAsync(firstId, new HealthReport(new Version(1, 0), 111, TimeSpan.Zero), TestPipes.Timeout());

        Assert.Equal(111, (await first).ProcessId);
        Assert.Equal(222, (await second).ProcessId);
    }

    [Fact]
    public async Task HostClosingTheConnection_FailsWaitingRequests_AndLaterOnes()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);

        var ping = client.PingAsync(TestPipes.Timeout());
        await hostChannel.ReceiveAsync(TestPipes.Timeout());
        await hostChannel.DisposeAsync();

        var waiting = await Assert.ThrowsAsync<ModelHostException>(() => ping);
        await client.Completion.WaitAsync(TestPipes.Timeout());
        var later = await Assert.ThrowsAsync<ModelHostException>(() => client.PingAsync(TestPipes.Timeout()));
        Assert.Null(waiting.Code);
        Assert.Null(later.Code);
    }

    [Fact]
    public async Task Generation_StreamsTheHostsRepliesInOrder_AndEndsWithItsEnd()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);

        var reading = ReadAllAsync(client.GenerateAsync(SampleMessages.GenerateText, TestPipes.Timeout()));
        var request = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        var call = new ToolCallGenerated(new ToolCall("call-1", "read_file", "{}"));
        await hostChannel.SendAsync(request!.Id, new TextDelta("Hel"), TestPipes.Timeout());
        await hostChannel.SendAsync(request.Id, new TextDelta("lo"), TestPipes.Timeout());
        await hostChannel.SendAsync(request.Id, call, TestPipes.Timeout());
        await hostChannel.SendAsync(request.Id, new GenerationEnded(GenerationStopReason.Completed), TestPipes.Timeout());

        Assert.IsType<GenerateTextRequest>(request.Message);
        Assert.Equal(
            [new TextDelta("Hel"), new TextDelta("lo"), call, new GenerationEnded(GenerationStopReason.Completed)],
            await reading);
    }

    [Fact]
    public async Task AnErrorAfterSomeText_FailsTheStreamWithItsCode()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);
        var received = new List<ModelHostReply>();

        var reading = Task.Run(async () =>
        {
            await foreach (var reply in client.GenerateAsync(SampleMessages.GenerateText, TestPipes.Timeout()))
            {
                received.Add(reply);
            }
        });
        var request = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        await hostChannel.SendAsync(request!.Id, new TextDelta("Hel"), TestPipes.Timeout());
        await hostChannel.SendAsync(request.Id, new ModelHostError(ModelHostErrorCode.GenerationFailed), TestPipes.Timeout());

        var failure = await Assert.ThrowsAsync<ModelHostException>(() => reading);
        Assert.Equal(ModelHostErrorCode.GenerationFailed, failure.Code);
        Assert.Equal([new TextDelta("Hel")], received);
    }

    [Fact]
    public async Task LeavingAGenerationEarly_AsksTheHostToStopIt_AndTheNextWaitsForItsEnd()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);

        var first = client.GenerateAsync(SampleMessages.GenerateText, TestPipes.Timeout()).GetAsyncEnumerator();
        var moving = first.MoveNextAsync();
        var request = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        await hostChannel.SendAsync(request!.Id, new TextDelta("Hel"), TestPipes.Timeout());
        Assert.True(await moving);
        await first.DisposeAsync();

        var cancel = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        Assert.Equal(new CancelGenerationRequest(request.Id), cancel!.Message);

        // The next generation is not sent while the host is still ending the first, which it would refuse as busy.
        var second = ReadAllAsync(client.GenerateAsync(SampleMessages.GenerateText, TestPipes.Timeout()));
        await hostChannel.SendAsync(request.Id, new TextDelta("lo"), TestPipes.Timeout());
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        await hostChannel.SendAsync(request.Id, new GenerationEnded(GenerationStopReason.Cancelled), TestPipes.Timeout());

        var next = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        Assert.IsType<GenerateTextRequest>(next!.Message);
        await hostChannel.SendAsync(next.Id, new GenerationEnded(GenerationStopReason.Completed), TestPipes.Timeout());
        Assert.Equal([new GenerationEnded(GenerationStopReason.Completed)], await second);
    }

    [Fact]
    public async Task CancellingAGeneration_AsksTheHostToStopIt()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);
        using var cancel = new CancellationTokenSource();

        var reading = ReadAllAsync(client.GenerateAsync(SampleMessages.GenerateText, cancel.Token));
        var request = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        var stop = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        Assert.Equal(new CancelGenerationRequest(request!.Id), stop!.Message);
    }

    [Fact]
    public async Task TheConnectionClosingMidAnswer_FailsTheStream()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);

        var reading = ReadAllAsync(client.GenerateAsync(SampleMessages.GenerateText, TestPipes.Timeout()));
        var request = await hostChannel.ReceiveAsync(TestPipes.Timeout());
        await hostChannel.SendAsync(request!.Id, new TextDelta("Hel"), TestPipes.Timeout());
        await hostChannel.DisposeAsync();

        var failure = await Assert.ThrowsAsync<ModelHostException>(() => reading);
        Assert.Null(failure.Code);
        var later = await Assert.ThrowsAsync<ModelHostException>(
            () => ReadAllAsync(client.GenerateAsync(SampleMessages.GenerateText, TestPipes.Timeout())));
        Assert.Null(later.Code);
    }

    [Fact]
    public async Task CancelledPing_StopsWaiting()
    {
        var (host, app) = await TestPipes.ConnectAsync();
        await using var hostChannel = new ModelHostChannel(host);
        await using var client = new ModelHostClient(app, NullLogger<ModelHostClient>.Instance);
        using var cancel = new CancellationTokenSource();

        var ping = client.PingAsync(cancel.Token);
        await hostChannel.ReceiveAsync(TestPipes.Timeout());
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ping);
    }

    private static async Task<List<ModelHostReply>> ReadAllAsync(IAsyncEnumerable<ModelHostReply> replies)
    {
        var all = new List<ModelHostReply>();
        await foreach (var reply in replies)
        {
            all.Add(reply);
        }

        return all;
    }
}
