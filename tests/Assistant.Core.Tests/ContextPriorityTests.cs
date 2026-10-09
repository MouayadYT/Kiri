using Assistant.Core.Budgeting;
using Assistant.Core.Domain;
using Xunit;
using static Assistant.Core.Tests.BudgetTestData;

namespace Assistant.Core.Tests;

/// <summary>
/// The fixed order context is served in when it does not all fit: what the user explicitly selected, then what is on the
/// screen now, then what a search or a tool returned, then what was attached to earlier messages.
/// </summary>
public sealed class ContextPriorityTests
{
    private static readonly string Instructions = Sized("rules", 4);
    private static readonly int Fixed = ContextBudgeter.RequestOverheadTokens + 4;
    private static readonly int Overhead = ContextBudgeter.MessageOverheadTokens;
    private static readonly int Block = ContextBudgeter.ContextBlockOverheadTokens;

    private readonly ContextBudgeter _budgeter = new(new HeuristicTokenEstimator());

    // ---- The rules ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ContextItemType.File, ContextSource.UserSelected)]
    [InlineData(ContextItemType.Image, ContextSource.UserSelected)]
    [InlineData(ContextItemType.Selection, ContextSource.CurrentScreen)]
    [InlineData(ContextItemType.Screenshot, ContextSource.CurrentScreen)]
    [InlineData(ContextItemType.Page, ContextSource.CurrentScreen)]
    [InlineData(ContextItemType.SearchResults, ContextSource.Retrieval)]
    public void AnItemThatSaysNothingAboutItsSource_RanksAsTheKindOfContextItIs(ContextItemType type, ContextSource expected)
    {
        var item = new ContextItem(Guid.NewGuid(), type, "x");

        Assert.Equal(expected, ContextPriorityRules.SourceOf(item));
    }

    [Theory]
    [InlineData(ContextItemType.Selection, ContextSource.UserSelected, ContextPriority.Selected)]
    [InlineData(ContextItemType.File, ContextSource.Retrieval, ContextPriority.Retrieved)]
    [InlineData(ContextItemType.File, ContextSource.Tool, ContextPriority.Retrieved)]
    [InlineData(ContextItemType.File, ContextSource.CurrentScreen, ContextPriority.OnScreen)]
    public void WhatTheItemSays_DecidesOverItsKind(ContextItemType type, ContextSource source, ContextPriority expected)
    {
        var item = new ContextItem(Guid.NewGuid(), type, "x") { Source = source };

        Assert.Equal(expected, ContextPriorityRules.Of(item, isCurrent: true));
    }

    [Theory]
    [InlineData(ContextSource.UserSelected, ContextPriority.EarlierAttached)]
    [InlineData(ContextSource.CurrentScreen, ContextPriority.EarlierAttached)]
    [InlineData(ContextSource.Unspecified, ContextPriority.EarlierAttached)]
    [InlineData(ContextSource.Retrieval, ContextPriority.EarlierRetrieved)]
    [InlineData(ContextSource.Tool, ContextPriority.EarlierRetrieved)]
    public void ContextOfAnEarlierMessage_RanksAfterAllThatIsCurrent(ContextSource source, ContextPriority expected)
    {
        Assert.Equal(expected, ContextPriorityRules.Of(source, isCurrent: false));
    }

    [Fact]
    public void TheRanks_AreInTheOrderTheRulesStateThem()
    {
        Assert.True(ContextPriority.Selected < ContextPriority.OnScreen);
        Assert.True(ContextPriority.OnScreen < ContextPriority.Retrieved);
        Assert.True(ContextPriority.Retrieved < ContextPriority.EarlierAttached);
        Assert.True(ContextPriority.EarlierAttached < ContextPriority.EarlierRetrieved);
    }

    // ---- What the budgeter does with them -------------------------------------------------------------------------

    [Fact]
    public void WhenOnlySomeContextFits_ItIsServedByRank_WhateverOrderItWasAttachedIn()
    {
        var selected = File("F", Tokens(100));
        var onScreen = Selection("S", Tokens(100));
        var retrieved = Retrieved("R", Tokens(100));
        var earlier = File("E", Tokens(100));

        // Attached in the opposite order to their ranks.
        var conversation = new[] { User("t", earlier), Answer("r"), User("q", retrieved, onScreen, selected) };
        var fit = _budgeter.Fit(Instructions, conversation, LargeModel, Room(RoomFor(itemsWhole: 2, messages: 3)));

        Assert.Equal(
            [
                (selected.Id, ContextPriority.Selected, ContextFate.Whole),
                (onScreen.Id, ContextPriority.OnScreen, ContextFate.Whole),
                (retrieved.Id, ContextPriority.Retrieved, ContextFate.LeftOut),
                (earlier.Id, ContextPriority.EarlierAttached, ContextFate.LeftOut),
            ],
            fit.Report.Items.Select(item => (item.ItemId, item.Priority, item.Fate)));
        Assert.Equal(2, fit.Report.ContextItemsKept);
        Assert.Equal(2, fit.Report.ContextItemsLeftOut);
    }

    [Fact]
    public void WhenOneItemFits_TheUsersOwnSelectionIsTheOneSent()
    {
        var selected = File("F", Tokens(100));
        var onScreen = Selection("S", Tokens(100));
        var retrieved = Retrieved("R", Tokens(100));
        var conversation = new[] { User("q", onScreen, retrieved, selected) };

        var fit = _budgeter.Fit(Instructions, conversation, LargeModel, Room(RoomFor(itemsWhole: 1, messages: 1)));

        var sent = Assert.Single(fit.Messages[^1].ContextItems);
        Assert.Equal(selected.Id, sent.Id);
        Assert.Equal(2, fit.Report.ContextItemsLeftOut);
        Assert.Contains(ContextBudgetNotices.ContextLeftOut(["S", "R"]), fit.Notices);
    }

    [Fact]
    public void WhatTheItemSaysAboutItsSource_DecidesItsRankInThePrompt()
    {
        // A selection the user picked on purpose outranks a file that a search found.
        var picked = Selection("Picked", Tokens(100)) with { Source = ContextSource.UserSelected };
        var found = File("Found", Tokens(100)) with { Source = ContextSource.Retrieval };
        var conversation = new[] { User("q", found, picked) };

        var fit = _budgeter.Fit(Instructions, conversation, LargeModel, Room(RoomFor(itemsWhole: 1, messages: 1)));

        Assert.Equal(picked.Id, Assert.Single(fit.Messages[^1].ContextItems).Id);
    }

    [Fact]
    public void ContextRetrievedForAnEarlierMessage_IsTheFirstToGo()
    {
        var earlierAttached = File("A", Tokens(100));
        var earlierRetrieved = Retrieved("B", Tokens(100));
        var conversation = new[] { User("t", earlierRetrieved, earlierAttached), Answer("r"), User("q") };

        var fit = _budgeter.Fit(Instructions, conversation, LargeModel, Room(RoomFor(itemsWhole: 1, messages: 3)));

        Assert.Equal(
            [
                (earlierAttached.Id, ContextPriority.EarlierAttached, ContextFate.Whole),
                (earlierRetrieved.Id, ContextPriority.EarlierRetrieved, ContextFate.LeftOut),
            ],
            fit.Report.Items.Select(item => (item.ItemId, item.Priority, item.Fate)));
    }

    [Fact]
    public void ItemsOfOneRank_ShareWhatIsLeftFairly()
    {
        var first = File("A", Tokens(300));
        var second = File("B", Tokens(300));
        var conversation = new[] { User("q", first, second) };

        var fit = _budgeter.Fit(Instructions, conversation, LargeModel, Room(RoomFor(itemsWhole: 1, messages: 1) + 150));

        Assert.All(fit.Report.Items, item => Assert.Equal(ContextFate.Shortened, item.Fate));
        Assert.Equal(2, fit.Report.ContextItemsShortened);
    }

    [Fact]
    public void TheContextOfTurnsThatWereLeftOut_IsReportedAsLeftOut()
    {
        var old = File("Old", Tokens(50));
        var conversation = new[]
        {
            User(Tokens(200), old), Answer(Tokens(200)),
            User("q"),
        };

        var fit = _budgeter.Fit(Instructions, conversation, LargeModel, Room(Fixed + Overhead + 1 + 40));

        Assert.Equal(1, fit.Report.TurnsLeftOut);
        var item = Assert.Single(fit.Report.Items);
        Assert.Equal((old.Id, ContextPriority.EarlierAttached, ContextFate.LeftOut), (item.ItemId, item.Priority, item.Fate));

        // They do not count as items that were left out of the prompt: their turn was, and it is told once.
        Assert.Equal(0, fit.Report.ContextItemsLeftOut);
    }

    [Fact]
    public void TheSameConversation_GivesTheSameReport_EveryTime()
    {
        var conversation = new[]
        {
            User("t", Retrieved("R", Tokens(80))), Answer("r"),
            User("q", Selection("S", Tokens(200)), File("F", Tokens(200))),
        };
        var limits = Room(RoomFor(itemsWhole: 1, messages: 3));

        var first = _budgeter.Fit(Instructions, conversation, LargeModel, limits);
        var second = _budgeter.Fit(Instructions, conversation, LargeModel, limits);

        Assert.Equal(first.Report, second.Report);
        Assert.Equal(first.Report.GetHashCode(), second.Report.GetHashCode());
    }

    // What a prompt of `messages` one-word messages and `itemsWhole` 100-token items with one-letter labels takes, with a
    // little over: not enough for another item, and not enough to share in a useful piece.
    private static int RoomFor(int itemsWhole, int messages) =>
        Fixed + messages * (Overhead + 1) + itemsWhole * (Block + 1 + 100) + 10;
}
