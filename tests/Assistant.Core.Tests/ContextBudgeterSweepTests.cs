using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Xunit;
using static Assistant.Core.Tests.BudgetTestData;

namespace Assistant.Core.Tests;

/// <summary>
/// The rules held over hundreds of invented conversations of every shape (fixed seeds, so every run is the same): the
/// estimate is exactly what the fitted messages cost, it is within the budget, what is kept of the conversation is an
/// unbroken run of whole turns, and the same conversation always gives the same result.
/// </summary>
public sealed class ContextBudgeterSweepTests
{
    private const int Cases = 400;

    private static readonly string Instructions = Sized("rules", 4);
    private readonly ContextBudgeter _budgeter = new(new HeuristicTokenEstimator());

    [Fact]
    public void TheEstimateIsExactlyWhatTheFittedMessagesCost_AndIsWithinTheBudget()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var (conversation, model, limits) = InventedConversation.Create(seed, minWindow: 300);

            var fit = _budgeter.Fit(Instructions, conversation, model, limits);

            var recount = Recount(Instructions, fit.Messages, model);
            Assert.True(recount == fit.Report.EstimatedPromptTokens, $"seed {seed}: recount {recount}, report {fit.Report.EstimatedPromptTokens}");
            Assert.True(fit.Report.Fits, $"seed {seed}: {fit.Report.EstimatedPromptTokens} over {fit.Report.Budget.PromptTokens}");
            Assert.Equal(fit.Report.Budget.ReservedOutputTokens, fit.MaxOutputTokens);
        }
    }

    [Fact]
    public void WhatIsKept_IsAnUnbrokenRunOfWholeTurns_EndingWithTheUsersMessage()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var (conversation, model, limits) = InventedConversation.Create(seed, minWindow: 300);

            var fit = _budgeter.Fit(Instructions, conversation, model, limits);

            var first = conversation.Length - fit.Messages.Count;
            Assert.True(first >= 0, $"seed {seed}");
            Assert.Equal(conversation.Skip(first).Select(message => message.Id), fit.Messages.Select(message => message.Id));

            // The run starts where a turn starts: at a user message that does not follow another one's question.
            // (When no earlier turn is kept, the run is the user's message alone, whatever comes before it.)
            Assert.True(
                first == 0
                || first == conversation.Length - 1
                || (conversation[first].Role == MessageRole.User && conversation[first - 1].Role != MessageRole.User),
                $"seed {seed}: the run starts inside a turn");
            Assert.Equal(MessageRole.User, fit.Messages[^1].Role);
            Assert.Equal(conversation[^1].Id, fit.Messages[^1].Id);
        }
    }

    [Fact]
    public void ContextIsOnlyEverLeftOutOrCutFromItsStart_NeverChangedOrReordered()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var (conversation, model, limits) = InventedConversation.Create(seed, minWindow: 300);

            var fit = _budgeter.Fit(Instructions, conversation, model, limits);

            foreach (var message in fit.Messages)
            {
                var original = conversation.Single(candidate => candidate.Id == message.Id);
                var originalIds = original.ContextItems.Select(item => item.Id).ToList();

                // The items that are left are some of the originals, in their order.
                var positions = message.ContextItems.Select(item => originalIds.IndexOf(item.Id)).ToList();
                Assert.DoesNotContain(-1, positions);
                Assert.Equal(positions.OrderBy(position => position), positions);

                foreach (var item in message.ContextItems)
                {
                    var before = original.ContextItems.Single(candidate => candidate.Id == item.Id);
                    if (!ReferenceEquals(before, item))
                    {
                        Assert.EndsWith(ContextBudgeter.ContextCutMarker, item.Text, StringComparison.Ordinal);
                        var prefix = item.Text![..^ContextBudgeter.ContextCutMarker.Length];
                        Assert.StartsWith(prefix, before.Text, StringComparison.Ordinal);
                        Assert.Equal(before.DisplayName, item.DisplayName);
                        Assert.Equal(before.Type, item.Type);
                    }
                }
            }

            // The conversation given is as it was.
            Assert.Equal(InventedConversation.Create(seed, minWindow: 300).Conversation.Select(message => message.Text), conversation.Select(message => message.Text));
        }
    }

    [Fact]
    public void TheUserIsToldOfExactlyWhatWasLeftOut()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var (conversation, model, limits) = InventedConversation.Create(seed, minWindow: 300);

            var fit = _budgeter.Fit(Instructions, conversation, model, limits);

            var report = fit.Report;
            Assert.Equal(report.TurnsLeftOut > 0, fit.Notices.Contains(ContextBudgetNotices.EarlierMessagesLeftOut));
            Assert.Equal(report.QuestionShortened, fit.Notices.Contains(ContextBudgetNotices.MessageShortened));
            Assert.Equal(report.AnythingTrimmed, fit.Notices.Count > 0);

            // One notice for the context that was cut short and one for what was left out, as there is of each.
            var aboutContext = fit.Notices.Count(
                notice => notice != ContextBudgetNotices.EarlierMessagesLeftOut && notice != ContextBudgetNotices.MessageShortened);
            Assert.Equal((report.ContextItemsShortened > 0 ? 1 : 0) + (report.ContextItemsLeftOut > 0 ? 1 : 0), aboutContext);
            Assert.Equal(!report.AnythingTrimmed, ReferenceEquals(fit.Messages, conversation));
        }
    }

    [Fact]
    public void TheSameConversation_GivesTheSameResult_AndMoreRoomNeverKeepsFewerTurns()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var (conversation, model, limits) = InventedConversation.Create(seed, minWindow: 300);
            var roomier = limits with
            {
                NormalContextTokens = limits.NormalContextTokens + 700,
                HeavyContextTokens = limits.HeavyContextTokens + 700,
            };

            var first = _budgeter.Fit(Instructions, conversation, model, limits);
            var again = _budgeter.Fit(Instructions, conversation, model, limits);
            var more = _budgeter.Fit(Instructions, conversation, model, roomier);

            Assert.Equal(first.Report, again.Report);
            Assert.Equal(first.Notices, again.Notices);
            Assert.Equal(
                first.Messages.SelectMany(message => message.ContextItems.Select(item => item.Text)),
                again.Messages.SelectMany(message => message.ContextItems.Select(item => item.Text)));
            Assert.True(
                more.Report.TurnsKept >= first.Report.TurnsKept || more.Report.Budget.WindowTokens == first.Report.Budget.WindowTokens,
                $"seed {seed}: {first.Report.TurnsKept} turns kept, then {more.Report.TurnsKept} with more room");
        }
    }

    [Fact]
    public void TheWholePromptAsItIsLaidOut_WrappersIncluded_IsWithinTheBudget()
    {
        var builder = new PromptBuilder();
        for (var seed = 0; seed < Cases; seed++)
        {
            // The window must hold the longest instructions a conversation can have (every guidance, the screenshot's included).
            var (conversation, model, limits) = InventedConversation.Create(seed, minWindow: 800);

            var built = builder.Build(null, conversation, model, limits);

            var request = built.Request;
            var rendered = ContextBudgeter.RequestOverheadTokens
                + Estimate(request.Instructions)
                + request.Messages.Sum(message => ContextBudgeter.MessageOverheadTokens + Estimate(message.Text))
                + request.Images.Count * ContextBudgeter.ImageTokens;
            var budget = built.Budget!;
            Assert.True(budget.Fits, $"seed {seed}");
            Assert.True(
                rendered <= budget.Budget.PromptTokens,
                $"seed {seed}: the prompt takes about {rendered}, the budget is {budget.Budget.PromptTokens}");
            Assert.Equal(budget.Budget.ReservedOutputTokens, request.MaxOutputTokens);
        }
    }

    // What the messages cost, counted again without the budgeter: the request, each message that is sent, and the
    // context its user messages would carry.
    private static int Recount(string instructions, IReadOnlyList<Message> messages, ModelInfo model)
    {
        var total = ContextBudgeter.RequestOverheadTokens + Estimate(instructions);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (!(message.Role == MessageRole.Assistant && message.Text.Length == 0 && message.ToolCalls.Count == 0))
            {
                total += ContextBudgeter.MessageOverheadTokens + Estimate(message.Text);
            }

            foreach (var item in message.ContextItems)
            {
                if (item.Type == ContextItemType.Screenshot && model.SupportsVision && !item.ImageData.IsEmpty)
                {
                    total += index == messages.Count - 1 ? ContextBudgeter.ImageTokens : 0;
                }
                else if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    var label = item.DisplayName.Length > 100 ? item.DisplayName[..100] : item.DisplayName;
                    total += ContextBudgeter.ContextBlockOverheadTokens + Estimate(label) + Estimate(item.Text);
                }
            }
        }

        return total;
    }
}

/// <summary>Conversations of every shape, made from a seed: the same seed is always the same conversation.</summary>
internal static class InventedConversation
{
    public static (Message[] Conversation, ModelInfo Model, ContextLimitSettings Limits) Create(int seed, int minWindow)
    {
        var random = new Random(seed);
        var messages = new List<Message>();
        for (var turn = random.Next(0, 13); turn > 0; turn--)
        {
            messages.Add(User(Text(random, random.Next(1, 300)), Items(random, 2)));
            if (random.Next(8) > 0)
            {
                messages.Add(Answer(random.Next(6) == 0 ? string.Empty : Text(random, random.Next(1, 500))));
            }
        }

        var question = random.Next(4) == 0 ? random.Next(200, 3000) : random.Next(1, 60);
        messages.Add(User(Text(random, question), Items(random, 3)));

        var limits = new ContextLimitSettings
        {
            NormalContextTokens = random.Next(minWindow, 6000),
            HeavyContextTokens = random.Next(minWindow, 12000),
            ReservedOutputTokens = random.Next(50, 500),
        };
        var model = new ModelInfo("invented", random.Next(minWindow, 12000)) { SupportsVision = random.Next(2) == 0 };
        return ([.. messages], model, limits);
    }

    private static ContextItem[] Items(Random random, int most)
    {
        var items = new ContextItem[random.Next(0, most + 1)];
        for (var index = 0; index < items.Length; index++)
        {
            var name = random.Next(6) == 0
                ? $"Long \"name\"\n{index} " + new string('x', random.Next(60, 200))
                : $"Item{index}.txt";
            var text = random.Next(16) switch
            {
                0 => null,
                1 => "  \n ",
                _ => Text(random, random.Next(1, 1500)),
            };
            var item = random.Next(5) switch
            {
                0 => new ContextItem(Guid.NewGuid(), ContextItemType.File, name),
                1 => new ContextItem(Guid.NewGuid(), ContextItemType.Selection, name),
                2 => new ContextItem(Guid.NewGuid(), ContextItemType.Page, name),
                3 => new ContextItem(Guid.NewGuid(), ContextItemType.SearchResults, name),
                _ => new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, name),
            };
            items[index] = item with
            {
                Text = text,
                ImageData = item.Type == ContextItemType.Screenshot && random.Next(2) == 0 ? new byte[] { 1, 2, 3 } : default,
            };
        }

        return items;
    }

    // Prose, paragraphs, code or text in another script, about `tokens` long.
    private static string Text(Random random, int tokens) => random.Next(4) switch
    {
        0 => Tokens(tokens),
        1 => Paragraphs(Math.Max(1, tokens / 20), 20),
        2 => string.Concat(Enumerable.Repeat("foo(bar[1], \"x\");\n", Math.Max(1, tokens / 12))),
        _ => string.Concat(Enumerable.Repeat("日本語のテキスト。 ", Math.Max(1, tokens / 8))),
    };
}
