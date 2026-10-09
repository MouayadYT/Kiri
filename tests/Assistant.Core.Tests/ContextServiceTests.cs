using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Domain;
using Xunit;
using static Assistant.Core.Tests.BudgetTestData;

namespace Assistant.Core.Tests;

/// <summary>The central context service: what it accepts, merges, ranks, remembers and forgets.</summary>
public sealed class ContextServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestClock _clock = new(Start);
    private readonly Guid _conversation = Guid.NewGuid();
    private readonly ContextService _service;

    public ContextServiceTests()
    {
        _service = new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()), _clock);
    }

    // ---- Accepting ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ContextItemType.File, ContextSource.UserSelected)]
    [InlineData(ContextItemType.Selection, ContextSource.CurrentScreen)]
    [InlineData(ContextItemType.Screenshot, ContextSource.CurrentScreen)]
    [InlineData(ContextItemType.SearchResults, ContextSource.Retrieval)]
    public void Items_OfEveryKindAreAccepted_AndKnowHowTheyCameIn(ContextItemType type, ContextSource source)
    {
        var item = new ContextItem(Guid.NewGuid(), type, "x") { Text = "some text" };

        var result = _service.Add(_conversation, item, "test");

        Assert.Equal(ContextAddOutcome.Added, result.Outcome);
        Assert.Equal(source, result.Item.Source);
        Assert.Equal(item.Id, result.Item.Id);
        Assert.Equal([result.Item], _service.PendingItems(_conversation));
    }

    [Fact]
    public void AToolsItem_KeepsTheSourceItSays()
    {
        var item = File("Result", "rows") with { Source = ContextSource.Tool };

        var added = _service.Add(_conversation, item, "tool");

        Assert.Equal(ContextSource.Tool, added.Item.Source);
        Assert.Equal(ContextPriority.Retrieved, Assert.Single(_service.GetContext(_conversation).Pending).Priority);
    }

    [Fact]
    public void WhoSuppliedAnItem_AndWhen_IsRemembered()
    {
        var item = Selection("S", "text");
        _service.Add(_conversation, item, "hotkey");

        var entry = Assert.Single(_service.GetContext(_conversation).Pending);

        Assert.Equal([new ContextProvenance(ContextSource.CurrentScreen, "hotkey", Start)], entry.Provenance);
        Assert.False(entry.IsSent);
        Assert.Null(entry.LastFit);
    }

    [Fact]
    public void AnOriginThatIsEmptyIsRefused_AndALongOneIsCut()
    {
        Assert.Throws<ArgumentException>(() => _service.Add(_conversation, File("F", "t"), " "));

        _service.Add(_conversation, File("F", "t"), new string('x', 100));

        Assert.Equal(40, Assert.Single(Assert.Single(_service.GetContext(_conversation).Pending).Provenance).Origin.Length);
    }

    [Fact]
    public void EachConversation_HasContextOfItsOwn()
    {
        var other = Guid.NewGuid();
        _service.Add(_conversation, File("F", "t"), "composer");

        Assert.Empty(_service.PendingItems(other));
        Assert.Same(ConversationContext.Empty, _service.GetContext(other));
    }

    // ---- Duplicates -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheSameTextFromTwoSources_IsOneItemWithTwoProvenances()
    {
        var first = Selection("From the hotkey", "same words");
        var second = Selection("From the browser", "same words");

        _service.Add(_conversation, first, "hotkey");
        _clock.Advance(TimeSpan.FromSeconds(5));
        var merged = _service.Add(_conversation, second, "browser");

        Assert.Equal(ContextAddOutcome.Merged, merged.Outcome);
        Assert.Equal(first.Id, merged.Item.Id);
        var entry = Assert.Single(_service.GetContext(_conversation).Pending);
        Assert.Equal(["hotkey", "browser"], entry.Provenance.Select(provenance => provenance.Origin));
        Assert.Equal(Start.AddSeconds(5), entry.Provenance[1].AddedAt);
        Assert.Single(_service.PendingItems(_conversation));
    }

    [Fact]
    public void TextsThatDifferOnlyInLineEndsAndOuterSpace_AreTheSameText()
    {
        _service.Add(_conversation, Selection("A", "one\r\ntwo"), "x");

        var again = _service.Add(_conversation, Selection("B", "  one\ntwo\n"), "y");

        Assert.Equal(ContextAddOutcome.Merged, again.Outcome);
    }

    [Fact]
    public void TheSameFileWithOtherText_IsAnotherItem_AndOtherFilesAreNeverMerged()
    {
        _service.Add(_conversation, File("A", "passages one") with { FilePath = @"C:\docs\a.txt" }, "composer");

        var otherText = _service.Add(_conversation, File("A", "passages two") with { FilePath = @"C:\docs\a.txt" }, "composer");
        var otherFile = _service.Add(_conversation, File("B", "passages one") with { FilePath = @"C:\docs\b.txt" }, "composer");
        var sameFileAnySpelling = _service.Add(_conversation, File("A", "passages one") with { FilePath = "c:/DOCS/a.txt" }, "explorer");

        Assert.Equal(ContextAddOutcome.Added, otherText.Outcome);
        Assert.Equal(ContextAddOutcome.Added, otherFile.Outcome);
        Assert.Equal(ContextAddOutcome.Merged, sameFileAnySpelling.Outcome);
        Assert.Equal(3, _service.PendingItems(_conversation).Count);
    }

    [Fact]
    public void TheSameTextOfAnotherKind_IsAnotherItem()
    {
        _service.Add(_conversation, Selection("S", "words"), "hotkey");

        var page = _service.Add(
            _conversation, new ContextItem(Guid.NewGuid(), ContextItemType.Page, "P") { Text = "words" }, "browser");

        Assert.Equal(ContextAddOutcome.Added, page.Outcome);
    }

    [Fact]
    public void TheSameImageTwice_IsOneItem()
    {
        _service.Add(_conversation, Screenshot("A", null, [1, 2, 3]), "capture");

        var again = _service.Add(_conversation, Screenshot("B", null, [1, 2, 3]), "capture");
        var other = _service.Add(_conversation, Screenshot("C", null, [1, 2, 4]), "capture");

        Assert.Equal(ContextAddOutcome.Merged, again.Outcome);
        Assert.Equal(ContextAddOutcome.Added, other.Outcome);
    }

    [Fact]
    public void ItemsWithNothingToCompare_AreNeverTakenForEachOther()
    {
        var first = _service.Add(_conversation, File("A", null), "composer");
        var second = _service.Add(_conversation, File("A", null), "composer");

        Assert.Equal(ContextAddOutcome.Added, first.Outcome);
        Assert.Equal(ContextAddOutcome.Added, second.Outcome);
    }

    [Fact]
    public void TheSameItemTwice_IsTheItemOnce()
    {
        var item = File("A", null);
        _service.Add(_conversation, item, "composer");

        Assert.Equal(ContextAddOutcome.Merged, _service.Add(_conversation, item, "composer").Outcome);
        Assert.Single(_service.PendingItems(_conversation));
    }

    [Fact]
    public void WhenTwoWaysInDiffer_TheItemRanksByTheBetterOne()
    {
        _service.Add(_conversation, File("Found", "same") with { Source = ContextSource.Retrieval }, "search");

        var merged = _service.Add(_conversation, File("Picked", "same"), "composer");

        Assert.Equal(ContextAddOutcome.Merged, merged.Outcome);
        Assert.Equal(ContextSource.UserSelected, merged.Item.Source);
        Assert.Equal(ContextPriority.Selected, Assert.Single(_service.GetContext(_conversation).Pending).Priority);
    }

    [Fact]
    public void ABetterRankedItem_IsNotLoweredByAWorseOne()
    {
        _service.Add(_conversation, File("Picked", "same"), "composer");

        var merged = _service.Add(_conversation, File("Found", "same") with { Source = ContextSource.Retrieval }, "search");

        Assert.Equal(ContextSource.UserSelected, merged.Item.Source);
    }

    // ---- Waiting, sending, remembering ----------------------------------------------------------------------------

    [Fact]
    public void WhatWaits_IsLaidOutByRank_ThenInTheOrderItCame()
    {
        var found = Retrieved("Found", "a");
        var selection = Selection("Selection", "b");
        var file = File("File", "c");
        var otherFile = File("Other file", "d");
        var screenshot = Screenshot("Screenshot", "e");
        foreach (var item in new[] { found, selection, file, screenshot, otherFile })
        {
            _service.Add(_conversation, item, "test");
        }

        Assert.Equal(
            [file.Id, otherFile.Id, selection.Id, screenshot.Id, found.Id],
            _service.PendingItems(_conversation).Select(item => item.Id));
    }

    [Fact]
    public void WhatWaits_StaysUntilItIsSent_SoAQuestionThatNeverStartedLosesNothing()
    {
        var item = File("F", "text");
        _service.Add(_conversation, item, "composer");

        _ = _service.PendingItems(_conversation);

        Assert.Single(_service.PendingItems(_conversation));
    }

    [Fact]
    public void AnItemCanBeTakenBack_WhileItWaits()
    {
        var item = File("F", "text");
        _service.Add(_conversation, item, "composer");

        Assert.True(_service.Remove(_conversation, item.Id));
        Assert.False(_service.Remove(_conversation, item.Id));
        Assert.Empty(_service.PendingItems(_conversation));
    }

    [Fact]
    public void WhenSent_TheItemStopsWaiting_AndKeepsOnlyItsDescriptor()
    {
        var image = Screenshot("Shot", "ocr words", [9, 9, 9]);
        _service.Add(_conversation, image, "capture");
        var pending = _service.PendingItems(_conversation);

        _service.Commit(_conversation, pending);

        Assert.Empty(_service.PendingItems(_conversation));
        var entry = Assert.Single(_service.GetContext(_conversation).Earlier);
        Assert.True(entry.IsSent);
        Assert.Null(entry.Item.Text);
        Assert.True(entry.Item.ImageData.IsEmpty);
        Assert.Equal(("Shot", ContextItemType.Screenshot), (entry.Item.DisplayName, entry.Item.Type));
        Assert.Equal(ContextPriority.EarlierAttached, entry.Priority);
        Assert.False(_service.Remove(_conversation, image.Id));

        // The caller's own item still has its content: only the service's copy was stripped.
        Assert.Equal("ocr words", pending[0].Text);
    }

    [Fact]
    public void WhatTheConversationAlreadyCarries_IsNotAddedAgain_ButItsProvenanceGrows()
    {
        _service.Add(_conversation, Selection("S", "same words"), "hotkey");
        _service.Commit(_conversation, _service.PendingItems(_conversation));

        var again = _service.Add(_conversation, Selection("S2", "same words"), "browser");

        Assert.Equal(ContextAddOutcome.AlreadyInConversation, again.Outcome);
        Assert.Empty(_service.PendingItems(_conversation));
        var entry = Assert.Single(_service.GetContext(_conversation).All);
        Assert.Equal(["hotkey", "browser"], entry.Provenance.Select(provenance => provenance.Origin));
    }

    [Fact]
    public void EarlierContext_IsListedByRank_ThenTheNewestQuestionFirst()
    {
        var olderFile = File("Older", "a");
        var newerFile = File("Newer", "b");
        var retrieved = Retrieved("Found", "c");
        _service.Add(_conversation, olderFile, "t");
        _service.Commit(_conversation, _service.PendingItems(_conversation));
        _service.Add(_conversation, retrieved, "t");
        _service.Add(_conversation, newerFile, "t");
        _service.Commit(_conversation, _service.PendingItems(_conversation));

        Assert.Equal(
            [newerFile.Id, olderFile.Id, retrieved.Id],
            _service.GetContext(_conversation).Earlier.Select(entry => entry.Item.Id));
    }

    [Fact]
    public void Forgetting_EmptiesAConversationsContext()
    {
        _service.Add(_conversation, File("F", "t"), "composer");

        _service.Forget(_conversation);

        Assert.Same(ConversationContext.Empty, _service.GetContext(_conversation));
        Assert.Equal(ContextAddOutcome.Added, _service.Add(_conversation, File("F", "t"), "composer").Outcome);
    }

    // ---- Bounds ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TooManyWaitingItems_AreRefused_AndTheRestKeepWaiting()
    {
        for (var index = 0; index < ContextService.MaxPendingItems; index++)
        {
            Assert.Equal(ContextAddOutcome.Added, _service.Add(_conversation, File($"F{index}", $"text {index}"), "t").Outcome);
        }

        var extra = File("Extra", "one more");
        var result = _service.Add(_conversation, extra, "t");

        Assert.Equal(ContextAddOutcome.Rejected, result.Outcome);
        Assert.Same(extra, result.Item);
        Assert.Equal(ContextService.MaxPendingItems, _service.PendingItems(_conversation).Count);
    }

    [Fact]
    public void OnlyTheConversationsTouchedLastAreRemembered()
    {
        var first = Guid.NewGuid();
        _service.Add(first, File("F", "t"), "composer");
        for (var index = 0; index < ContextService.MaxConversations; index++)
        {
            _service.Add(Guid.NewGuid(), File($"F{index}", "t"), "composer");
        }

        Assert.Empty(_service.PendingItems(first));
    }

    [Fact]
    public void TheOldestSentItems_AreForgottenFirst_WhenAConversationRemembersTooMany()
    {
        Guid firstSent = Guid.Empty;
        for (var index = 0; index < ContextService.MaxEntries + 10; index++)
        {
            var item = File($"F{index}", $"text {index}");
            firstSent = index == 0 ? item.Id : firstSent;
            _service.Add(_conversation, item, "t");
            _service.Commit(_conversation, _service.PendingItems(_conversation));
        }

        var context = _service.GetContext(_conversation);

        Assert.Equal(ContextService.MaxEntries, context.Earlier.Count);
        Assert.DoesNotContain(context.Earlier, entry => entry.Item.Id == firstSent);
    }

    // ---- Fitting --------------------------------------------------------------------------------------------------

    [Fact]
    public void Preparing_FitsThroughTheBudgeter_AndRemembersWhatDidNotFit()
    {
        var big = File("Big", Tokens(2000));
        var small = Selection("Small", Tokens(20));

        // The file is the user's own pick, so it is served first, and the little the window has left goes to it.
        var added = new[] { big, small };
        foreach (var item in added)
        {
            _service.Add(_conversation, item, "t");
        }

        var message = User("q", [.. _service.PendingItems(_conversation)]);
        var fit = _service.Prepare(_conversation, Sized("rules", 4), [message], LargeModel, Room(600));

        Assert.True(fit.Report.AnythingTrimmed);
        var context = _service.GetContext(_conversation);
        Assert.Equal(ContextFate.Shortened, context.Pending.Single(entry => entry.Item.Id == big.Id).LastFit);
        Assert.Equal(ContextFate.LeftOut, context.Pending.Single(entry => entry.Item.Id == small.Id).LastFit);
        Assert.Equal(1, context.ShortenedCount);
        Assert.Equal(1, context.LeftOutCount);
    }

    [Fact]
    public void WhatTheLastPromptLeftOut_IsReported_AndCanBeAddedAgain()
    {
        var first = File("Big", Tokens(2000));
        _service.Add(_conversation, first, "t");
        var message = User("q", [.. _service.PendingItems(_conversation)]);
        _service.Prepare(_conversation, Sized("rules", 4), [message], LargeModel, Room(Fixed(40)));
        _service.Commit(_conversation, message.ContextItems);

        Assert.Equal(1, _service.GetContext(_conversation).LeftOutCount);

        // What the model did not get is worth supplying again, instead of being taken for something it already has.
        var again = _service.Add(_conversation, File("Big", Tokens(2000)), "t");
        Assert.Equal(ContextAddOutcome.Added, again.Outcome);
    }

    [Fact]
    public void Preparing_ForAConversationItHoldsNothingFor_JustFits()
    {
        var message = User("q", File("F", Tokens(5)));

        var fit = _service.Prepare(Guid.NewGuid(), Sized("rules", 4), [message], LargeModel, Room(1000));

        Assert.False(fit.Report.AnythingTrimmed);
        Assert.Empty(_service.GetContext(Guid.Empty).All);
    }

    // ---- Privacy --------------------------------------------------------------------------------------------------

    [Fact]
    public void NothingTheServiceReturns_PrintsContent()
    {
        var secret = Selection("PRIVATE-LABEL", "PRIVATE-TEXT");
        var result = _service.Add(_conversation, secret, "t");
        var context = _service.GetContext(_conversation);

        foreach (var text in new[] { result.ToString(), context.ToString(), context.Pending[0].ToString() })
        {
            Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        }
    }

    private static int Fixed(int rest) => ContextBudgeter.RequestOverheadTokens + 4 + ContextBudgeter.MessageOverheadTokens + 1 + rest;
}
