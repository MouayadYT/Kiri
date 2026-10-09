using System.Text;
using Assistant.BrowserBridge.Protocol;
using Assistant.Core.Ipc;
using Xunit;

namespace Assistant.BrowserBridge.Tests;

/// <summary>The extension-to-host protocol, version 1 (PROJECT_SPEC §4.5, §5.7).</summary>
public sealed class BrowserMessageProtocolTests
{
    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void ASelectionMessageRoundTrips_AndIsTheEnvelopeTheExtensionBuilds()
    {
        var selection = new BrowserSelection("line one\nline “two”", true, "Título — page", "https://example.test/ü?x=1", "Microsoft Edge");

        var payload = BrowserMessageProtocol.EncodeSelection(selection);

        Assert.True(BrowserMessageProtocol.TryDecode(payload, out var message, out _));
        Assert.Equal(BrowserMessageKind.Selection, message!.Kind);
        Assert.Equal(selection, message.Selection);

        // With "Selection + Nearby Context" (step 88) the extension adds the page's text around the selection, and the host reads it.
        var around = selection with { NearbyBefore = "Before.\nSecond line.", NearbyAfter = "After…" };
        Assert.True(BrowserMessageProtocol.TryDecode(BrowserMessageProtocol.EncodeSelection(around), out var withNearby, out _));
        Assert.Equal(around, withNearby!.Selection);
        Assert.True(BrowserMessageProtocol.TryDecode(
            Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"selection\",\"body\":{\"selectionText\":\"x\",\"nearbyBefore\":\"b\",\"later\":1}}"), out var onlyBefore, out _));
        Assert.Equal(new BrowserSelection("x", false, "", "", "", "b"), onlyBefore!.Selection);
        Assert.StartsWith("{\"v\":1,\"type\":\"selection\",\"body\":{\"selectionText\":", Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
    }

    [Fact]
    public void APingIsAMessageWithNoBody_AndTheRepliesAreTheDocumentedOnes()
    {
        Assert.True(BrowserMessageProtocol.TryDecode(BrowserMessageProtocol.EncodePing(), out var message, out _));
        Assert.Equal(new BrowserMessage(BrowserMessageKind.Ping, null), message);

        Assert.Equal("{\"v\":1,\"type\":\"ping\"}", Encoding.UTF8.GetString(BrowserMessageProtocol.EncodePing()));
        Assert.Equal("{\"v\":1,\"type\":\"pong\"}", Encoding.UTF8.GetString(BrowserMessageProtocol.EncodePong()));
        Assert.Equal("{\"v\":1,\"type\":\"accepted\"}", Encoding.UTF8.GetString(BrowserMessageProtocol.EncodeAccepted()));
    }

    [Fact]
    public void EveryErrorHasItsOwnNameOnTheWire()
    {
        var names = Enum.GetValues<BrowserMessageError>().Select(BrowserMessageProtocol.CodeName).ToArray();

        Assert.Equal(
            ["malformed", "unsupportedProtocolVersion", "unknownType", "appNotFound", "appDidNotRespond", "appRefused"], names);
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(
            "{\"v\":1,\"type\":\"error\",\"body\":{\"code\":\"appNotFound\"}}",
            Encoding.UTF8.GetString(BrowserMessageProtocol.EncodeError(BrowserMessageError.AppNotFound)));
    }

    [Theory]
    [InlineData("", "malformed")]
    [InlineData("not json", "malformed")]
    [InlineData("[]", "malformed")]
    [InlineData("{\"type\":\"ping\"}", "malformed")]
    [InlineData("{\"v\":\"1\",\"type\":\"ping\"}", "malformed")]
    [InlineData("{\"v\":1}", "malformed")]
    [InlineData("{\"v\":1,\"type\":5}", "malformed")]
    [InlineData("{\"v\":0,\"type\":\"ping\"}", "unsupportedProtocolVersion")]
    [InlineData("{\"v\":2,\"type\":\"selection\",\"body\":{\"selectionText\":\"x\"}}", "unsupportedProtocolVersion")]
    [InlineData("{\"v\":1,\"type\":\"runTool\"}", "unknownType")]
    [InlineData("{\"v\":1,\"type\":\"page\",\"body\":{}}", "unknownType")]
    [InlineData("{\"v\":1,\"type\":\"selection\"}", "malformed")]
    [InlineData("{\"v\":1,\"type\":\"selection\",\"body\":{}}", "malformed")]
    [InlineData("{\"v\":1,\"type\":\"selection\",\"body\":{\"selectionText\":\"\"}}", "malformed")]
    [InlineData("{\"v\":1,\"type\":\"selection\",\"body\":{\"selectionText\":\"x\",\"pageUrl\":1}}", "malformed")]
    public void AMessageThatIsNotOneIsRefusedWithItsCode(string json, string expected)
    {
        Assert.False(BrowserMessageProtocol.TryDecode(Bytes(json), out var message, out var error));
        Assert.Null(message);
        Assert.Equal(expected, BrowserMessageProtocol.CodeName(error));
    }

    [Fact]
    public void TitleAndAddressAreOptional_UnknownFieldsAreIgnored_AndTheLimitsAreEnforced()
    {
        Assert.True(BrowserMessageProtocol.TryDecode(
            Bytes("{\"v\":1,\"type\":\"selection\",\"body\":{\"selectionText\":\"hi\",\"tabId\":4},\"later\":1}"), out var message, out _));
        Assert.Equal(new BrowserSelection("hi", false, "", ""), message!.Selection);

        var tooLong = BrowserMessageProtocol.EncodeSelection(new BrowserSelection(new string('a', BrowserSelection.MaxTextLength + 1), false, "", ""));
        Assert.False(BrowserMessageProtocol.TryDecode(tooLong, out _, out var error));
        Assert.Equal(BrowserMessageError.Malformed, error);
    }

    [Fact]
    public void AnUnpairedSurrogateWrittenAsAnEscapeIsMalformed_NotACrash()
    {
        var json = "{\"v\":1,\"type\":\"selection\",\"body\":{\"selectionText\":\"a" + (char)92 + "udc00b\"}}";

        Assert.False(BrowserMessageProtocol.TryDecode(Bytes(json), out _, out var error));
        Assert.Equal(BrowserMessageError.Malformed, error);
    }

    [Fact]
    public void TheHostReadsAsMuchAsTheAppPipeCarries()
    {
        Assert.Equal(InvocationProtocol.MaxFrameLength, BrowserMessageProtocol.MaxMessageLength);
        Assert.Equal(InvocationProtocol.Version, BrowserMessageProtocol.Version);
    }
}
