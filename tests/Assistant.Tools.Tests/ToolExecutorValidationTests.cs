using System.Diagnostics;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// What the executor does with the arguments a model wrote (PROJECT_SPEC §4.8, step 102): unknown arguments, types, ranges and lengths,
/// the words and the code a failure comes back with, a time limit that holds even for a tool that ignores it, and a result that is always
/// of the call that was made.
/// </summary>
public sealed class ToolExecutorValidationTests
{
    private static ToolCall Call(string name, string arguments, string id = "c1") => new(id, name, arguments);

    private static HandlerTool Typed(
        string name, RiskLevel risk, IEnumerable<ToolParameter> parameters, Action? ran = null) =>
        new(
            ToolDefinition.Create(name, "A tool.", parameters, risk),
            (call, _, _, _) =>
            {
                ran?.Invoke();
                return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, """{"ok":true}"""));
            });

    private static ToolParameter Percent => new("percent", ToolParameterType.Integer, "The volume.", Minimum: 0, Maximum: 100);

    private static (string Code, string Message) Failure(ToolResult result)
    {
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out var message), result.OutputJson);
        return (code, message);
    }

    // ---- Arguments the tool does not name ----

    [Fact]
    public async Task AnArgumentATypedToolDoesNotTake_IsRefused_AndTheToolDoesNotRun()
    {
        var runs = 0;
        var executor = new ToolExecutor([Typed("get_thing", RiskLevel.ReadOnly, [new ToolParameter("which", ToolParameterType.String, "Which.")], () => runs++)]);

        var result = await executor.ExecuteAsync(Call("get_thing", """{"which":"a","limit":5}"""));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        var (code, message) = Failure(result);
        Assert.Equal(ToolErrors.InvalidArguments, code);
        Assert.Contains("\"limit\" is not one this tool takes. It takes: which.", message, StringComparison.Ordinal);
        Assert.Contains("get_thing(which: text)", result.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task AToolWithNoArguments_RefusesAnyArgument()
    {
        var executor = new ToolExecutor([Typed("mute", RiskLevel.ReadOnly, [])]);

        var result = await executor.ExecuteAsync(Call("mute", """{"now":true}"""));

        Assert.Contains("It takes no arguments.", Failure(result).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASideEffectToolRefusesAnUnknownArgument_EvenWhenItsSchemaIsWrittenByHand()
    {
        // What the user is shown to confirm is the call's arguments: one that is shown and not used would mislead.
        var tool = new FakeTool("open", RiskLevel.SideEffect, """{"type":"object","properties":{"file":{"type":"string"}},"required":["file"]}""");
        var confirmation = new FakeConfirmation(true);

        var result = await new ToolExecutor([tool], confirmation).ExecuteAsync(Call("open", """{"file":"f1","extra":1}"""));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("\"extra\" is not one this tool takes", Failure(result).Message, StringComparison.Ordinal);
        Assert.Equal(0, confirmation.Asked);
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task AReadOnlyToolWithAHandWrittenSchemaThatDoesNotForbidThem_StillIgnoresUnknownArguments()
    {
        var tool = new FakeTool("find", schema: """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""");

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("find", """{"query":"a","limit":5}"""));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task AHandWrittenSchemaThatSaysAdditionalPropertiesFalse_IsHeldToIt()
    {
        var tool = new FakeTool("find", schema: """{"type":"object","properties":{"query":{"type":"string"}},"additionalProperties":false}""");

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("find", """{"query":"a","limit":5}"""));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(0, tool.Runs);
    }

    // ---- Types, ranges, lengths ----

    [Theory]
    [InlineData("""{"percent":50}""", true)]
    [InlineData("""{"percent":0}""", true)]
    [InlineData("""{"percent":100}""", true)]
    [InlineData("""{"percent":101}""", false)]
    [InlineData("""{"percent":-1}""", false)]
    [InlineData("""{"percent":1e9}""", false)]
    [InlineData("""{"percent":50.5}""", false)]
    [InlineData("""{"percent":"50"}""", false)]
    [InlineData("""{"percent":true}""", false)]
    [InlineData("""{"percent":[50]}""", false)]
    [InlineData("""{"percent":{"a":1}}""", false)]
    [InlineData("""{"percent":null}""", false)]
    [InlineData("""{}""", false)]
    public async Task ARangeIsHeldTo_AndATypeIsNotCoerced(string arguments, bool runs)
    {
        var count = 0;
        var executor = new ToolExecutor([Typed("set_volume", RiskLevel.ReadOnly, [Percent], () => count++)]);

        var result = await executor.ExecuteAsync(Call("set_volume", arguments));

        Assert.Equal(runs ? ToolResultStatus.Succeeded : ToolResultStatus.Failed, result.Status);
        Assert.Equal(runs ? 1 : 0, count);
        if (!runs)
        {
            Assert.Equal(ToolErrors.InvalidArguments, Failure(result).Code);
        }
    }

    [Fact]
    public async Task TheWordsSayWhatRangeAnArgumentMustBeIn()
    {
        var executor = new ToolExecutor([Typed("set_volume", RiskLevel.ReadOnly, [Percent])]);

        var tooBig = Failure(await executor.ExecuteAsync(Call("set_volume", """{"percent":150}""")));

        Assert.Equal("The argument \"percent\" must be from 0 to 100.", tooBig.Message);
    }

    [Fact]
    public async Task TextIsBoundedByItsLength_OrTheUsualLimit()
    {
        var short5 = new ToolParameter("name", ToolParameterType.String, "A name.", MaxLength: 5);
        var any = new ToolParameter("text", ToolParameterType.String, "Text.", Required: false);
        var executor = new ToolExecutor([Typed("tell", RiskLevel.ReadOnly, [short5, any])]);

        Assert.Equal(ToolResultStatus.Succeeded, (await executor.ExecuteAsync(Call("tell", """{"name":"abcde"}"""))).Status);
        var long5 = await executor.ExecuteAsync(Call("tell", """{"name":"abcdef"}"""));
        Assert.Contains("too long: at most 5 characters", Failure(long5).Message, StringComparison.Ordinal);

        var usual = "{\"name\":\"a\",\"text\":\"" + new string('x', 4_001) + "\"}";
        Assert.Contains("too long: at most 4000 characters", Failure(await executor.ExecuteAsync(Call("tell", usual))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryProblemIsToldAtOnce_NotJustTheFirst()
    {
        var executor = new ToolExecutor(
        [
            Typed("tool_x", RiskLevel.ReadOnly,
            [
                new ToolParameter("a", ToolParameterType.String, "A."),
                new ToolParameter("b", ToolParameterType.Integer, "B.", Maximum: 10),
            ]),
        ]);

        var (_, message) = Failure(await executor.ExecuteAsync(Call("tool_x", """{"b":11,"c":1}""")));

        Assert.Contains("\"a\" is required", message, StringComparison.Ordinal);
        Assert.Contains("\"b\" must be at most 10", message, StringComparison.Ordinal);
        Assert.Contains("\"c\" is not one this tool takes", message, StringComparison.Ordinal);
    }

    // ---- What the model wrote, made tidy ----

    [Theory]
    [InlineData("\"{\\\"percent\\\":50}\"")]
    [InlineData("```json\n{\"percent\": 50}\n```")]
    public async Task ArgumentsInAnotherWrapping_AreReadAsTheObjectTheyHold(string arguments)
    {
        string? seen = null;
        var tool = new HandlerTool(
            ToolDefinition.Create("set_volume", "Sets.", [Percent], RiskLevel.ReadOnly),
            (call, args, _, _) =>
            {
                seen = args.GetProperty("percent").GetInt32() + "|" + call.ArgumentsJson;
                return Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}"));
            });

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("set_volume", arguments));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal("""50|{"percent":50}""", seen);
    }

    [Fact]
    public async Task TheUserIsShownTheTidyArguments_WhenAskedToConfirm()
    {
        var shown = new List<string>();
        var confirmation = new RecordingConfirmation(shown);
        var tool = Typed("set_volume", RiskLevel.SideEffect, [Percent]);

        await new ToolExecutor([tool], confirmation).ExecuteAsync(Call("set_volume", "```json\n{ \"percent\" : 30 }\n```"));

        Assert.Equal(["""{"percent":30}"""], shown);
    }

    [Fact]
    public async Task ADuplicatedArgument_IsRefusedBeforeAnythingIsAsked()
    {
        var confirmation = new FakeConfirmation(true);
        var tool = Typed("set_volume", RiskLevel.SideEffect, [Percent]);

        var result = await new ToolExecutor([tool], confirmation).ExecuteAsync(Call("set_volume", """{"percent":10,"percent":90}"""));

        Assert.Contains("given more than once", Failure(result).Message, StringComparison.Ordinal);
        Assert.Equal(0, confirmation.Asked);
    }

    [Fact]
    public async Task APermissionThatIsOff_IsToldBeforeTheArgumentsAreLookedAt()
    {
        var tool = new HandlerTool(
            ToolDefinition.Create("read_thing", "Reads.", [new ToolParameter("which", ToolParameterType.String, "Which.")], RiskLevel.ReadOnly, PermissionCapability.Files),
            (call, _, _, _) => Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, "{}")));

        var result = await new ToolExecutor([tool], policy: new FakePermissions(false)).ExecuteAsync(Call("read_thing", "garbage"));

        Assert.Equal(ToolErrors.PermissionOff, Failure(result).Code);
    }

    // ---- The code of each failure ----

    [Fact]
    public async Task EveryKindOfFailureHasItsOwnCode()
    {
        var tool = Typed("open_thing", RiskLevel.SideEffect, []);
        var wipe = new FakeTool("wipe", RiskLevel.Destructive);

        Assert.Equal(ToolErrors.UnknownTool, Failure(await new ToolExecutor([tool]).ExecuteAsync(Call("nope", "{}"))).Code);
        Assert.Equal(ToolErrors.NotAllowed, Failure(await new ToolExecutor([wipe]).ExecuteAsync(Call("wipe", "{}"))).Code);
        Assert.Equal(ToolErrors.InvalidArguments, Failure(await new ToolExecutor([tool]).ExecuteAsync(Call("open_thing", "not json"))).Code);
        var declined = await new ToolExecutor([tool], new FakeConfirmation(false)).ExecuteAsync(Call("open_thing", "{}"));
        Assert.Equal(ToolResultStatus.Declined, declined.Status);
        Assert.Equal(ToolErrors.Declined, Failure(declined).Code);
    }

    // ---- Time ----

    [Fact]
    public async Task AToolThatNeverLooksAtItsToken_IsStillGivenUpWhenItsTimeIsOut()
    {
        var release = new ManualResetEventSlim();
        var blocking = new FakeTool("stuck")
        {
            Timeout = TimeSpan.FromMilliseconds(150),
            Run = (_, _, _) =>
            {
                release.Wait(TimeSpan.FromSeconds(20));
                return Task.FromResult("{}");
            },
        };
        var stopwatch = Stopwatch.StartNew();

        var result = await new ToolExecutor([blocking]).ExecuteAsync(Call("stuck", "{}"));
        stopwatch.Stop();
        release.Set();

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(ToolErrors.TimedOut, Failure(result).Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"It took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task StoppingIsNotHeldUpByAToolThatDoesNotLookAtItsToken()
    {
        var release = new ManualResetEventSlim();
        var blocking = new FakeTool("stuck")
        {
            Timeout = TimeSpan.FromSeconds(30),
            Run = (_, _, _) =>
            {
                release.Wait(TimeSpan.FromSeconds(20));
                return Task.FromResult("{}");
            },
        };
        using var stop = new CancellationTokenSource();
        var running = new ToolExecutor([blocking]).ExecuteAsync(Call("stuck", "{}"), stop.Token);
        await Task.Delay(100);
        var stopwatch = Stopwatch.StartNew();

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        stopwatch.Stop();
        release.Set();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"It took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task AToolThatFailsAfterItWasGivenUp_IsNotAnUnobservedException()
    {
        var unobserved = new List<Exception>();
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.Add(e.Exception);
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var tool = new FakeTool("late")
            {
                Timeout = TimeSpan.FromMilliseconds(50),
                Run = async (_, _, _) =>
                {
                    await Task.Delay(200);
                    throw new InvalidOperationException("too late");
                },
            };

            var result = await new ToolExecutor([tool]).ExecuteAsync(Call("late", "{}"));
            await Task.Delay(400);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.Equal(ToolErrors.TimedOut, Failure(result).Code);
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    // ---- The result is of the call that was made ----

    [Fact]
    public async Task AResultIsOfTheCallThatWasMade_WhateverTheToolPutInIt()
    {
        var tool = new HandlerTool(
            ToolDefinition.Create("get_thing", "Gets.", [], RiskLevel.ReadOnly),
            (_, _, _, _) => Task.FromResult(new ToolResult("someone-else", "other_tool", ToolResultStatus.Succeeded, """{"ok":true}""")));

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("get_thing", "{}", "mine"));

        Assert.Equal(("mine", "get_thing"), (result.ToolCallId, result.ToolName));
    }

    [Fact]
    public async Task AResultThatIsTooLarge_IsAFailureThatSaysSo()
    {
        var tool = new FakeTool("big") { Run = (_, _, _) => Task.FromResult(new string('x', ToolExecutor.MaxResultLength + 1)) };

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("big", "{}"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(ToolErrors.ResultTooLarge, Failure(result).Code);
        Assert.True(result.OutputJson.Length < 500);
    }

    [Fact]
    public async Task AResultAtTheLimitIsGivenWhole()
    {
        var tool = new FakeTool("big") { Run = (_, _, _) => Task.FromResult(new string('x', ToolExecutor.MaxResultLength)) };

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("big", "{}"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(ToolExecutor.MaxResultLength, result.OutputJson.Length);
    }

    [Fact]
    public async Task AToolThatReturnsNothing_IsAFailure()
    {
        var tool = new HandlerTool(
            ToolDefinition.Create("get_thing", "Gets.", [], RiskLevel.ReadOnly), (_, _, _, _) => Task.FromResult<ToolResult>(null!));

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("get_thing", "{}"));

        Assert.Equal(ToolErrors.Failed, Failure(result).Code);
    }

    private sealed class RecordingConfirmation(List<string> shown) : IPermissionService
    {
        public Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            shown.Add(call.ArgumentsJson);
            return Task.FromResult(ConfirmationDecision.Approved);
        }
    }
}
