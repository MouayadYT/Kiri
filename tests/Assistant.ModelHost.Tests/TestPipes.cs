using System.IO.Pipes;
using Assistant.Core.Ipc;

namespace Assistant.ModelHost.Tests;

/// <summary>Real named pipes for tests, with the same options as the app and the host.</summary>
internal static class TestPipes
{
    public static string NewName() => LocalPipe.CreateUniqueName("Assistant.ModelHost.Tests");

    /// <summary>A connected pair: the host's end and the app's end.</summary>
    public static async Task<(NamedPipeServerStream Host, NamedPipeClientStream App)> ConnectAsync()
    {
        var name = NewName();
        var host = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, LocalPipe.Options);
        var app = new NamedPipeClientStream(".", name, PipeDirection.InOut, LocalPipe.Options);
        await Task.WhenAll(host.WaitForConnectionAsync(), app.ConnectAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        return (host, app);
    }

    /// <summary>A token that ends a test that hangs instead of letting it block the run.</summary>
    public static CancellationToken Timeout(int seconds = 20) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
