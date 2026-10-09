using System.Text;
using Assistant.Core.ModelHosting;
using Xunit;

namespace Assistant.ModelHost.Tests;

public sealed class ModelHostSerializerTests
{
    [Fact]
    public void Envelope_HasVersionIdTypeAndBody()
    {
        var json = Encoding.UTF8.GetString(ModelHostSerializer.Serialize(7, new HealthRequest()));

        Assert.Equal("""{"v":1,"id":7,"type":"health","body":{}}""", json);
    }

    [Fact]
    public void Body_IsCamelCase_AndLeavesOutNulls()
    {
        var json = Encoding.UTF8.GetString(ModelHostSerializer.Serialize(3, new GenerationEnded(GenerationStopReason.Completed)
        {
            OutputTokens = 12,
        }));

        Assert.Equal("""{"v":1,"id":3,"type":"generationEnded","body":{"reason":"Completed","outputTokens":12}}""", json);
    }

    [Theory]
    [MemberData(nameof(SampleMessages.TypeNames), MemberType = typeof(SampleMessages))]
    public void EveryMessage_RoundTrips(string typeName)
    {
        var message = SampleMessages.Get(typeName);
        var bytes = ModelHostSerializer.Serialize(41, message);

        var frame = ModelHostSerializer.Deserialize(bytes);

        Assert.Null(frame.Error);
        Assert.Equal(41, frame.Id);
        Assert.IsType(message.GetType(), frame.Message);
        Assert.Equal(bytes, ModelHostSerializer.Serialize(41, frame.Message!));
    }

    [Fact]
    public void EveryMessageType_HasOneWireName()
    {
        var messageTypes = typeof(ModelHostMessage).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(ModelHostMessage)) && !type.IsAbstract)
            .ToHashSet();

        Assert.Equal(messageTypes, ModelHostSerializer.MessageNames.Keys.ToHashSet());
        Assert.Equal(messageTypes.Count, ModelHostSerializer.MessageNames.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(messageTypes, SampleMessages.All.Select(message => message.GetType()).ToHashSet());
    }

    [Fact]
    public void RequestsAndReplies_AreSeparate()
    {
        foreach (var type in ModelHostSerializer.MessageNames.Keys)
        {
            Assert.True(type.IsSubclassOf(typeof(ModelHostRequest)) ^ type.IsSubclassOf(typeof(ModelHostReply)), type.Name);
        }
    }

    [Fact]
    public void Images_CrossAsBase64_ByteForByte()
    {
        var bytes = ModelHostSerializer.Serialize(1, SampleMessages.GenerateMultimodal);
        var json = Encoding.UTF8.GetString(bytes);

        var request = Assert.IsType<GenerateMultimodalRequest>(ModelHostSerializer.Deserialize(bytes).Message);

        Assert.Contains(Convert.ToBase64String(SampleMessages.PrivateImage), json, StringComparison.Ordinal);
        Assert.Equal(SampleMessages.PrivateImage, Assert.Single(request.Images).ToArray());
        Assert.Equal(SampleMessages.PrivatePrompt, Assert.Single(request.Messages).Text);
    }

    [Fact]
    public void FieldsInAnyOrder_AndUnknownFields_AreRead()
    {
        var frame = Read("""{"extra":[1,2],"body":{"modelId":"chat-model","addedLater":true},"type":"loadModel","id":9,"v":1}""");

        Assert.Equal(9, frame.Id);
        Assert.Equal(new LoadModelRequest("chat-model"), frame.Message);
    }

    [Fact]
    public void OtherVersion_IsUnsupported_AndKeepsTheId()
    {
        var frame = Read("""{"v":2,"id":5,"type":"health","body":{}}""");

        Assert.Null(frame.Message);
        Assert.Equal(ModelHostErrorCode.UnsupportedProtocolVersion, frame.Error);
        Assert.Equal(5, frame.Id);
    }

    [Fact]
    public void UnknownType_IsReported_AndKeepsTheId()
    {
        var frame = Read("""{"v":1,"id":6,"type":"summarizeEverything","body":{}}""");

        Assert.Equal(ModelHostErrorCode.UnknownMessageType, frame.Error);
        Assert.Equal(6, frame.Id);
    }

    [Theory]
    [InlineData("""not json""", 0)]
    [InlineData("""[1,2,3]""", 0)]
    [InlineData("""{"v":1,"type":"health","body":{}}""", 0)]
    [InlineData("""{"v":1,"id":-3,"type":"health","body":{}}""", 0)]
    [InlineData("""{"v":1,"id":"7","type":"health","body":{}}""", 0)]
    [InlineData("""{"id":7,"type":"health","body":{}}""", 7)]
    [InlineData("""{"v":1,"id":7,"body":{}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"health"}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"health","body":null}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"health","body":{"unterminated":""", 0)]
    [InlineData("""{"v":1,"id":7,"type":"loadModel","body":{}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"loadModel","body":{"modelId":"  "}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"loadModel","body":{"modelId":42}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"loadModel","body":{"modelId":"m","files":{"modelPath":"models\\chat.gguf"}}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"loadModel","body":{"modelId":"m","files":{"modelPath":"C:\\models\\chat.gguf","projectorPath":"mmproj.gguf"}}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"loadModel","body":{"modelId":"m","files":{"modelPath":"C:\\models\\chat.gguf","contextLength":8}}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"loadModel","body":{"modelId":"m","files":{}}}""", 7)]
    [InlineData("""{"v":1,"id":0,"type":"modelStatus","body":{"sequence":0,"status":"Ready"}}""", 0)]
    [InlineData("""{"v":1,"id":0,"type":"modelStatus","body":{"sequence":1,"status":"Sleepy"}}""", 0)]
    [InlineData("""{"v":1,"id":7,"type":"cancelGeneration","body":{"requestId":0}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"generateText","body":{"modelId":"m","instructions":"","messages":[]}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"generateText","body":{"modelId":"m","instructions":"","messages":[{"role":"Wizard","text":""}]}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"generateText","body":{"modelId":"m","instructions":"","messages":[{"role":"User"}]}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"generateText","body":{"modelId":"m","instructions":"","messages":[{"role":"User","text":""}],"maxOutputTokens":0}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"generateText","body":{"modelId":"m","instructions":"","messages":[{"role":"User","text":""}],"temperature":2.5}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"generateText","body":{"modelId":"m","instructions":"","messages":[{"role":"User","text":""}],"temperature":-0.5}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"generateMultimodal","body":{"modelId":"m","instructions":"","messages":[{"role":"User","text":""}],"images":[]}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"generateMultimodal","body":{"modelId":"m","instructions":"","messages":[{"role":"User","text":""}],"images":["not base64!"]}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"error","body":{"code":"Gremlins"}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"error","body":{"code":99}}""", 7)]
    [InlineData("""{"v":1,"id":7,"type":"modelLoadProgress","body":{"modelId":"m","fraction":1.5}}""", 7)]
    public void MalformedFrames_AreReported_WithTheirIdWhenReadable(string json, long expectedId)
    {
        var frame = Read(json);

        Assert.Null(frame.Message);
        Assert.Equal(ModelHostErrorCode.MalformedMessage, frame.Error);
        Assert.Equal(expectedId, frame.Id);
    }

    [Fact]
    public void HealthReport_CarriesTheRuntimeStateByName_AndAnOlderHostsReportHasNone()
    {
        var json = Encoding.UTF8.GetString(ModelHostSerializer.Serialize(
            4, new HealthReport(new Version(1, 0), 10, TimeSpan.Zero) { Runtime = ModelRuntimeState.Incomplete }));
        var older = Read("""{"v":1,"id":4,"type":"healthReport","body":{"hostVersion":"1.0","processId":10,"uptime":"00:00:00"}}""");

        Assert.Contains("\"runtime\":\"Incomplete\"", json, StringComparison.Ordinal);
        Assert.Null(Assert.IsType<HealthReport>(older.Message).Runtime);
    }

    [Fact]
    public void MinimalGenerateText_UsesDefaults()
    {
        var frame = Read("""{"v":1,"id":2,"type":"generateText","body":{"modelId":"m","instructions":"","messages":[{"role":"User","text":"hi"}]}}""");

        var request = Assert.IsType<GenerateTextRequest>(frame.Message);
        Assert.Empty(request.Tools);
        Assert.Null(request.MaxOutputTokens);
        Assert.Empty(Assert.Single(request.Messages).ToolCalls);
    }

    [Fact]
    public void MessageOutsideTheProtocol_CannotBeWritten()
    {
        Assert.Throws<ArgumentException>(() => ModelHostSerializer.Serialize(1, new StrayRequest()));
    }

    [Fact]
    public void ToString_LeavesOutPrivateContent()
    {
        var text = string.Join(
            Environment.NewLine,
            SampleMessages.All.Select(message => message.ToString()).Concat(SampleMessages.Conversation.Select(turn => turn.ToString())));

        foreach (var secret in SampleMessages.PrivateStrings)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        Assert.Contains("Messages = 3", SampleMessages.GenerateText.ToString(), StringComparison.Ordinal);
        Assert.Contains("Images = 1", SampleMessages.GenerateMultimodal.ToString(), StringComparison.Ordinal);
    }

    private static ModelHostFrame Read(string json) => ModelHostSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    private sealed record StrayRequest : ModelHostRequest;
}
