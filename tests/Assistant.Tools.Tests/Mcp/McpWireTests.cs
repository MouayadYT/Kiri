using System.Text;
using System.Text.Json;
using Assistant.Tools.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

public sealed class JsonRpcTests
{
    private static IReadOnlyList<JsonRpcMessage> Parse(string json) => JsonRpcMessage.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ARequestHasAnIdAndAMethod()
    {
        var message = Assert.Single(Parse("""{"jsonrpc":"2.0","id":7,"method":"ping","params":{"a":1}}"""));
        Assert.Equal(JsonRpcKind.Request, message.Kind);
        Assert.Equal("ping", message.Method);
        Assert.Equal("7", message.IdKey);
        Assert.Equal(1, message.Params.GetProperty("a").GetInt32());
    }

    [Fact]
    public void ANotificationHasNoId()
    {
        var message = Assert.Single(Parse("""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}"""));
        Assert.Equal(JsonRpcKind.Notification, message.Kind);
        Assert.Null(message.IdKey);
    }

    [Fact]
    public void AResponseCarriesItsResult()
    {
        var message = Assert.Single(Parse("""{"jsonrpc":"2.0","id":"abc","result":{"ok":true}}"""));
        Assert.Equal(JsonRpcKind.Response, message.Kind);
        Assert.Equal("abc", message.IdKey);
        Assert.True(message.Result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void AnErrorCarriesItsCodeMessageAndData()
    {
        var message = Assert.Single(Parse("""{"jsonrpc":"2.0","id":3,"error":{"code":-32022,"message":"Unsupported","data":{"supported":["2026-07-28"]}}}"""));
        Assert.Equal(JsonRpcKind.Error, message.Kind);
        Assert.Equal(-32022, message.Error!.Code);
        Assert.Equal("Unsupported", message.Error.Message);
        Assert.Equal("2026-07-28", message.Error.Data!.Value.GetProperty("supported")[0].GetString());
    }

    [Fact]
    public void AnErrorMessageIsCutToALength()
    {
        var message = Assert.Single(Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"error\":{\"code\":1,\"message\":\"" + new string('m', 5000) + "\"}}"));
        Assert.Equal(JsonRpcMessage.MaxErrorMessageLength, message.Error!.Message!.Length);
    }

    [Fact]
    public void ANumberAndTheSameDigitsAsATextHaveTheSameKey()
    {
        var number = Assert.Single(Parse("""{"id":12,"result":{}}"""));
        var text = Assert.Single(Parse("""{"id":"12","result":{}}"""));
        Assert.Equal(number.IdKey, text.IdKey);
    }

    [Fact]
    public void ABatchIsReadMessageByMessage()
    {
        var messages = Parse("""[{"id":1,"result":{}},{"method":"notifications/x"},"junk",5]""");
        Assert.Equal([JsonRpcKind.Response, JsonRpcKind.Notification], messages.Select(message => message.Kind));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"half\":")]
    [InlineData("42")]
    [InlineData("{\"jsonrpc\":\"2.0\"}")]
    [InlineData("{\"id\":1}")]
    public void WhatIsNotAMessageIsNothing(string text) => Assert.Empty(Parse(text));

    [Fact]
    public void TheElementsOutliveTheTextTheyWereReadFrom()
    {
        var message = Assert.Single(Parse("""{"id":1,"result":{"deep":{"x":[1,2,3]}}}"""));
        GC.Collect();
        Assert.Equal(3, message.Result.GetProperty("deep").GetProperty("x").GetArrayLength());
    }

    [Fact]
    public void ARequestIsWrittenOnOneLine()
    {
        var request = JsonRpc.Request(5, "tools/list", new System.Text.Json.Nodes.JsonObject { ["cursor"] = "a\nb" });
        var text = Encoding.UTF8.GetString(request.Json);
        Assert.DoesNotContain('\n', text);
        Assert.Equal(5, request.Id);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("a\nb", document.RootElement.GetProperty("params").GetProperty("cursor").GetString());
    }

    [Fact]
    public void ANotificationHasNoIdWhenItIsWritten()
    {
        var notification = JsonRpc.Notification("notifications/initialized");
        Assert.Null(notification.Id);
        using var document = JsonDocument.Parse(notification.Json);
        Assert.False(document.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public void PingIsAnsweredAndNothingElseIsServed()
    {
        var ping = Assert.Single(Parse("""{"jsonrpc":"2.0","id":"p1","method":"ping"}"""));
        using var answer = JsonDocument.Parse(JsonRpc.AnswerServerRequest(ping)!);
        Assert.Equal("p1", answer.RootElement.GetProperty("id").GetString());
        Assert.True(answer.RootElement.TryGetProperty("result", out _));

        var sampling = Assert.Single(Parse("""{"jsonrpc":"2.0","id":9,"method":"sampling/createMessage"}"""));
        using var refused = JsonDocument.Parse(JsonRpc.AnswerServerRequest(sampling)!);
        Assert.Equal(9, refused.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(McpProtocol.MethodNotFound, refused.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        var notification = Assert.Single(Parse("""{"jsonrpc":"2.0","method":"notifications/message"}"""));
        Assert.Null(JsonRpc.AnswerServerRequest(notification));
    }

    [Fact]
    public void TheModernMetaNamesTheVersionTheClientAndNoCapabilities()
    {
        var meta = McpProtocol.ModernMeta();
        Assert.Equal("2026-07-28", (string?)meta["io.modelcontextprotocol/protocolVersion"]);
        Assert.Equal("Assistant", (string?)meta["io.modelcontextprotocol/clientInfo"]!["name"]);
        Assert.Empty(meta["io.modelcontextprotocol/clientCapabilities"]!.AsObject());
    }
}

public sealed class LineAndEventReaderTests
{
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task LinesAreReadWithoutTheirEnds()
    {
        var reader = new BoundedLineReader(Stream("one\ntwo\r\n\nthree"), 100);
        Assert.Equal("one", Encoding.UTF8.GetString((await reader.ReadLineAsync(default))!));
        Assert.Equal("two", Encoding.UTF8.GetString((await reader.ReadLineAsync(default))!));
        Assert.Empty((await reader.ReadLineAsync(default))!);
        Assert.Equal("three", Encoding.UTF8.GetString((await reader.ReadLineAsync(default))!));
        Assert.Null(await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task ALineLongerThanTheLimitEndsTheReading()
    {
        var reader = new BoundedLineReader(Stream(new string('a', 50_000) + "\nnext"), 1000);
        var exception = await Assert.ThrowsAsync<McpException>(async () => await reader.ReadLineAsync(default));
        Assert.Equal(McpFailure.TooLarge, exception.Failure);
    }

    [Fact]
    public async Task ALineThatNeverEndsIsNotHeldBeyondTheLimit()
    {
        var reader = new BoundedLineReader(new EndlessStream(), 64 * 1024);
        var exception = await Assert.ThrowsAsync<McpException>(async () => await reader.ReadLineAsync(default));
        Assert.Equal(McpFailure.TooLarge, exception.Failure);
    }

    [Fact]
    public async Task MultiByteCharactersSplitAcrossReadsAreIntact()
    {
        var reader = new BoundedLineReader(new OneByteAtATime(Encoding.UTF8.GetBytes("héllo 世界\n")), 100);
        Assert.Equal("héllo 世界", Encoding.UTF8.GetString((await reader.ReadLineAsync(default))!));
    }

    [Fact]
    public async Task EventsAreReadByTheirTypeAndData()
    {
        var stream = Stream(": comment\n\nevent: endpoint\ndata: /messages?s=1\n\ndata: {\"a\":\ndata: 1}\n\nid: 5\nretry: 10\n\n");
        var events = new List<SseEvent>();
        await foreach (var sse in SseReader.ReadAsync(stream, 1000, default))
        {
            events.Add(sse);
        }

        Assert.Equal([new SseEvent("endpoint", "/messages?s=1"), new SseEvent("message", "{\"a\":\n1}")], events);
    }

    [Fact]
    public async Task AnEventThatIsCutOffByTheEndOfTheStreamIsGivenAsFarAsItCame()
    {
        var events = new List<SseEvent>();
        await foreach (var sse in SseReader.ReadAsync(Stream("data: last"), 1000, default))
        {
            events.Add(sse);
        }

        Assert.Equal([new SseEvent("message", "last")], events);
    }

    [Fact]
    public async Task AByteOrderMarkAtTheStartIsIgnoredAndFieldsWithNoSpaceAreRead()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("data:no-space\r\n\r\n")).ToArray();
        var events = new List<SseEvent>();
        await foreach (var sse in SseReader.ReadAsync(new MemoryStream(bytes), 1000, default))
        {
            events.Add(sse);
        }

        Assert.Equal([new SseEvent("message", "no-space")], events);
    }

    [Fact]
    public async Task AnEventLargerThanTheLimitIsRefused()
    {
        var stream = Stream(string.Concat(Enumerable.Repeat("data: " + new string('x', 90) + "\n", 20)) + "\n");
        var exception = await Assert.ThrowsAsync<McpException>(async () =>
        {
            await foreach (var unused in SseReader.ReadAsync(stream, 500, default))
            {
            }
        });
        Assert.Equal(McpFailure.TooLarge, exception.Failure);
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'x');
            return ValueTask.FromResult(buffer.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OneByteAtATime(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= bytes.Length)
            {
                return 0;
            }

            buffer[offset] = bytes[_position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= bytes.Length)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = bytes[_position++];
            return ValueTask.FromResult(1);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class HeaderMirrorTests
{
    private static JsonElement Schema(string json) => Sample.Json(json);

    private static IReadOnlyList<McpHeaderMirror> Extract(string schema)
    {
        Assert.True(McpHeaderMirrors.TryExtract(Schema(schema), out var mirrors));
        return mirrors;
    }

    [Fact]
    public void AMarkedPropertyIsMirrored()
    {
        var mirror = Assert.Single(Extract("""{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"},"q":{"type":"string"}}}"""));
        Assert.Equal(["region"], mirror.Path);
        Assert.Equal("Region", mirror.HeaderName);
        Assert.Equal("string", mirror.Type);
    }

    [Fact]
    public void ANestedPropertyReachedThroughPropertiesAloneIsMirrored()
    {
        var mirror = Assert.Single(Extract("""{"type":"object","properties":{"scope":{"type":"object","properties":{"tenant":{"type":"integer","x-mcp-header":"Tenant"}}}}}"""));
        Assert.Equal(["scope", "tenant"], mirror.Path);
    }

    [Fact]
    public void ASchemaWithNoMarksHasNoMirrors() => Assert.Empty(Extract("""{"type":"object","properties":{"q":{"type":"string"}}}"""));

    [Theory]
    [InlineData("""{"type":"object","properties":{"a":{"type":"number","x-mcp-header":"A"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"array","x-mcp-header":"A"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":""}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":"Bad Name"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":"Bad:Name"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":5}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":"X"},"b":{"type":"string","x-mcp-header":"x"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"array","items":{"type":"string","x-mcp-header":"X"}}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"anyOf":[{"type":"string","x-mcp-header":"X"}]}}}""")]
    [InlineData("""{"type":"object","x-mcp-header":"Root"}""")]
    [InlineData("""{"type":"object","$defs":{"d":{"type":"string","x-mcp-header":"X"}},"properties":{"a":{"$ref":"#/$defs/d"}}}""")]
    public void MarksThatBreakTheRulesRejectTheTool(string schema)
    {
        Assert.False(McpHeaderMirrors.TryExtract(Schema(schema), out var mirrors));
        Assert.Empty(mirrors);
    }

    [Fact]
    public void ValuesAreTakenFromTheArgumentsByTheirPlaceAndWrittenAsText()
    {
        var mirrors = Extract("""{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"},"limit":{"type":"integer","x-mcp-header":"Limit"},"deep":{"type":"boolean","x-mcp-header":"Deep"},"missing":{"type":"string","x-mcp-header":"Missing"}}}""");
        var headers = McpHeaderMirrors.HeadersFor(mirrors, Schema("""{"region":"us-west1","limit":42,"deep":false}"""));

        Assert.Equal(
            [("Mcp-Param-Region", "us-west1"), ("Mcp-Param-Limit", "42"), ("Mcp-Param-Deep", "false")],
            headers.Select(header => (header.Key, header.Value)));
    }

    [Fact]
    public void AValueOfAnotherTypeThanTheMarkSaysIsNotSent()
    {
        var mirrors = Extract("""{"type":"object","properties":{"limit":{"type":"integer","x-mcp-header":"Limit"}}}""");
        Assert.Empty(McpHeaderMirrors.HeadersFor(mirrors, Schema("""{"limit":"forty"}""")));
        Assert.Empty(McpHeaderMirrors.HeadersFor(mirrors, Schema("""{"limit":1.5}""")));
        Assert.Empty(McpHeaderMirrors.HeadersFor(mirrors, Schema("""{"limit":null}""")));
        Assert.Empty(McpHeaderMirrors.HeadersFor(mirrors, Schema("""{"limit":9007199254740993}""")));
    }

    [Theory]
    [InlineData("plain value", "plain value")]
    [InlineData("us-west1", "us-west1")]
    [InlineData("", "")]
    [InlineData(" padded ", "=?base64?IHBhZGRlZCA=?=")]
    [InlineData("line1\nline2", "=?base64?bGluZTEKbGluZTI=?=")]
    [InlineData("=?base64?literal?=", "=?base64?PT9iYXNlNjQ/bGl0ZXJhbD89?=")]
    public void ValuesThatAreNotPlainPrintableAsciiAreBase64Encoded(string value, string expected) => Assert.Equal(expected, McpHeaderMirrors.Encode(value));

    [Fact]
    public void NonAsciiTextIsBase64Encoded()
    {
        var encoded = McpHeaderMirrors.Encode("Hello, 世界");
        Assert.Equal("=?base64?SGVsbG8sIOS4lueVjA==?=", encoded);
    }
}
