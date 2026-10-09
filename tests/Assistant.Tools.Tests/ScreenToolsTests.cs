using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Ocr;
using Assistant.Core.Tools;
using Assistant.Tools.Screen;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>The tool that reads a screenshot's words (PROJECT_SPEC §4.6, §4.8): when it is offered, what it returns and why it fails.</summary>
public sealed class ScreenToolsTests
{
    private static readonly OcrResult Dialog = new(
        [
            new OcrLine("Error 404", new OcrBox(40, 20, 120, 20), []),
            new OcrLine("The file was not found.", new OcrBox(40, 60, 300, 20), []),
        ],
        400,
        200,
        "en-US");

    internal sealed class FakeScreens : IScreenText
    {
        public bool Available { get; set; } = true;
        public bool Has { get; set; } = true;
        public OcrResult? Result { get; set; } = Dialog;
        public List<Guid> Asked { get; } = [];

        public bool IsAvailable => Available;

        public bool HasScreenshot(Guid conversationId) => Has;

        public Task<OcrResult?> ReadAsync(ContextItem screenshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<OcrResult?> ReadAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            Asked.Add(conversationId);
            return Task.FromResult(Result);
        }

        public void Forget(Guid itemId)
        {
        }
    }

    private static ToolCall Call(string arguments = "{}") => new("c1", ScreenToolResults.ReadScreenText, arguments);

    [Fact]
    public void TheToolIsAReadOnlyOne_WithOneOptionalArgument()
    {
        var definition = new ReadScreenTextTool(new FakeScreens()).Definition;

        Assert.Equal("read_screen_text", definition.Name);
        Assert.Equal(RiskLevel.ReadOnly, definition.RiskLevel);
        using var schema = JsonDocument.Parse(definition.InputSchemaJson);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("contains", out _));
        Assert.False(schema.RootElement.TryGetProperty("required", out _));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ItIsOfferedOnlyInAConversationThatHasAScreenshot_AndWhereTextCanBeRead(bool available, bool has, bool offered)
    {
        var tool = new ReadScreenTextTool(new FakeScreens { Available = available, Has = has });

        Assert.Equal(offered, tool.IsOffered(new ToolContext(Guid.NewGuid())));
    }

    [Fact]
    public void TheRegistry_OffersEachConversationOnlyTheToolsThatAreOfUseInIt()
    {
        var screens = new FakeScreens { Has = false };
        var registry = new ToolRegistry([new FakeToolForRegistry("other"), new ReadScreenTextTool(screens)]);
        var context = new ToolContext(Guid.NewGuid());

        Assert.Equal(["other", "read_screen_text"], registry.Tools.Select(tool => tool.Name));
        Assert.Equal(["other"], registry.ToolsFor(context).Select(tool => tool.Name));

        screens.Has = true;
        Assert.Equal(["other", "read_screen_text"], registry.ToolsFor(context).Select(tool => tool.Name));
    }

    [Fact]
    public async Task ARun_ReturnsTheLines_ForTheConversationItWasMadeIn()
    {
        var screens = new FakeScreens();
        var conversation = Guid.NewGuid();
        var executor = new ToolExecutor([new ReadScreenTextTool(screens)]);

        var result = await executor.ExecuteAsync(Call("""{"contains":"404"}"""), new ToolContext(conversation));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.Equal(1, json.RootElement.GetProperty("found").GetInt32());
        Assert.Equal("Error 404", json.RootElement.GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.Equal([conversation], screens.Asked);

        var all = await executor.ExecuteAsync(Call(), new ToolContext(conversation));
        using var everything = JsonDocument.Parse(all.OutputJson);
        Assert.Equal(2, everything.RootElement.GetProperty("found").GetInt32());
    }

    [Theory]
    [InlineData(false, true, true, "no screenshot attached")]
    [InlineData(true, false, true, "no OCR language")]
    [InlineData(true, true, false, "could not be read")]
    public async Task WhenTheTextCannotBeRead_TheResultSaysWhy_AndTellsTheModelToTellTheUser(bool has, bool available, bool read, string expected)
    {
        var screens = new FakeScreens { Has = has, Available = available, Result = read ? Dialog : null };

        var result = await new ToolExecutor([new ReadScreenTextTool(screens)]).ExecuteAsync(Call(), new ToolContext(Guid.NewGuid()));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(expected, result.OutputJson, StringComparison.Ordinal);
        Assert.Contains("Tell the user", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArgumentOfTheWrongKind_IsRefusedBeforeTheToolRuns()
    {
        var screens = new FakeScreens();

        var result = await new ToolExecutor([new ReadScreenTextTool(screens)]).ExecuteAsync(Call("""{"contains":5}"""), new ToolContext(Guid.NewGuid()));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Empty(screens.Asked);
    }

    private sealed class FakeToolForRegistry(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "does", """{"type":"object"}""", RiskLevel.ReadOnly);

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
    }
}
