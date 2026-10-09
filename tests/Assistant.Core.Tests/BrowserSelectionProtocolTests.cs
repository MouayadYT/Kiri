using System.IO.Pipes;
using System.Text;
using Assistant.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// The browser selection on the app pipe (PROJECT_SPEC §4.5, §5.7): the request the native-messaging host sends, its checks and its limits.
/// </summary>
public sealed class BrowserSelectionProtocolTests
{
    private static readonly BrowserSelection Sample = new("Quoted “text”\nwith a line break", IsTruncated: false, "Fox — a page", "https://example.test/a?b=c#d");

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void ASelectionRequestRoundTrips_AndTheEnvelopeIsTheDocumentedOne()
    {
        var payload = InvocationProtocol.EncodeRequest(InvocationRequest.ForBrowserSelection(Sample with { IsTruncated = true }));

        Assert.True(InvocationProtocol.TryDecodeRequest(payload, out var request, out _));
        Assert.Equal(InvocationAction.AskAboutBrowserSelection, request!.Action);
        Assert.Empty(request.Paths);
        Assert.Equal(Sample with { IsTruncated = true }, request.Selection);

        var json = Encoding.UTF8.GetString(payload);
        Assert.StartsWith("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":", json, StringComparison.Ordinal);
        Assert.Contains("\"selectionTruncated\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"pageTitle\":", json, StringComparison.Ordinal);
        Assert.Contains("\"pageUrl\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageTitleAddressAndFlagAreOptional_AndUnknownFieldsAreIgnored()
    {
        Assert.True(InvocationProtocol.TryDecodeRequest(
            Bytes("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"hi\",\"later\":[1,2]},\"extra\":true}"),
            out var request, out _));

        Assert.Equal(new BrowserSelection("hi", false, "", ""), request!.Selection);
    }

    [Theory]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\"}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":[]}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{}}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":7}}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"\"}}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"   \"}}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"hi\",\"pageTitle\":3}}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"hi\",\"pageUrl\":null}}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"hi\",\"selectionTruncated\":\"yes\"}}")]
    public void ASelectionRequestThatIsNotOneIsMalformed(string json)
    {
        Assert.False(InvocationProtocol.TryDecodeRequest(Bytes(json), out var request, out var error));
        Assert.Null(request);
        Assert.Equal(InvocationErrorCode.Malformed, error);
    }

    [Fact]
    public void TheLimitsAreEnforced_AndTheLongestAllowedPassesWithEveryCharacterEscaped()
    {
        BrowserSelection Of(int text, int title, int url) => new(new string('a', text), false, new string('t', title), new string('u', url));

        foreach (var over in new[]
                 {
                     Of(BrowserSelection.MaxTextLength + 1, 0, 0),
                     Of(1, BrowserSelection.MaxTitleLength + 1, 0),
                     Of(1, 0, BrowserSelection.MaxUrlLength + 1),
                 })
        {
            var json = Encoding.UTF8.GetString(InvocationProtocol.EncodeRequest(InvocationRequest.ForBrowserSelection(over)));
            Assert.False(InvocationProtocol.TryDecodeRequest(Bytes(json), out _, out var error));
            Assert.Equal(InvocationErrorCode.Malformed, error);
        }

        // The most text, each character a control character that JSON writes as a six-byte escape, still fits in a frame.
        var longest = new BrowserSelection(
            new string('\u0001', BrowserSelection.MaxTextLength), true, new string('\u0002', BrowserSelection.MaxTitleLength),
            new string('u', BrowserSelection.MaxUrlLength));
        var payload = InvocationProtocol.EncodeRequest(InvocationRequest.ForBrowserSelection(longest));
        Assert.True(payload.Length < InvocationProtocol.MaxFrameLength, $"{payload.Length} bytes");
        Assert.True(InvocationProtocol.TryDecodeRequest(payload, out var request, out _));
        Assert.Equal(longest, request!.Selection);
    }

    // ---- The page's text around the selection (step 88) -----------------------------------------------------------------------

    [Fact]
    public void PageTextAroundTheSelectionRoundTrips_IsWrittenOnlyWhenThereIsSome_AndIsOptional()
    {
        var plain = Encoding.UTF8.GetString(InvocationProtocol.EncodeRequest(InvocationRequest.ForBrowserSelection(Sample)));
        Assert.DoesNotContain("nearby", plain, StringComparison.Ordinal);
        Assert.False(Sample.HasNearbyContext);

        var around = Sample with { NearbyBefore = "Before “it”.\nSecond line.", NearbyAfter = "After it 🦊…" };
        var payload = InvocationProtocol.EncodeRequest(InvocationRequest.ForBrowserSelection(around));
        Assert.True(InvocationProtocol.TryDecodeRequest(payload, out var request, out _));
        Assert.Equal(around, request!.Selection);
        Assert.True(request.Selection!.HasNearbyContext);
        var json = Encoding.UTF8.GetString(payload);
        Assert.Contains("\"nearbyBefore\":", json, StringComparison.Ordinal);
        Assert.Contains("\"nearbyAfter\":", json, StringComparison.Ordinal);

        // One side is enough, and a request from before the option existed has neither.
        Assert.True(InvocationProtocol.TryDecodeRequest(
            Bytes("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"hi\",\"nearbyAfter\":\"next\"}}"), out var one, out _));
        Assert.Equal(new BrowserSelection("hi", false, "", "", "", "", "next"), one!.Selection);
        Assert.True(one.Selection!.HasNearbyContext);
        Assert.False(new BrowserSelection("hi", false, "", "", "", "  ", "\n").HasNearbyContext);
    }

    [Theory]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"hi\",\"nearbyBefore\":3}}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"hi\",\"nearbyAfter\":null}}")]
    [InlineData("{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"hi\",\"nearbyBefore\":[\"a\"]}}")]
    public void PageTextOfTheWrongKindIsMalformed(string json)
    {
        Assert.False(InvocationProtocol.TryDecodeRequest(Bytes(json), out var request, out var error));
        Assert.Null(request);
        Assert.Equal(InvocationErrorCode.Malformed, error);
    }

    [Fact]
    public void PageTextOverTheLimitIsMalformed_AndTheLongestAllowedPassesWithEveryCharacterEscaped()
    {
        foreach (var over in new[]
                 {
                     Sample with { NearbyBefore = new string('b', BrowserSelection.MaxNearbyLength + 1) },
                     Sample with { NearbyAfter = new string('a', BrowserSelection.MaxNearbyLength + 1) },
                 })
        {
            Assert.False(InvocationProtocol.TryDecodeRequest(
                InvocationProtocol.EncodeRequest(InvocationRequest.ForBrowserSelection(over)), out _, out var error));
            Assert.Equal(InvocationErrorCode.Malformed, error);
        }

        var longest = Sample with
        {
            NearbyBefore = new string('\u0001', BrowserSelection.MaxNearbyLength),
            NearbyAfter = new string('\u0002', BrowserSelection.MaxNearbyLength),
        };
        var payload = InvocationProtocol.EncodeRequest(InvocationRequest.ForBrowserSelection(longest));
        Assert.True(payload.Length < InvocationProtocol.MaxFrameLength, $"{payload.Length} bytes");
        Assert.True(InvocationProtocol.TryDecodeRequest(payload, out var request, out _));
        Assert.Equal(longest, request!.Selection);
    }

    [Fact]
    public void AnUnpairedSurrogateWrittenAsAnEscapeIsMalformed_NotACrash()
    {
        var json = "{\"v\":1,\"type\":\"askAboutBrowserSelection\",\"body\":{\"selectionText\":\"a" + (char)92 + "ud800b\"}}";

        Assert.False(InvocationProtocol.TryDecodeRequest(Bytes(json), out var request, out var error));
        Assert.Null(request);
        Assert.Equal(InvocationErrorCode.Malformed, error);
    }

    [Fact]
    public void ARequestForTheWrongActionHoldsNoSelection_AndASelectionRequestCannotBeBuiltWithoutOne()
    {
        Assert.Throws<ArgumentException>(() =>
            InvocationProtocol.EncodeRequest(new InvocationRequest(InvocationAction.AskAboutBrowserSelection, [])));
        Assert.Throws<ArgumentNullException>(() => InvocationRequest.ForBrowserSelection(null!));
        Assert.Null(new InvocationRequest(InvocationAction.AskAboutFiles, [@"C:\a.txt"]).Selection);
    }

    [Fact]
    public async Task AServerHandsTheSelectionToItsHandler_ThroughARealPipe()
    {
        var name = LocalPipe.CreateUniqueName("Assistant.Tests.BrowserSelection");
        var handler = new RecordingHandler();
        using var stop = new CancellationTokenSource();
        var server = new InvocationServer(name, handler, NullLogger<InvocationServer>.Instance);
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, LocalPipe.Options);
            await pipe.ConnectAsync(10_000);
            var reply = await InvocationClient.SendAsync(pipe, InvocationRequest.ForBrowserSelection(Sample));

            Assert.True(reply.IsAccepted);
            var request = Assert.Single(handler.Requests);
            Assert.Equal(InvocationAction.AskAboutBrowserSelection, request.Action);
            Assert.Equal(Sample, request.Selection);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
        }
    }

    private sealed class RecordingHandler : IInvocationHandler
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
