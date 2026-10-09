using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Generation;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// Generating in the host: the request the engine gets, how its streamed answer is read, and the replies the owner
/// gets, with the fake engine answering over its real UNIX socket.
/// </summary>
public sealed class ModelGenerationTests
{
    // A stream as llama-server b11146 wrote it for a real model (the model's path shortened), with a reasoning piece and
    // the usage chunk that include_usage adds.
    private const string RecordedStream =
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null}}],\"created\":1790732813,\"id\":\"chatcmpl-y\",\"model\":\"C:/models/PRIVATE-PATH-0c5a.gguf\",\"system_fingerprint\":\"b11146-7fe450e19\",\"object\":\"chat.completion.chunk\"}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"reasoning_content\":\"Let me think\"}}],\"object\":\"chat.completion.chunk\"}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"content\":\"H\"}}],\"object\":\"chat.completion.chunk\"}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"content\":\"er \\u00e9\"}}],\"object\":\"chat.completion.chunk\"}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"length\",\"index\":0,\"delta\":{}}],\"object\":\"chat.completion.chunk\"}\n\n" +
        "data: {\"choices\":[],\"object\":\"chat.completion.chunk\",\"usage\":{\"completion_tokens\":3,\"prompt_tokens\":42,\"total_tokens\":45,\"prompt_tokens_details\":{\"cached_tokens\":11}},\"timings\":{\"cache_n\":11,\"prompt_n\":31}}\n\n" +
        "data: [DONE]\n\n";

    // ---- Reading the engine's stream ------------------------------------------------------------------------------

    [Fact]
    public async Task TheEnginesStream_IsReadAsItsText_ThenWhyItStopped()
    {
        var events = await ReadAsync(RecordedStream);

        Assert.Equal(
            [new ChatTextEvent("H"), new ChatTextEvent("er é"), new ChatFinishedEvent(GenerationStopReason.OutputLimit) { PromptTokens = 42, OutputTokens = 3 }],
            events);
    }

    [Fact]
    public async Task ToolCallPieces_AreJoinedAndGivenWhole_BeforeTheEnd()
    {
        var stream =
            Data("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"get_time","arguments":"{"}}]}}]}""") +
            Data("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}""") +
            Data("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":1}"}}]}}]}""") +
            Data("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"}"}}]}}]}""") +
            Data("""{"choices":[{"finish_reason":"tool_calls","index":0,"delta":{}}]}""") +
            "data: [DONE]\n\n";

        var events = await ReadAsync(stream);

        Assert.Equal(
            [
                new ChatToolCallEvent(new ToolCall("call_a", "read_file", "{\"path\":1}")),
                new ChatToolCallEvent(new ToolCall("call_b", "get_time", "{}")),
                new ChatFinishedEvent(GenerationStopReason.Completed),
            ],
            events);
    }

    [Theory]
    [InlineData("error: {\"code\":500,\"message\":\"PRIVATE-ANSWER-4d2e\",\"type\":\"server_error\"}\n\n", ModelHostErrorCode.GenerationFailed)]
    [InlineData("data: {\"error\":{\"code\":400,\"message\":\"too long\",\"type\":\"exceed_context_size_error\"}}\n\n", ModelHostErrorCode.ContextExceeded)]
    [InlineData("data: {not json\n\n", ModelHostErrorCode.GenerationFailed)]
    [InlineData("", ModelHostErrorCode.GenerationFailed)]
    public async Task AnErrorInTheStream_OrAStreamThatBreaksOff_FailsWithACode(string tail, ModelHostErrorCode expected)
    {
        var stream = Data("""{"choices":[{"index":0,"delta":{"content":"Hi"}}]}""") + tail;

        var failure = await Assert.ThrowsAsync<GenerationException>(() => ReadAsync(stream));

        Assert.Equal(expected, failure.Code);
        Assert.DoesNotContain("PRIVATE", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":{"code":400,"message":"request (15041 tokens) exceeds the available context size (2048 tokens)","type":"exceed_context_size_error"}}""", ModelHostErrorCode.ContextExceeded)]
    [InlineData("""{"error":{"code":500,"message":"x","type":"server_error"}}""", ModelHostErrorCode.GenerationFailed)]
    [InlineData("<html>Bad gateway</html>", ModelHostErrorCode.GenerationFailed)]
    public void ErrorBodies_MapByTheirType(string body, ModelHostErrorCode expected) =>
        Assert.Equal(expected, ChatCompletionStream.CodeOfError(body));

    // ---- The request the engine gets ------------------------------------------------------------------------------

    [Fact]
    public void TheRequest_CarriesTheInstructionsConversationToolsAndLimit_AndAsksForAStreamWithItsUsage()
    {
        using var document = JsonDocument.Parse(ChatCompletionRequest.Write(SampleMessages.GenerateText));
        var root = document.RootElement;

        var messages = root.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(SampleMessages.PrivateInstructions, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(SampleMessages.PrivatePrompt, messages[1].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        var call = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal("call-1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("read_file", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(SampleMessages.PrivateToolArguments, call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call-1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal(SampleMessages.PrivateAnswer, messages[3].GetProperty("content").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        // The engine serves the one model it loaded: the request names none.
        Assert.False(root.TryGetProperty("model", out _));
    }

    [Fact]
    public void AMultimodalRequest_GivesItsLastUserMessageTheImagesAsDataUris_ThenItsText()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02];
        var request = new GenerateMultimodalRequest(
            "vision",
            "",
            [
                new PromptMessage(MessageRole.User, "Earlier question"),
                new PromptMessage(MessageRole.Assistant, "Earlier answer"),
                new PromptMessage(MessageRole.User, SampleMessages.PrivatePrompt),
            ],
            [SampleMessages.PrivateImage, jpeg]);

        using var document = JsonDocument.Parse(ChatCompletionRequest.Write(request));

        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("Earlier question", messages[0].GetProperty("content").GetString());
        Assert.Equal("Earlier answer", messages[1].GetProperty("content").GetString());
        var parts = messages[2].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        Assert.Equal(
            [
                ("image_url", "data:image/png;base64," + Convert.ToBase64String(SampleMessages.PrivateImage)),
                ("image_url", "data:image/jpeg;base64," + Convert.ToBase64String(jpeg)),
                ("text", SampleMessages.PrivatePrompt),
            ],
            parts.EnumerateArray().Select(part => (
                part.GetProperty("type").GetString(),
                part.TryGetProperty("image_url", out var image)
                    ? image.GetProperty("url").GetString()
                    : part.GetProperty("text").GetString())));
    }

    [Fact]
    public void AMultimodalRequest_WithoutText_SendsTheImagesAlone()
    {
        var request = SampleMessages.GenerateMultimodal with { Messages = [new PromptMessage(MessageRole.User, "")] };

        using var document = JsonDocument.Parse(ChatCompletionRequest.Write(request));

        var part = Assert.Single(document.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray());
        Assert.Equal("image_url", part.GetProperty("type").GetString());
    }

    [Fact]
    public void TheRequest_LeavesOutWhatIsNotSet()
    {
        var request = new GenerateTextRequest("chat", "", [new PromptMessage(MessageRole.User, "Hi")]);

        using var document = JsonDocument.Parse(ChatCompletionRequest.Write(request));

        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.False(document.RootElement.TryGetProperty("tools", out _));
        Assert.False(document.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(document.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void TheRequest_CarriesTheTemperatureAsked()
    {
        var request = new GenerateTextRequest("chat", "", [new PromptMessage(MessageRole.User, "Hi")]) { Temperature = 0.3 };

        using var document = JsonDocument.Parse(ChatCompletionRequest.Write(request));

        Assert.Equal(0.3, document.RootElement.GetProperty("temperature").GetDouble());
        Assert.Contains("\"temperature\":0.3", System.Text.Encoding.UTF8.GetString(ModelHostSerializer.Serialize(1, request)), StringComparison.Ordinal);
    }

    // ---- The generator, with the fake engine ----------------------------------------------------------------------

    [Fact]
    public async Task Generating_StreamsTheEnginesText_ThenEndsWithTheTokenCounts()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = await Rig.LoadAsync(setup);
        var replies = new RecordingReplies();

        await rig.Generator.GenerateAsync(Request(rig), replies, TestPipes.Timeout());

        Assert.Equal(
            [
                new TextDelta("Hello"), new TextDelta(" there"), new TextDelta("!"),
                new GenerationEnded(GenerationStopReason.Completed) { PromptTokens = 21, OutputTokens = 3 },
            ],
            replies.Sent);

        // The engine got the prompt as a chat completion.
        using var sent = JsonDocument.Parse(File.ReadAllBytes(FakeChatReply.RequestPath(setup.ScenarioPath, 1)));
        Assert.Equal(SampleMessages.PrivatePrompt, sent.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ToolCallsAndAnAnswerCutShort_AreReplied()
    {
        var chat = new FakeChatReply
        {
            Pieces = ["Checking"],
            FinishReason = "length",
            ToolCalls = [new FakeToolCall("read_file", SampleMessages.PrivateToolArguments)],
        };
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { Chat = chat });
        await using var rig = await Rig.LoadAsync(setup);
        var replies = new RecordingReplies();

        await rig.Generator.GenerateAsync(Request(rig), replies, TestPipes.Timeout());

        Assert.Equal(
            [
                new TextDelta("Checking"),
                new ToolCallGenerated(new ToolCall("call_0", "read_file", SampleMessages.PrivateToolArguments)),
                new GenerationEnded(GenerationStopReason.OutputLimit) { PromptTokens = 21, OutputTokens = 1 },
            ],
            replies.Sent);
    }

    [Theory]
    [InlineData("context", 0, ModelHostErrorCode.ContextExceeded)]
    [InlineData("server", 0, ModelHostErrorCode.GenerationFailed)]
    [InlineData("stream", 3, ModelHostErrorCode.GenerationFailed)]
    public async Task EngineErrors_AreRepliedWithACode(string error, int textReplies, ModelHostErrorCode expected)
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { Chat = new FakeChatReply { Error = error } });
        await using var rig = await Rig.LoadAsync(setup);
        var replies = new RecordingReplies();

        await rig.Generator.GenerateAsync(Request(rig), replies, TestPipes.Timeout());

        Assert.Equal(textReplies, replies.Sent.OfType<TextDelta>().Count());
        Assert.Equal(new ModelHostError(expected), replies.Sent[^1]);
    }

    [Fact]
    public async Task AnEngineThatBreaksOff_OrCrashesMidAnswer_FailsTheGeneration()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { Chat = new FakeChatReply { BreakOff = true } });
        await using var rig = await Rig.LoadAsync(setup);
        var brokenOff = new RecordingReplies();
        await rig.Generator.GenerateAsync(Request(rig), brokenOff, TestPipes.Timeout());

        using var crashing = FakeEngineSetup.Create(new FakeEngineScenario { Chat = new FakeChatReply { CrashMidAnswer = true } });
        await using var crashRig = await Rig.LoadAsync(crashing);
        var crashed = new RecordingReplies();
        await crashRig.Generator.GenerateAsync(Request(crashRig), crashed, TestPipes.Timeout());

        Assert.Equal(new ModelHostError(ModelHostErrorCode.GenerationFailed), brokenOff.Sent[^1]);
        Assert.Equal([new TextDelta("Hello"), new ModelHostError(ModelHostErrorCode.GenerationFailed)], crashed.Sent);
    }

    [Fact]
    public async Task AModelThatIsNotLoaded_IsNotFound_AndOneWithoutAProjectorCannotReadImages()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = await Rig.LoadAsync(setup);
        var replies = new RecordingReplies();

        await rig.Generator.GenerateAsync(SampleMessages.GenerateText with { ModelId = "another" }, replies, TestPipes.Timeout());
        await rig.Generator.GenerateAsync(SampleMessages.GenerateMultimodal with { ModelId = "another" }, replies, TestPipes.Timeout());
        await rig.Generator.GenerateAsync(SampleMessages.GenerateMultimodal with { ModelId = rig.ModelId }, replies, TestPipes.Timeout());

        Assert.Equal(
            [
                new ModelHostError(ModelHostErrorCode.ModelNotFound),
                new ModelHostError(ModelHostErrorCode.ModelNotFound),
                new ModelHostError(ModelHostErrorCode.VisionNotSupported),
            ],
            replies.Sent);
        Assert.False(File.Exists(FakeChatReply.RequestPath(setup.ScenarioPath, 1)));
    }

    [Fact]
    public async Task AModelLoadedWithItsProjector_IsAskedWithTheImages_AndStreamsItsAnswer()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = await Rig.LoadAsync(setup, withProjector: true);
        var replies = new RecordingReplies();

        await rig.Generator.GenerateAsync(SampleMessages.GenerateMultimodal with { ModelId = rig.ModelId }, replies, TestPipes.Timeout());

        Assert.Equal(
            [
                new TextDelta("Hello"), new TextDelta(" there"), new TextDelta("!"),
                new GenerationEnded(GenerationStopReason.Completed) { PromptTokens = 21, OutputTokens = 3 },
            ],
            replies.Sent);
        using var sent = JsonDocument.Parse(File.ReadAllBytes(FakeChatReply.RequestPath(setup.ScenarioPath, 1)));
        var parts = sent.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(
            "data:image/png;base64," + Convert.ToBase64String(SampleMessages.PrivateImage),
            parts[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal(SampleMessages.PrivatePrompt, parts[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ASecondGenerationWhileOneRuns_IsBusy()
    {
        using var setup = FakeEngineSetup.Create(
            new FakeEngineScenario { Chat = new FakeChatReply { Pieces = ["a", "b", "c"], PieceDelayMs = 300 } });
        await using var rig = await Rig.LoadAsync(setup);
        var first = new RecordingReplies();
        var running = rig.Generator.GenerateAsync(Request(rig), first, TestPipes.Timeout());
        await first.WaitForAsync(1);

        var second = new RecordingReplies();
        await rig.Generator.GenerateAsync(Request(rig), second, TestPipes.Timeout());
        await running;

        Assert.Equal([new ModelHostError(ModelHostErrorCode.Busy)], second.Sent);
        Assert.IsType<GenerationEnded>(first.Sent[^1]);
    }

    [Fact]
    public async Task Stopping_EndsTheEnginesWorkAtOnce_AndTheNextGenerationRuns()
    {
        using var setup = FakeEngineSetup.Create(
            new FakeEngineScenario { Chat = new FakeChatReply { Pieces = [.. Enumerable.Repeat("word ", 100)], PieceDelayMs = 50 } });
        await using var rig = await Rig.LoadAsync(setup);
        using var stop = new CancellationTokenSource();
        var replies = new RecordingReplies();
        var running = rig.Generator.GenerateAsync(Request(rig), replies, stop.Token);
        await replies.WaitForAsync(2);

        var stoppedAt = DateTime.UtcNow;
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(DateTime.UtcNow - stoppedAt < TimeSpan.FromMilliseconds(500));
        await WaitUntilAsync(() => File.Exists(FakeChatReply.DisconnectedPath(setup.ScenarioPath, 1)));
        Assert.DoesNotContain(replies.Sent, reply => reply is GenerationEnded or ModelHostError);

        var next = new RecordingReplies();
        await rig.Generator.GenerateAsync(SampleMessages.GenerateText with { ModelId = rig.ModelId, MaxOutputTokens = 2 }, next, TestPipes.Timeout());
        Assert.IsType<GenerationEnded>(next.Sent[^1]);
    }

    [Fact]
    public async Task Generating_LogsNoPromptAnswerOrPath()
    {
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory();
        var chat = new FakeChatReply { Pieces = [SampleMessages.PrivateAnswer], ToolCalls = [new FakeToolCall("read_file", SampleMessages.PrivateToolArguments)] };
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { Chat = chat }, folderName: "PRIVATE-PATH-0c5a");
        await using var rig = await Rig.LoadAsync(setup, loggers);
        using var failing = FakeEngineSetup.Create(new FakeEngineScenario { Chat = chat with { Error = "server" } }, folderName: "PRIVATE-PATH-0c5a");
        await using var failingRig = await Rig.LoadAsync(failing, loggers);

        await rig.Generator.GenerateAsync(Request(rig), new RecordingReplies(), TestPipes.Timeout());
        await failingRig.Generator.GenerateAsync(Request(failingRig), new RecordingReplies(), TestPipes.Timeout());
        using var seeing = FakeEngineSetup.Create(new FakeEngineScenario { Chat = chat }, folderName: "PRIVATE-PATH-0c5a");
        await using var seeingRig = await Rig.LoadAsync(seeing, loggers, withProjector: true);
        await seeingRig.Generator.GenerateAsync(
            SampleMessages.GenerateMultimodal with { ModelId = seeingRig.ModelId }, new RecordingReplies(), TestPipes.Timeout());

        Assert.Contains("Generation ended", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("Generation failed", capture.AllText, StringComparison.Ordinal);
        foreach (var secret in SampleMessages.PrivateStrings.Append("PRIVATE-REASONING-51aa"))
        {
            Assert.DoesNotContain(secret, capture.AllText, StringComparison.Ordinal);
        }
    }

    private static GenerateTextRequest Request(Rig rig) => SampleMessages.GenerateText with { ModelId = rig.ModelId, MaxOutputTokens = null };

    private static string Data(string json) => $"data: {json}\n\n";

    private static async Task<List<ChatCompletionEvent>> ReadAsync(string stream)
    {
        var events = new List<ChatCompletionEvent>();
        await foreach (var step in ChatCompletionStream.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(stream)), TestPipes.Timeout()))
        {
            events.Add(step);
        }

        return events;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>A loaded model: the fake engine run by a manager, a controller, the engine's client and a generator.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private Rig(FakeEngineSetup setup, ILoggerFactory? loggers)
        {
            Manager = setup.CreateManager(loggers: loggers);
            Controller = new ModelController(
                Manager, TimeProvider.System, loggers?.CreateLogger<ModelController>() ?? NullLogger<ModelController>.Instance);
            Engine = new LlamaServerChatEngine(Manager);
            Generator = new TextGenerator(
                Controller, Engine, TimeProvider.System, loggers?.CreateLogger<TextGenerator>() ?? NullLogger<TextGenerator>.Instance);
        }

        public string ModelId => "chat";

        public ModelProcessManager Manager { get; }

        public ModelController Controller { get; }

        public LlamaServerChatEngine Engine { get; }

        public TextGenerator Generator { get; }

        public static async Task<Rig> LoadAsync(FakeEngineSetup setup, ILoggerFactory? loggers = null, bool withProjector = false)
        {
            var rig = new Rig(setup, loggers);
            var files = new ModelFiles(setup.ScenarioPath)
            {
                ProjectorPath = withProjector ? setup.CreateGguf("mmproj.gguf") : null,
            };
            await rig.Controller.LoadAsync(new LoadModelRequest(rig.ModelId) { Files = files }, TestPipes.Timeout());
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            Controller.Dispose();
            await Manager.DisposeAsync();
            Engine.Dispose();
        }
    }

    /// <summary>Keeps every reply sent, in order.</summary>
    private sealed class RecordingReplies : IModelHostReplies
    {
        private readonly ConcurrentQueue<ModelHostReply> _sent = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public IReadOnlyList<ModelHostReply> Sent => _sent.ToArray();

        public ValueTask SendAsync(ModelHostReply reply, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sent.Enqueue(reply);
            _arrived.Release();
            return ValueTask.CompletedTask;
        }

        public async Task WaitForAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (_sent.Count < count)
            {
                await _arrived.WaitAsync(timeout.Token);
            }
        }
    }
}
