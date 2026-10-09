using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Tools;
using Assistant.Tools.Calculator;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// A sum the model asks the calculator to work out (PROJECT_SPEC §4.1, §4.8): the tool's structured result is drawn as the calculation
/// card, with a button that copies the value, and the model's own words follow.
/// </summary>
public sealed partial class PromptInputControlTests
{
    private static (ModelAnswerProvider Provider, RoundsModel Model, FakeClipboard Clipboard) CalculatorProvider(
        params IReadOnlyList<AssistantResponseChunk>[] rounds)
    {
        var model = new RoundsModel(ToolsModelInfo, rounds);
        var clipboard = new FakeClipboard();
        ITool[] tools = [CalculateTool.Create()];
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, toolRegistry: new ToolRegistry(tools), toolExecutor: new ToolExecutor(tools));
        return (new ModelAnswerProvider(orchestrator, new FixedClock(Now), clipboard: clipboard), model, clipboard);
    }

    [Fact]
    public void ASumTheModelAsksTheCalculatorFor_IsShownAsTheCalculationCard_AndTheModelsWordsFollow() => RunSta(() =>
    {
        var (provider, model, clipboard) = CalculatorProvider(
            [ModelCalls("calculate", """{"expression":"12*(3+4)"}""")],
            [AssistantResponseChunk.ForTextDelta("It is 84.")]);
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "what is 12 times 3 plus 4 all multiplied", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var answer = Assert.Single(shown);
        Assert.Equal(MessageStatus.Complete, answer.Status);
        var card = Assert.IsType<CalculationResult>(answer.Content[0]);
        Assert.Equal(("12 * (3 + 4)", "84", null), (card.Expression, card.Result, card.Secondary));
        Assert.Equal("It is 84.", Assert.IsType<TextContent>(answer.Content[1]).Text);

        // The value can be copied from the card.
        Assert.True(card.CopyCommand!.CanExecute(null));
        card.CopyCommand.Execute(null);
        Assert.Equal(["84"], clipboard.Copied);

        // The model was offered the calculator, and its next round carried the structured result.
        Assert.Equal(["calculate"], model.Requests[0].Tools.Select(tool => tool.Name));
        Assert.Equal("""{"expression":"12 * (3 + 4)","result":"84"}""", model.Requests[1].Messages[^1].Text);
    });

    [Fact]
    public void ARoundedResultKeepsItsNoteOnTheCard() => RunSta(() =>
    {
        var (provider, _, _) = CalculatorProvider(
            [ModelCalls("calculate", """{"expression":"1/3"}""")],
            [AssistantResponseChunk.ForTextDelta("About a third.")]);
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "one third", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var card = Assert.IsType<CalculationResult>(Assert.Single(shown).Content[0]);
        Assert.Equal(("0.3333333333", "Rounded to 10 decimal places."), (card.Result, card.Secondary));
    });

    [Theory]
    [InlineData("""{"expression":"1/0"}""")]
    [InlineData("""{"expression":"two plus two"}""")]
    [InlineData("""{"expression":"1+1","precision":3}""")]
    [InlineData("not json")]
    public void ASumThatCannotBeWorkedOut_ShowsNoCard_AndTheModelSaysWhy(string arguments) => RunSta(() =>
    {
        var (provider, model, _) = CalculatorProvider(
            [ModelCalls("calculate", arguments)],
            [AssistantResponseChunk.ForTextDelta("I could not work that out.")]);
        var shown = new List<MessageViewModel>();

        var asked = provider.StreamAnswerAsync(Guid.NewGuid(), "divide", shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var answer = Assert.Single(shown);
        Assert.DoesNotContain(answer.Content, content => content is CalculationResult);
        Assert.Equal("I could not work that out.", Assert.IsType<TextContent>(Assert.Single(answer.Content)).Text);

        // The model's next round read a structured error with a code.
        var told = model.Requests[1].Messages[^1].Text;
        Assert.Contains("\"error\":", told, StringComparison.Ordinal);
        Assert.Contains("\"code\":", told, StringComparison.Ordinal);
    });
}
