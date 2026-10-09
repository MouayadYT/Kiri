using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>What the executor does with a call the model made: validation, the risk level, the timeout, and what goes wrong.</summary>
public sealed class ToolExecutorTests
{
    private const string Schema =
        """{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer"},"mode":{"type":"string","enum":["a","b"]}},"required":["query"]}""";

    private static ToolCall Call(string name, string arguments) => new("c1", name, arguments);

    [Fact]
    public async Task AValidCallRuns_WithItsArguments_AndTheConversationItWasMadeIn()
    {
        var conversation = Guid.NewGuid();
        string? seen = null;
        var tool = new FakeTool("find", schema: Schema)
        {
            Run = (arguments, context, _) =>
            {
                seen = arguments.GetProperty("query").GetString() + "@" + context.ConversationId;
                return Task.FromResult("""{"ok":true}""");
            },
        };

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("find", """{"query":"milestone","limit":3,"extra":1}"""), new ToolContext(conversation));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal("""{"ok":true}""", result.OutputJson);
        Assert.Equal("c1", result.ToolCallId);
        Assert.Equal("milestone@" + conversation, seen);
    }

    [Theory]
    [InlineData("""{}""", "is required")]
    [InlineData("""{"query":null}""", "is required")]
    [InlineData("""{"query":5}""", "must be text")]
    [InlineData("""{"query":"a","limit":"x"}""", "must be a whole number")]
    [InlineData("""{"query":"a","limit":1.5}""", "must be a whole number")]
    [InlineData("""{"query":"a","mode":"c"}""", "must be one of: a, b")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[1]", "must be a JSON object")]
    public async Task ArgumentsThatBreakTheSchema_AreAFailedResultThatSaysWhy_AndTheToolDoesNotRun(string arguments, string expected)
    {
        var tool = new FakeTool("find", schema: Schema);

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("find", arguments));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(expected, result.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task EmptyArguments_AreAnEmptyObject()
    {
        var tool = new FakeTool("now");

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("now", ""));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task AToolThatIsNotRegistered_IsAFailedResult_NotAnException()
    {
        var result = await new ToolExecutor([new FakeTool("find")]).ExecuteAsync(Call("delete_everything", "{}"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal("delete_everything", result.ToolName);
        Assert.Contains("not a tool you have", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASideEffectToolRunsOnlyWhenTheUserConfirms_AndADestructiveOneNever()
    {
        var sideEffect = new FakeTool("open", RiskLevel.SideEffect);
        var destructive = new FakeTool("wipe", RiskLevel.Destructive);

        var declined = new FakeConfirmation(false);
        Assert.Equal(ToolResultStatus.Declined, (await new ToolExecutor([sideEffect], declined).ExecuteAsync(Call("open", "{}"))).Status);
        Assert.Equal(0, sideEffect.Runs);
        Assert.Equal(1, declined.Asked);

        Assert.Equal(ToolResultStatus.Declined, (await new ToolExecutor([sideEffect]).ExecuteAsync(Call("open", "{}"))).Status);

        var approved = new FakeConfirmation(true);
        Assert.Equal(ToolResultStatus.Succeeded, (await new ToolExecutor([sideEffect], approved).ExecuteAsync(Call("open", "{}"))).Status);
        Assert.Equal(1, sideEffect.Runs);

        Assert.Equal(ToolResultStatus.Failed, (await new ToolExecutor([destructive], approved).ExecuteAsync(Call("wipe", "{}"))).Status);
        Assert.Equal(0, destructive.Runs);
    }

    [Fact]
    public async Task AReadOnlyToolNeverAsksTheUser()
    {
        var confirmation = new FakeConfirmation(false);

        var result = await new ToolExecutor([new FakeTool("find")], confirmation).ExecuteAsync(Call("find", "{}"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(0, confirmation.Asked);
    }

    [Fact]
    public async Task AToolThatRunsTooLong_IsGivenUp_AndOneThatThrows_Fails()
    {
        var slow = new FakeTool("slow")
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            Run = async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return "{}";
            },
        };
        var broken = new FakeTool("broken") { Run = (_, _, _) => throw new InvalidOperationException("secret path C:\\private") };
        var executor = new ToolExecutor([slow, broken]);

        var late = await executor.ExecuteAsync(Call("slow", "{}"));
        var thrown = await executor.ExecuteAsync(Call("broken", "{}"));

        Assert.Equal(ToolResultStatus.Failed, late.Status);
        Assert.Contains("too long", late.OutputJson, StringComparison.Ordinal);
        Assert.Equal(ToolResultStatus.Failed, thrown.Status);
        Assert.DoesNotContain("private", thrown.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheCall_IsNotAFailedResult_ItStopsTheCaller()
    {
        var tool = new FakeTool("slow")
        {
            Run = async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return "{}";
            },
        };
        using var stop = new CancellationTokenSource();
        var running = new ToolExecutor([tool]).ExecuteAsync(Call("slow", "{}"), stop.Token);
        await Task.Delay(50);

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public void TheRegistryListsTheToolsInOrder_AndRefusesTwoOfOneNameAndAnyThatCanDestroyData()
    {
        var registry = new ToolRegistry([new FakeTool("b"), new FakeTool("a")]);

        Assert.Equal(["b", "a"], registry.Tools.Select(tool => tool.Name));
        Assert.Equal("a", registry.Find("a")!.Name);
        Assert.Null(registry.Find("c"));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("a"), new FakeTool("a")]));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("wipe", RiskLevel.Destructive)]));
    }
}
