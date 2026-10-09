using System.Data;
using Assistant.Core.Activity;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Core.Tools;
using Assistant.Tools;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// Straightforward arithmetic is a deterministic calculation (PROJECT_SPEC §4.1) once a calculator is registered: the sum goes to the
/// <c>calculate</c> tool and comes back as a calculation card, and no model is asked. The tool here is a stand-in that pins what the
/// route asks of any calculator and what it does with the answer (the real one is tried in <c>SystemToolsAppTests</c>); the route must
/// not exist at all while none is registered.
/// </summary>
public sealed class CalculationRouteTests
{
    private sealed class Clipboard : ITextClipboard
    {
        public List<string> Copied { get; } = [];

        public bool TrySetText(string text)
        {
            Copied.Add(text);
            return true;
        }
    }

    private sealed class Calculator(Func<string, ToolResult?>? answer = null) : IToolExecutor
    {
        public List<ToolCall> Calls { get; } = [];

        public List<Guid> Conversations { get; } = [];

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
            ExecuteAsync(call, new ToolContext(Guid.Empty), cancellationToken);

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add(call);
            Conversations.Add(context.ConversationId);
            using var arguments = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
            var expression = arguments.RootElement.GetProperty(CalculationToolResults.ExpressionArgument).GetString()!;
            return Task.FromResult(answer?.Invoke(expression) ?? new ToolResult(
                call.Id, call.ToolName, ToolResultStatus.Succeeded,
                CalculationToolResults.Result(new CalculationOutput(expression, Compute(expression)))));
        }
    }

    // The arithmetic of the stand-in calculator: the data engine's own expression evaluator.
    private static string Compute(string expression) =>
        Convert.ToString(new DataTable().Compute(expression, ""), System.Globalization.CultureInfo.InvariantCulture)!;

    private static QueryRouter RouterWith(bool calculator) => new(calculatorAvailable: () => calculator);

    private static async Task<List<MessageViewModel>> AskAsync(DemoAnswerProvider provider, string question, Guid? conversation = null)
    {
        var shown = new List<MessageViewModel>();
        if (conversation is { } id)
        {
            await provider.StreamAnswerAsync(id, question, shown.Add, CancellationToken.None);
        }
        else
        {
            await provider.StreamAnswerAsync(question, shown.Add, CancellationToken.None);
        }

        return shown;
    }

    [Theory]
    [InlineData("9+10", "9 + 10", "19")]
    [InlineData("what is 12 * 3", "12 * 3", "36")]
    [InlineData("calculate (2 + 3) * 4", "(2 + 3) * 4", "20")]
    public async Task ASumIsWorkedOutByTheCalculatorAndShownAsACalculationCard(string typed, string expression, string value)
    {
        var calculator = new Calculator();
        var clipboard = new Clipboard();
        var answers = new CalculationAnswers(RouterWith(true), calculator, clipboard);

        var answer = await answers.TryAnswerAsync(typed, null, CancellationToken.None);

        var call = Assert.Single(calculator.Calls);
        Assert.Equal("calculate", call.ToolName);
        Assert.Contains(expression, call.ArgumentsJson, StringComparison.Ordinal);
        Assert.NotNull(answer);
        Assert.Equal(MessageRole.Assistant, answer.Role);
        Assert.Equal($"{expression} is {value}.", answer.Text);
        var card = Assert.Single(answer.Content.OfType<CalculationResult>());
        Assert.Equal(expression, card.Expression);
        Assert.Equal(value, card.Result);
        card.CopyCommand!.Execute(null);
        Assert.Equal([value], clipboard.Copied);
    }

    [Fact]
    public async Task TheCardKeepsTheNoteTheCalculatorAdds()
    {
        var calculator = new Calculator(expression => new ToolResult(
            "c", "calculate", ToolResultStatus.Succeeded,
            CalculationToolResults.Result(new CalculationOutput(expression, "0.33", "Rounded to two decimals."))));
        var answers = new CalculationAnswers(RouterWith(true), calculator, new Clipboard());

        var answer = await answers.TryAnswerAsync("1/3", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("Rounded to two decimals.", Assert.Single(answer!.Content.OfType<CalculationResult>()).Secondary);
    }

    [Fact]
    public async Task TheCalculationIsMadeInTheConversationItWasAskedIn()
    {
        var calculator = new Calculator();
        var conversation = Guid.NewGuid();

        await new CalculationAnswers(RouterWith(true), calculator, new Clipboard()).TryAnswerAsync("2+2", conversation, CancellationToken.None);

        Assert.Equal([conversation], calculator.Conversations);
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("what is the capital of France")]
    [InlineData("2026-10-02")]
    [InlineData("find my 3+4 notes")]
    [InlineData("")]
    public async Task AnythingThatIsNotASumIsNotCalculated(string typed)
    {
        var calculator = new Calculator();

        var answer = await new CalculationAnswers(RouterWith(true), calculator, new Clipboard()).TryAnswerAsync(typed, null, CancellationToken.None);

        Assert.Null(answer);
        Assert.Empty(calculator.Calls);
    }

    [Fact]
    public async Task WithoutACalculatorASumIsNotAskedOfOne()
    {
        var calculator = new Calculator();

        var answer = await new CalculationAnswers(RouterWith(false), calculator, new Clipboard()).TryAnswerAsync("9+10", null, CancellationToken.None);

        Assert.Null(answer);
        Assert.Empty(calculator.Calls);
    }

    [Theory]
    [InlineData(ToolResultStatus.Failed, """{"error":"Division by zero."}""")]
    [InlineData(ToolResultStatus.Cancelled, "{}")]
    [InlineData(ToolResultStatus.Succeeded, "not json")]
    [InlineData(ToolResultStatus.Succeeded, """{"result":"19"}""")]
    [InlineData(ToolResultStatus.Succeeded, """{"expression":"9 + 10","result":19}""")]
    [InlineData(ToolResultStatus.Succeeded, """{"expression":"9 + 10","result":" "}""")]
    public async Task ACalculatorThatDoesNotGiveAValueIsNoAnswer_AndTheQuestionGoesOn(ToolResultStatus status, string output)
    {
        var calculator = new Calculator(_ => new ToolResult("c", "calculate", status, output));

        var answer = await new CalculationAnswers(RouterWith(true), calculator, new Clipboard()).TryAnswerAsync("9+10", null, CancellationToken.None);

        Assert.Null(answer);
        Assert.Single(calculator.Calls);
    }

    [Fact]
    public async Task TheAnswerProviderShowsTheCardAtOnce_AndNoModelIsAsked()
    {
        var calculator = new Calculator();
        var clipboard = new Clipboard();
        var provider = new DemoAnswerProvider(
            clipboard, TimeProvider.System, new ActivityTracker(), calculations: new CalculationAnswers(RouterWith(true), calculator, clipboard));

        var shown = await AskAsync(provider, "what is 9+10", Guid.NewGuid());

        var answer = Assert.Single(shown);
        Assert.Equal("9 + 10 is 19.", answer.Text);
        Assert.Single(answer.Content.OfType<CalculationResult>());
    }

    [Fact]
    public async Task ASumWithSomethingAttachedIsAboutThatAndIsNotCalculated()
    {
        var calculator = new Calculator();
        var clipboard = new Clipboard();
        var provider = new DemoAnswerProvider(
            clipboard, TimeProvider.System, new ActivityTracker(), calculations: new CalculationAnswers(RouterWith(true), calculator, clipboard));
        var question = new MessageViewModel(MessageRole.User, "9+10", texts: [new TextAttachment("some text", "notes")]);
        var shown = new List<MessageViewModel>();

        await provider.StreamAnswerAsync(Guid.NewGuid(), question, shown.Add, CancellationToken.None);

        Assert.Empty(calculator.Calls);
    }

    [Fact]
    public async Task WithNoCalculatorTheSampleAnswerIsStillGiven()
    {
        var clipboard = new Clipboard();
        var calculator = new Calculator();
        var provider = new DemoAnswerProvider(
            clipboard, TimeProvider.System, new ActivityTracker(), calculations: new CalculationAnswers(RouterWith(false), calculator, clipboard));

        var shown = await AskAsync(provider, "What is 9+10");

        Assert.Empty(calculator.Calls);
        Assert.Equal("9 + 10 is 19.", Assert.Single(shown).Text);
    }

    [Fact]
    public async Task ARegisteredCalculatorToolMakesTheRouteReal_FromTheRegistryToTheCard()
    {
        // The route exists exactly while a tool named calculate is registered; the executor is the real one, with the real checks.
        var definition = ToolDefinition.Create(
            CalculationToolResults.Calculate, "Works out a sum.",
            [new ToolParameter(CalculationToolResults.ExpressionArgument, ToolParameterType.String, "The sum, written with + - * / and parentheses.")],
            RiskLevel.ReadOnly, timeout: TimeSpan.FromSeconds(5));
        var tool = new HandlerTool(definition, (call, arguments, _, _) =>
        {
            var expression = arguments.GetProperty(CalculationToolResults.ExpressionArgument).GetString()!;
            return Task.FromResult(new ToolResult(
                call.Id, call.ToolName, ToolResultStatus.Succeeded,
                CalculationToolResults.Result(new CalculationOutput(expression, Compute(expression)))));
        });
        var registry = new ToolRegistry([tool]);
        var router = new QueryRouter(calculatorAvailable: () => registry.Find(CalculationToolResults.Calculate) is not null);
        var clipboard = new Clipboard();

        var answer = await new CalculationAnswers(router, new ToolExecutor([tool]), clipboard).TryAnswerAsync("what's 7*6", null, CancellationToken.None);

        Assert.Equal("7 * 6 is 42.", answer!.Text);
        Assert.Equal("42", Assert.Single(answer.Content.OfType<CalculationResult>()).Result);
        Assert.Equal(QueryRouteKind.Calculation, router.Route("7*6").Kind);
        Assert.Equal(QueryRouteKind.InstantSearch, new QueryRouter(calculatorAvailable: () => new ToolRegistry([]).Find("calculate") is not null).Route("7*6").Kind);
    }

    [Fact]
    public void TheToolIsNamedAsTheRouteExpectsItAndTheContractRoundTrips()
    {
        Assert.Equal("calculate", CalculationToolResults.Calculate);
        Assert.Equal("calculate", Bootstrap.ServiceCollectionExtensions.CalculatorToolName);
        var json = CalculationToolResults.Result(new CalculationOutput("1 + 1", "2", "exact"));

        Assert.True(CalculationToolResults.TryRead(json, out var output));
        Assert.Equal(new CalculationOutput("1 + 1", "2", "exact"), output);
        Assert.Equal("""{"expression":"1 + 1"}""", CalculationToolResults.Arguments("1 + 1"));
    }
}
