using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Ipc;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// The app pipe's protocol (PROJECT_SPEC §5.7): its name, its envelopes, and the checks the app makes on what it is sent.
/// </summary>
public sealed class InvocationProtocolTests
{
    [Fact]
    public void TheRequestToShowTheFullWindowRoundTrips_AndHoldsNothingOfTheUsers()
    {
        var payload = InvocationProtocol.EncodeRequest(InvocationRequest.ShowFullView);

        Assert.Equal("""{"v":1,"type":"showFullView"}""", System.Text.Encoding.UTF8.GetString(payload));
        Assert.True(InvocationProtocol.TryDecodeRequest(payload, out var request, out _));
        Assert.Equal(InvocationAction.ShowFullView, request!.Action);
        Assert.Empty(request.Paths);
        Assert.Null(request.Selection);
    }

    [Fact]
    public void ARequestAndItsRepliesRoundTrip_WithPathsKeptExactly()
    {
        string[] paths = [@"C:\Users\Ana\Documents\Plan (final).docx", @"\\server\share\Café ünïcode — notes.md", "C:\\a\"b"];
        var payload = InvocationProtocol.EncodeRequest(new InvocationRequest(InvocationAction.AskAboutFiles, paths));

        Assert.True(InvocationProtocol.TryDecodeRequest(payload, out var request, out _));
        Assert.Equal(InvocationAction.AskAboutFiles, request!.Action);
        Assert.Equal(paths, request.Paths);

        // The envelope is the documented one, in plain ASCII.
        var json = Encoding.UTF8.GetString(payload);
        Assert.StartsWith("{\"v\":1,\"type\":\"askAboutFiles\",\"body\":{\"paths\":[", json, StringComparison.Ordinal);
        Assert.All(payload, b => Assert.True(b < 0x80));

        Assert.True(InvocationProtocol.DecodeReply(InvocationProtocol.EncodeReply(InvocationReply.Accepted)).IsAccepted);
        foreach (var code in Enum.GetValues<InvocationErrorCode>())
        {
            Assert.Equal(code, InvocationProtocol.DecodeReply(InvocationProtocol.EncodeReply(new InvocationReply(code))).Error);
        }

        Assert.Equal("{\"v\":1,\"type\":\"accepted\"}", Encoding.UTF8.GetString(InvocationProtocol.EncodeReply(InvocationReply.Accepted)));
    }

    [Theory]
    [InlineData("", InvocationErrorCode.Malformed)]
    [InlineData("not json", InvocationErrorCode.Malformed)]
    [InlineData("[1,2]", InvocationErrorCode.Malformed)]
    [InlineData("{\"type\":\"askAboutFiles\",\"body\":{\"paths\":[\"C:\\\\a.txt\"]}}", InvocationErrorCode.Malformed)]
    [InlineData("{\"v\":\"1\",\"type\":\"askAboutFiles\",\"body\":{\"paths\":[\"C:\\\\a.txt\"]}}", InvocationErrorCode.Malformed)]
    [InlineData("{\"v\":2,\"type\":\"askAboutFiles\",\"body\":{\"paths\":[\"C:\\\\a.txt\"]}}", InvocationErrorCode.UnsupportedProtocolVersion)]
    [InlineData("{\"v\":1.5,\"type\":\"askAboutFiles\",\"body\":{\"paths\":[\"C:\\\\a.txt\"]}}", InvocationErrorCode.UnsupportedProtocolVersion)]
    [InlineData("{\"v\":1,\"type\":\"runTool\",\"body\":{\"paths\":[\"C:\\\\a.txt\"]}}", InvocationErrorCode.UnknownAction)]
    [InlineData("{\"v\":1,\"type\":\"readHistory\"}", InvocationErrorCode.UnknownAction)]
    [InlineData("{\"v\":1,\"type\":\"askAboutFiles\"}", InvocationErrorCode.Malformed)]
    [InlineData("{\"v\":1,\"type\":\"askAboutFiles\",\"body\":{\"paths\":\"C:\\\\a.txt\"}}", InvocationErrorCode.Malformed)]
    [InlineData("{\"v\":1,\"type\":\"askAboutFiles\",\"body\":{\"paths\":[]}}", InvocationErrorCode.Malformed)]
    [InlineData("{\"v\":1,\"type\":\"askAboutFiles\",\"body\":{\"paths\":[\"\"]}}", InvocationErrorCode.Malformed)]
    [InlineData("{\"v\":1,\"type\":\"askAboutFiles\",\"body\":{\"paths\":[7]}}", InvocationErrorCode.Malformed)]
    public void ARequestThatIsNotOneIsRefusedWithItsCode(string json, InvocationErrorCode expected)
    {
        Assert.False(InvocationProtocol.TryDecodeRequest(Encoding.UTF8.GetBytes(json), out var request, out var error));
        Assert.Null(request);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void UnknownFieldsAreIgnored_AndAnOverlongPathIsRefused()
    {
        var extra = "{\"v\":1,\"type\":\"askAboutFiles\",\"from\":\"explorer\",\"body\":{\"paths\":[\"C:\\\\a.txt\"],\"mode\":3}}";
        Assert.True(InvocationProtocol.TryDecodeRequest(Encoding.UTF8.GetBytes(extra), out var request, out _));
        Assert.Equal([@"C:\a.txt"], request!.Paths);

        var longest = "C:\\" + new string('a', InvocationProtocol.MaxPathLength - 3);
        var tooLong = longest + "a";
        Assert.True(InvocationProtocol.TryDecodeRequest(
            InvocationProtocol.EncodeRequest(new(InvocationAction.AskAboutFiles, [longest])), out _, out _));
        Assert.False(InvocationProtocol.TryDecodeRequest(
            InvocationProtocol.EncodeRequest(new(InvocationAction.AskAboutFiles, [tooLong])), out _, out var error));
        Assert.Equal(InvocationErrorCode.Malformed, error);

    }

    [Fact]
    public void ASelectionOfUpToMaxPathsIsOneRequest_AndOneMoreIsTooMany()
    {
        // A whole selection is sent as it is, so that the app can say how many were left out: more than the ten it uses.
        var selection = Enumerable.Range(1, InvocationProtocol.MaxPaths).Select(i => $@"C:\Pictures\Screenshot {i}.png").ToArray();
        Assert.True(InvocationProtocol.MaxPaths > InvocationProtocol.MaxFiles);
        Assert.True(InvocationProtocol.TryDecodeRequest(
            InvocationProtocol.EncodeRequest(new(InvocationAction.AskAboutFiles, selection)), out var request, out _));
        Assert.Equal(selection, request!.Paths);

        Assert.False(InvocationProtocol.TryDecodeRequest(
            InvocationProtocol.EncodeRequest(new(InvocationAction.AskAboutFiles, [.. selection, @"C:\one more.png"])), out _, out var error));
        Assert.Equal(InvocationErrorCode.TooManyFiles, error);

        // Real paths are far below the frame limit.
        Assert.True(InvocationProtocol.EncodeRequest(new(InvocationAction.AskAboutFiles, selection)).Length < InvocationProtocol.MaxFrameLength / 10);
    }

    [Theory]
    [InlineData("{\"v\":1,\"type\":\"error\",\"body\":{\"code\":\"somethingNew\"}}")]
    [InlineData("{\"v\":2,\"type\":\"accepted\"}")]
    [InlineData("{\"v\":1,\"type\":\"maybe\"}")]
    [InlineData("{\"v\":1,\"type\":\"error\"}")]
    [InlineData("nope")]
    public void AReplyThatIsNotOneIsAProtocolError(string json) =>
        Assert.Throws<IpcProtocolException>(() => InvocationProtocol.DecodeReply(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ThePipeNameIsPerUserAndSession_HidesTheName_AndIsAValidPipeName()
    {
        var name = AppPipe.NameFor(@"PC\ana", 1);
        Assert.True(LocalPipe.IsValidName(name));
        Assert.StartsWith(AppPipe.Prefix + ".", name, StringComparison.Ordinal);
        Assert.DoesNotContain("ana", name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(name, AppPipe.NameFor(@"pc\ANA", 1));
        Assert.NotEqual(name, AppPipe.NameFor(@"PC\bo", 1));
        Assert.NotEqual(name, AppPipe.NameFor(@"PC\ana", 2));
        Assert.Equal(AppPipe.ForCurrentUser(), AppPipe.ForCurrentUser());
        Assert.True(LocalPipe.IsValidName(AppPipe.ForCurrentUser()));
    }

    [Fact]
    public void ExplorerOffersEveryDocumentAndPictureType_AndNothingElse()
    {
        Assert.Equal(
            DocumentFileTypes.Extensions.Concat(ImageFileTypes.Extensions).Order(StringComparer.Ordinal),
            ExplorerFileTypes.Extensions.Order(StringComparer.Ordinal));
        Assert.All(ExplorerFileTypes.Extensions, extension => Assert.Equal(extension.ToLowerInvariant(), extension));
        Assert.True(ExplorerFileTypes.IsSupported(@"C:\a\Report.PDF"));
        Assert.True(ExplorerFileTypes.IsSupported(@"C:\a\photo.heic"));
        Assert.False(ExplorerFileTypes.IsSupported(@"C:\a\setup.exe"));
        Assert.False(ExplorerFileTypes.IsSupported(@"C:\a\folder"));
        Assert.False(ExplorerFileTypes.IsSupported(""));
    }
}
