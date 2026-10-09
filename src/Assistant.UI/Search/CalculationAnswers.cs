using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Core.Tools;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Search;

/// <summary>
/// The answer to straightforward arithmetic (PROJECT_SPEC §4.1): when the router reads what was asked as a sum and a calculator is
/// registered (the <c>calculate</c> tool), the sum is worked out by that tool, deterministically, and no model is asked. The answer is a
/// line of prose and a calculation card (<see cref="CalculationResult"/>) with the value large and a button that copies it. The tool's
/// output is shown only if it has the shape the contract gives it (<see cref="CalculationToolResults"/>); anything else, or a tool that
/// fails, is no answer, and the question goes on as it would have without a calculator. While no calculator is registered the router
/// never picks this route, so nothing here runs. The sum is private content and is not logged.
/// </summary>
internal sealed class CalculationAnswers(IQueryRouter router, IToolExecutor tools, ITextClipboard clipboard)
{
    /// <summary>
    /// The calculation card for <paramref name="question"/>, or <see langword="null"/> when it is not a sum for the calculator to work out,
    /// or the calculator did not give a value.
    /// </summary>
    /// <param name="question">What the user asked.</param>
    /// <param name="conversationId">The conversation it was asked in, if it has one.</param>
    /// <param name="cancellationToken">Cancels the calculation.</param>
    public async Task<MessageViewModel?> TryAnswerAsync(string question, Guid? conversationId, CancellationToken cancellationToken)
    {
        if (router.Route(question) is not { Kind: QueryRouteKind.Calculation, Expression: { Length: > 0 } expression })
        {
            return null;
        }

        var call = new ToolCall(
            "calc-" + Guid.NewGuid().ToString("N"), CalculationToolResults.Calculate, CalculationToolResults.Arguments(expression));
        var result = await tools.ExecuteAsync(call, new ToolContext(conversationId ?? Guid.Empty), cancellationToken).ConfigureAwait(true);
        if (result.Status != ToolResultStatus.Succeeded || !CalculationToolResults.TryRead(result.OutputJson, out var output))
        {
            return null;
        }

        var answer = new MessageViewModel(MessageRole.Assistant, $"{output.Expression} is {output.Result}.");
        answer.Content.Add(new CalculationResult(output.Expression, output.Result, new CopyTextCommand(clipboard, output.Result), output.Secondary));
        return answer;
    }
}
