using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.Tools;
using Assistant.Tools.Memory;
using Assistant.Tools.Screen;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// Tools that are kept out of the way when they are not what a request is about: a note is kept once for a request and never in place of what was
/// asked, a picture of the screen is not offered beside a picture the user gave, and a tool that had a question asked has the answer to itself.
/// </summary>
public sealed class ToolRestraintTests
{
    private static ToolCall Call(string name, string arguments) => new("c1", name, arguments);

    // ---- remember ----

    [Fact]
    public async Task ASecondNoteForTheSameRequestIsNotKept_AndTheModelIsToldToGetOnWithWhatWasAsked()
    {
        var memory = new InMemoryMemoryStore();
        var executor = new ToolExecutor([MemoryTools.Remember(memory, TimeProvider.System)], new FakeConfirmation(false), new FakePermissions(true));
        var context = new ToolContext(Guid.NewGuid(), "my sister is Lena and she likes tea, set a timer for five minutes");

        var first = await executor.ExecuteAsync(Call("remember", """{"note":"The user's sister is called Lena."}"""), context);
        var second = await executor.ExecuteAsync(Call("remember", """{"note":"The user's sister likes tea."}"""), context);

        Assert.Equal(ToolResultStatus.Succeeded, first.Status);
        Assert.Contains("Do not call remember again", first.OutputJson, StringComparison.Ordinal);
        Assert.Equal(ToolResultStatus.Failed, second.Status);
        Assert.True(ToolErrors.TryRead(second.OutputJson, out var code, out var message));
        Assert.Equal(ToolErrors.Repeated, code);
        Assert.Contains("do what the user asked now", message, StringComparison.Ordinal);
        Assert.Single(memory.Entries);

        // The next request may keep a note of its own, in the same conversation.
        var next = await executor.ExecuteAsync(Call("remember", """{"note":"The user's sister likes tea."}"""), context with { Request = "also remember she likes tea" });
        Assert.Equal(ToolResultStatus.Succeeded, next.Status);
        Assert.Equal(2, memory.Entries.Count);
    }

    [Theory]
    [InlineData("remember that I like my coffee black", true)]
    [InlineData("call me Mo from now on", true)]
    [InlineData("I prefer temperatures in Celsius", true)]
    [InlineData("what is the capital of France", false)] // It runs without a question, so it is offered only when something is to be kept.
    [InlineData("hi", false)]
    [InlineData("send a message to my brother saying hello", false)] // Who someone is to message is remember_person's.
    [InlineData("text Sami that I am late", false)]
    public void RememberIsOfferedOnlyWhenSomethingIsToBeKept_AndNeverWhileSomeoneIsBeingMessaged(string request, bool offered)
    {
        var tool = MemoryTools.Remember(new InMemoryMemoryStore(), TimeProvider.System);

        Assert.Equal(offered, tool.IsOffered(new ToolContext(Guid.NewGuid(), request)));
    }

    [Fact]
    public void RememberIsOfferedWhenWhatWasAskedIsNotKnown_AndItsWordsSayWhatItIsNotFor()
    {
        var tool = MemoryTools.Remember(new InMemoryMemoryStore(), TimeProvider.System);

        Assert.True(tool.IsOffered(new ToolContext(Guid.NewGuid())));
        Assert.Contains("never in place of what the user asked for", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("remember_person", tool.Definition.Description, StringComparison.Ordinal);
    }

    // ---- take_screenshot ----

    private sealed class NoScreenshots : IScreenshotTaker
    {
        public Task<ScreenshotOutcome> TakeAsync(Guid conversationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void APictureOfTheScreenIsNotOfferedForAQuestionThatCameWithAPicture()
    {
        var tool = new TakeScreenshotTool(new NoScreenshots());
        var conversation = Guid.NewGuid();

        // "What does this say?" with a picture pasted is about that picture.
        Assert.False(tool.IsOffered(new ToolContext(conversation, "what does this say?") { HasPicture = true }));

        // Without one, "this" may be what is on the screen, and the tool is there to be asked about.
        Assert.True(tool.IsOffered(new ToolContext(conversation, "what does this say?")));

        var registry = new ToolRegistry([tool]);
        Assert.Empty(registry.ToolsFor(new ToolContext(conversation, "what does this say?") { HasPicture = true }));
        Assert.Single(registry.ToolsFor(new ToolContext(conversation, "what does this say?")));
    }

    // ---- a tool that claims a request ----

    private sealed class Claiming(string name, Func<ToolContext, bool> claims, bool offered = true) : ITool
    {
        public ToolDefinition Definition { get; } = ToolDefinition.Create(name, "Does a thing.", [], RiskLevel.ReadOnly);

        public bool IsOffered(ToolContext context) => offered;

        public bool Claims(ToolContext context) => claims(context);

        public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
    }

    [Fact]
    public void ARequestThatAToolClaimsIsOfferedOnlyToTheToolsThatClaimIt()
    {
        var registry = new ToolRegistry(
        [
            new Claiming("answer_device", context => context.Request == "the bedroom one"),
            new Claiming("search_files", _ => false),
            new Claiming("claims_but_is_not_offered", _ => true, offered: false),
        ]);

        Assert.Equal(["answer_device"], registry.ToolsFor(new ToolContext(Guid.NewGuid(), "the bedroom one")).Select(tool => tool.Name));

        // A tool that claims a request it is not offered for takes nothing from the others.
        Assert.Equal(["answer_device", "search_files"], registry.ToolsFor(new ToolContext(Guid.NewGuid(), "find my notes")).Select(tool => tool.Name));
    }
}