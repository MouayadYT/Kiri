using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.BrowserBridge.Tests;

/// <summary>
/// The real <c>Assistant.BrowserBridge.exe</c>, started the way a browser starts it (the extension's origin as its argument, standard
/// streams redirected), against an app that is a server in this process on a pipe of the test's own. The real app and its pipe are never used.
/// </summary>
public sealed class NativeHostProcessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly string Executable = Path.Combine(AppContext.BaseDirectory, "Assistant.BrowserBridge.exe");

    [Fact]
    public async Task TheHostAnswersTheBrowserOnItsStandardOutput_AndHandsTheSelectionToTheAppPipe()
    {
        Assert.True(File.Exists(Executable), Executable);
        var pipe = LocalPipe.CreateUniqueName("Assistant.Tests.Host");
        var app = new RecordingApp();
        using var stop = new CancellationTokenSource();
        var serving = new InvocationServer(pipe, app, NullLogger<InvocationServer>.Instance).RunAsync(stop.Token);
        var selection = new BrowserSelection("Text with “quotes”,\r\nlines and ünïcode \U0001F98A", false, "A page — title", "https://example.test/p?q=1", "Brave");

        using var host = Process.Start(new ProcessStartInfo(
            Executable, $"chrome-extension://{ExtensionIdentity()}/ --parent-window=0 --pipe {pipe}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            using var timeout = new CancellationTokenSource(Wait);
            var input = host.StandardInput.BaseStream;
            var output = host.StandardOutput.BaseStream;

            await IpcFraming.WriteFrameAsync(input, Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"ping\"}"), timeout.Token);
            Assert.Equal("{\"v\":1,\"type\":\"pong\"}", await ReadReplyAsync(output, timeout.Token));

            await IpcFraming.WriteFrameAsync(input, SelectionMessage(selection), timeout.Token);
            Assert.Equal("{\"v\":1,\"type\":\"accepted\"}", await ReadReplyAsync(output, timeout.Token));
            var request = Assert.Single(app.Requests);
            Assert.Equal(InvocationAction.AskAboutBrowserSelection, request.Action);
            Assert.Equal(selection, request.Selection);

            // The page's text around the selection, which the extension sends only in "Selection + Nearby Context" (step 88), arrives with it.
            var withNearby = selection with { NearbyBefore = "Before it, a line.\nAnother “line”.", NearbyAfter = "After it 🦊 and more…" };
            await IpcFraming.WriteFrameAsync(input, SelectionMessage(withNearby), timeout.Token);
            Assert.Equal("{\"v\":1,\"type\":\"accepted\"}", await ReadReplyAsync(output, timeout.Token));
            Assert.Equal(withNearby, app.Requests[1].Selection);
            Assert.True(app.Requests[1].Selection!.HasNearbyContext);
            Assert.False(request.Selection!.HasNearbyContext);

            // A message that cannot be read is answered, and the host goes on.
            await IpcFraming.WriteFrameAsync(input, Encoding.UTF8.GetBytes("{oops"), timeout.Token);
            Assert.Equal(
                "{\"v\":1,\"type\":\"error\",\"body\":{\"code\":\"malformed\"}}", await ReadReplyAsync(output, timeout.Token));

            // The browser closes its end after the last message: the host ends with success and has written nothing more.
            input.Close();
            await host.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, host.ExitCode);
            Assert.Equal(-1, host.StandardOutput.BaseStream.ReadByte());
            Assert.Equal("", await host.StandardError.ReadToEndAsync(timeout.Token));
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }

            await stop.CancelAsync();
            await serving;
        }
    }

    [Fact]
    public async Task ABrokenFrameEndsTheHostWithAFailureAndNoReply()
    {
        using var host = Process.Start(new ProcessStartInfo(Executable, "chrome-extension://x/ --pipe Assistant.Tests.Unused")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            using var timeout = new CancellationTokenSource(Wait);
            await host.StandardInput.BaseStream.WriteAsync(new byte[] { 0, 0, 0, 0 }, timeout.Token);
            await host.StandardInput.BaseStream.FlushAsync(timeout.Token);
            await host.WaitForExitAsync(timeout.Token);

            Assert.Equal(1, host.ExitCode);
            Assert.Equal(-1, host.StandardOutput.BaseStream.ReadByte());
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task AnUnknownCommandIsAUsageError_AndNothingIsWritten()
    {
        using var host = Process.Start(new ProcessStartInfo(Executable, "register --extension-id nope")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        using var timeout = new CancellationTokenSource(Wait);
        await host.WaitForExitAsync(timeout.Token);

        Assert.Equal(2, host.ExitCode);
        Assert.Equal("", await host.StandardOutput.ReadToEndAsync(timeout.Token));
    }

    // What the extension sends: the envelope with the selection, as JavaScript's JSON.stringify writes it (no escapes for non-ASCII).
    private static byte[] SelectionMessage(BrowserSelection selection)
    {
        // The nearby fields are present only when the extension had page text to send, as its worker spreads them into the request.
        var body = new Dictionary<string, object>
        {
            ["selectionText"] = selection.Text,
            ["selectionTruncated"] = selection.IsTruncated,
            ["pageTitle"] = selection.PageTitle,
            ["pageUrl"] = selection.PageUrl,
            ["browserName"] = selection.BrowserName,
        };
        if (selection.NearbyBefore.Length > 0)
        {
            body["nearbyBefore"] = selection.NearbyBefore;
        }

        if (selection.NearbyAfter.Length > 0)
        {
            body["nearbyAfter"] = selection.NearbyAfter;
        }

        var message = new Dictionary<string, object> { ["v"] = 1, ["type"] = "selection", ["body"] = body };
        return JsonSerializer.SerializeToUtf8Bytes(
            message, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static async Task<string> ReadReplyAsync(Stream output, CancellationToken cancellationToken)
    {
        var payload = await IpcFraming.ReadFrameAsync(output, 1 << 20, cancellationToken);
        return Encoding.UTF8.GetString(payload ?? throw new InvalidOperationException("The host closed its output."));
    }

    // The id the manifest's key gives (ExtensionIdentityTests checks it): the origin a browser would pass.
    private static string ExtensionIdentity() => Registration.ExtensionIdentity.Id;

    private sealed class RecordingApp : IInvocationHandler
    {
        private readonly List<InvocationRequest> _requests = [];

        public IReadOnlyList<InvocationRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public InvocationReply Handle(InvocationRequest request)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return InvocationReply.Accepted;
        }
    }
}
