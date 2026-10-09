using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.Tools;
using Assistant.Tools.Files;
using Assistant.Tools.Screen;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The registry of typed tools (PROJECT_SPEC §4.8): what a tool must be to be registered, that the model can call only what is
/// registered, that the permission a tool names is checked before it runs, and that nothing that runs a command is ever a tool.
/// </summary>
public sealed class ToolRegistryTests
{
    private static readonly ToolHandler Ok = (call, _, _, _) =>
        Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, """{"ok":true}"""));

    private static HandlerTool Tool(
        string name = "add_numbers", RiskLevel risk = RiskLevel.ReadOnly, PermissionCapability? permission = null, TimeSpan? timeout = null,
        IEnumerable<ToolParameter>? parameters = null, ToolHandler? handler = null) =>
        new(
            ToolDefinition.Create(
                name, "Adds two numbers.",
                parameters ?? [new ToolParameter("first", ToolParameterType.Number, "The first number.")], risk, permission, timeout),
            handler ?? Ok);

    private static HandlerTool WithSchema(string name, string schema) =>
        new(new ToolDefinition(name, "A tool.", schema, RiskLevel.ReadOnly), Ok);

    private static ToolCall Call(string name, string arguments = "{}") => new("c1", name, arguments);

    // ---- A typed definition ----

    [Fact]
    public void ATypedDefinitionHasItsNameDescriptionSchemaCategoryPermissionAndTimeout()
    {
        var definition = ToolDefinition.Create(
            "open_folder", "Opens a folder.",
            [
                new ToolParameter("which", ToolParameterType.String, "Which folder.", Choices: ["downloads", "documents"]),
                new ToolParameter("count", ToolParameterType.Integer, "How many.", Required: false),
            ],
            RiskLevel.SideEffect, PermissionCapability.Files, TimeSpan.FromSeconds(7));

        Assert.Equal("open_folder", definition.Name);
        Assert.Equal("Opens a folder.", definition.Description);
        Assert.Equal(RiskLevel.SideEffect, definition.RiskLevel);
        Assert.Equal(PermissionCapability.Files, definition.RequiredPermission);
        Assert.Equal(TimeSpan.FromSeconds(7), definition.EffectiveTimeout);
        using var schema = JsonDocument.Parse(definition.InputSchemaJson);
        var root = schema.RootElement;
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.Equal("string", root.GetProperty("properties").GetProperty("which").GetProperty("type").GetString());
        Assert.Equal(["downloads", "documents"], root.GetProperty("properties").GetProperty("which").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("integer", root.GetProperty("properties").GetProperty("count").GetProperty("type").GetString());
        Assert.Equal(["which"], root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void ADefinitionWithoutATimeoutGetsTheDefault_AndNoPermission()
    {
        var definition = new ToolDefinition("now", "The time.", """{"type":"object"}""", RiskLevel.ReadOnly);

        Assert.Equal(ToolDefinition.DefaultTimeout, definition.EffectiveTimeout);
        Assert.Null(definition.RequiredPermission);
        Assert.Equal(ToolDefinition.DefaultTimeout, ((ITool)new HandlerTool(definition, Ok)).Timeout);
    }

    [Fact]
    public async Task ATypedSchemaIsWhatACallsArgumentsAreCheckedAgainst()
    {
        var tool = Tool(parameters:
        [
            new ToolParameter("expression", ToolParameterType.String, "The sum."),
            new ToolParameter("mode", ToolParameterType.String, "How.", Required: false, Choices: ["a", "b"]),
        ]);
        var executor = new ToolExecutor([tool]);

        Assert.Equal(ToolResultStatus.Succeeded, (await executor.ExecuteAsync(Call("add_numbers", """{"expression":"1+1","mode":"a"}"""))).Status);
        Assert.Contains("is required", (await executor.ExecuteAsync(Call("add_numbers", "{}"))).OutputJson, StringComparison.Ordinal);
        Assert.Contains("one of: a, b", (await executor.ExecuteAsync(Call("add_numbers", """{"expression":"1","mode":"c"}"""))).OutputJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("1st")]
    [InlineData("")]
    public void AParameterIsNamedInLowerSnakeCase(string name)
    {
        Assert.Throws<ArgumentException>(() => ToolSchema.Build([new ToolParameter(name, ToolParameterType.String, "Something.")]));
    }

    [Fact]
    public void AParameterNeedsADescription_IsGivenOnce_AndChoicesAreForText()
    {
        Assert.Throws<ArgumentException>(() => ToolSchema.Build([new ToolParameter("a", ToolParameterType.String, " ")]));
        Assert.Throws<ArgumentException>(() => ToolSchema.Build(
            [new ToolParameter("a", ToolParameterType.String, "x"), new ToolParameter("a", ToolParameterType.Integer, "y")]));
        Assert.Throws<ArgumentException>(() => ToolSchema.Build(
            [new ToolParameter("a", ToolParameterType.Integer, "x", Choices: ["1"])]));
        Assert.Equal("""{"type":"object","properties":{},"additionalProperties":false}""", ToolSchema.Build([]));
    }

    // ---- What may be registered ----

    [Fact]
    public void RegisteringKeepsTheOrderAndFindsATool_AndOnlyThatTool()
    {
        var registry = new ToolRegistry([Tool("first_tool"), Tool("second_tool")]);

        Assert.Equal(["first_tool", "second_tool"], registry.Tools.Select(tool => tool.Name));
        Assert.Equal("second_tool", registry.Find("second_tool")?.Name);
        Assert.Null(registry.Find("third_tool"));
        Assert.Null(registry.Find(null!));
    }

    [Theory]
    [InlineData("AddNumbers")]
    [InlineData("add-numbers")]
    [InlineData("add numbers")]
    [InlineData("_add")]
    [InlineData("add__numbers")]
    [InlineData("")]
    public void ANameIsStableLowerSnakeCase(string name)
    {
        var exception = Assert.Throws<ArgumentException>(() => new ToolRegistry([WithSchema(name, """{"type":"object"}""")]));

        Assert.Contains("snake_case", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameOverSixtyFourCharactersIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([Tool(new string('a', 65))]));
        _ = new ToolRegistry([Tool(new string('a', 64))]);
    }

    [Fact]
    public void TwoToolsWithOneNameAreRefused()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ToolRegistry([Tool("same"), Tool("same")]));

        Assert.Contains("same", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ToolExecutor([Tool("same"), Tool("same")]));
    }

    [Fact]
    public void AToolThatCanDestroyDataIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([Tool("wipe_files", RiskLevel.Destructive)]));
    }

    [Fact]
    public void AToolIsNeverGivenThePermissionToDestroyData()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([Tool(permission: PermissionCapability.DestructiveActions)]));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([Tool(permission: (PermissionCapability)99)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    public void ATimeIsMoreThanNothingAndAtMostFiveMinutes(int seconds)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([Tool(timeout: TimeSpan.FromSeconds(seconds))]));
        _ = new ToolRegistry([Tool(timeout: TimeSpan.FromSeconds(300))]);
    }

    [Fact]
    public void ADescriptionIsRequired_AndBounded()
    {
        var empty = new HandlerTool(new ToolDefinition("a_tool", " ", """{"type":"object"}""", RiskLevel.ReadOnly), Ok);
        var huge = new HandlerTool(new ToolDefinition("a_tool", new string('x', 2001), """{"type":"object"}""", RiskLevel.ReadOnly), Ok);

        Assert.Throws<ArgumentException>(() => new ToolRegistry([empty]));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([huge]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"string"}""")]
    [InlineData("""{"type":"object","properties":[]}""")]
    [InlineData("""{"type":"object","properties":{"a":{"description":"no type"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"mystery"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["b"]}""")]
    [InlineData("""{"type":"object","properties":{"A":{"type":"string"}}}""")]
    public void AnInputSchemaIsAnObjectOfTypedProperties(string schema)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([WithSchema("a_tool", schema)]));
    }

    [Fact]
    public void ARegisteredToolMayTakeNoArguments()
    {
        _ = new ToolRegistry([WithSchema("a_tool", """{"type":"object"}""")]);
        _ = new ToolRegistry([WithSchema("b_tool", """{"type":"object","properties":{},"required":[]}""")]);
    }

    // ---- No tool runs a command ----

    [Theory]
    [InlineData("run_powershell")]
    [InlineData("powershell")]
    [InlineData("execute_command")]
    [InlineData("shell_exec")]
    [InlineData("cmd")]
    [InlineData("bash_script")]
    [InlineData("eval_code")]
    [InlineData("open_terminal")]
    [InlineData("run_script")]
    public void NoToolIsNamedForRunningACommandOrAScript(string name)
    {
        var exception = Assert.Throws<ArgumentException>(() => new ToolRegistry([Tool(name)]));

        Assert.Contains("command or a script", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ToolExecutor([Tool(name)]));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("script")]
    [InlineData("command_line")]
    [InlineData("shell")]
    [InlineData("args")]
    [InlineData("executable")]
    public void NoToolTakesSomethingToRunAsAnArgument(string argument)
    {
        var tool = Tool("open_thing", parameters: [new ToolParameter(argument, ToolParameterType.String, "Something.")]);

        var exception = Assert.Throws<ArgumentException>(() => new ToolRegistry([tool]));

        Assert.Contains("something to run", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ToolExecutor([tool]));
    }

    [Fact]
    public void TheToolsOfTheApp_AreReadOnly_AreTimedAndNameOnlyWhatTheyAreFor()
    {
        var files = new ConversationFiles();
        var tools = new ITool[]
        {
            new SearchFilesTool(new FakeFileRequests(), files),
            new ReadScreenTextTool(new ScreenToolsTests.FakeScreens()),
        };

        var registry = new ToolRegistry(tools);

        Assert.Equal(["search_files", "read_screen_text"], registry.Tools.Select(tool => tool.Name));
        Assert.All(registry.Tools, tool =>
        {
            Assert.Equal(RiskLevel.ReadOnly, tool.RiskLevel);
            Assert.NotNull(tool.Timeout);
        });
        Assert.Equal(PermissionCapability.Files, registry.Find("search_files")!.RequiredPermission);
        Assert.Null(registry.Find("read_screen_text")!.RequiredPermission);
    }

    // ---- The model may call only what is registered, and only with the permission on ----

    [Fact]
    public async Task OnlyARegisteredToolRunsAnythingElseIsAFailedResult()
    {
        var ran = false;
        var executor = new ToolExecutor([Tool(handler: (call, _, _, _) =>
        {
            ran = true;
            return Ok(call, default, new ToolContext(Guid.Empty), default);
        })]);

        foreach (var name in new[] { "run_powershell", "powershell", "cmd", "Add_Numbers", "add_numbers ", "" })
        {
            var result = await executor.ExecuteAsync(Call(name));

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Contains("not a tool you have", result.OutputJson, StringComparison.Ordinal);
        }

        Assert.False(ran);
    }

    [Fact]
    public async Task AToolThatNeedsAPermissionRunsOnlyWhileItIsOn()
    {
        var runs = 0;
        var tool = Tool(permission: PermissionCapability.Files, handler: (call, arguments, context, token) =>
        {
            runs++;
            return Ok(call, arguments, context, token);
        });

        var off = await new ToolExecutor([tool], policy: new FakePermissions(false)).ExecuteAsync(Call("add_numbers", """{"first":1}"""));
        var on = await new ToolExecutor([tool], policy: new FakePermissions(true)).ExecuteAsync(Call("add_numbers", """{"first":1}"""));

        Assert.Equal(ToolResultStatus.Failed, off.Status);
        Assert.Contains("Files is turned off in Settings, under Permissions", off.OutputJson, StringComparison.Ordinal);
        Assert.Equal(ToolResultStatus.Succeeded, on.Status);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ATestOfThePermissionComesBeforeTheArgumentsAreLookedAt()
    {
        var tool = Tool(permission: PermissionCapability.Files);

        var result = await new ToolExecutor([tool], policy: new FakePermissions(false)).ExecuteAsync(Call("add_numbers", "not json"));

        Assert.Contains("turned off", result.OutputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("not valid JSON", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAWayToCheckThePermissionAToolThatNeedsOneDoesNotRun()
    {
        var runs = 0;
        var tool = Tool(permission: PermissionCapability.Files, handler: (call, arguments, context, token) =>
        {
            runs++;
            return Ok(call, arguments, context, token);
        });

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("add_numbers", """{"first":1}"""));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("cannot be checked", result.OutputJson, StringComparison.Ordinal);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ACapabilityThisBuildCannotDoIsNotAvailableWhateverTheSettingsSay()
    {
        var tool = Tool(permission: PermissionCapability.Messaging);

        var result = await new ToolExecutor([tool], policy: new RefusingPermissions()).ExecuteAsync(Call("add_numbers", """{"first":1}"""));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("not available in this version", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolWithNoPermissionNamedNeedsNoPolicy()
    {
        var result = await new ToolExecutor([Tool()]).ExecuteAsync(Call("add_numbers", """{"first":1}"""));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task ASideEffectToolStillAsksTheUserAfterThePermissionAllowsIt()
    {
        var tool = Tool(risk: RiskLevel.SideEffect, permission: PermissionCapability.Files);
        var declined = new FakeConfirmation(approve: false);

        var result = await new ToolExecutor([tool], declined, new FakePermissions(true)).ExecuteAsync(Call("add_numbers", """{"first":1}"""));

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Equal(1, declined.Asked);
    }

    [Fact]
    public async Task ATimeLimitInTheDefinitionIsTheLimitOfACall()
    {
        var tool = Tool(timeout: TimeSpan.FromMilliseconds(50), handler: async (_, _, _, token) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });

        var result = await new ToolExecutor([tool]).ExecuteAsync(Call("add_numbers", """{"first":1}"""));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("took too long", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandlerToolIsOfferedAlways_OrWhereItsRuleSays()
    {
        var context = new ToolContext(Guid.NewGuid());
        var definition = ToolDefinition.Create("a_tool", "A tool.", [], RiskLevel.ReadOnly);

        Assert.True(new HandlerTool(definition, Ok).IsOffered(context));
        Assert.False(new HandlerTool(definition, Ok, _ => false).IsOffered(context));
        Assert.Throws<ArgumentNullException>(() => new HandlerTool(definition, null!));
        Assert.Throws<ArgumentNullException>(() => new HandlerTool(null!, Ok));
    }

    private sealed class RefusingPermissions : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(capability, PermissionDecisionReason.NotAvailable));
    }
}
