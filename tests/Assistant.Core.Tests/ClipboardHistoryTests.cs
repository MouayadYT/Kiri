using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Clipboard;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class ClipboardHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static ClipboardHistory History(TestClock? clock = null, ClipboardHistoryLimits? limits = null, bool enabled = true)
    {
        var history = new ClipboardHistory(limits, clock ?? new TestClock(Now));
        history.SetEnabled(enabled);
        return history;
    }

    [Fact]
    public void ItIsOffUntilTheUserAllowsItAndKeepsNothingWhileItIsOff()
    {
        var history = new ClipboardHistory(clock: new TestClock(Now));

        Assert.False(history.IsEnabled);
        Assert.False(history.Add("something copied"));
        Assert.Empty(history.Items);
    }

    [Fact]
    public void ItKeepsWhatWasCopiedNewestFirst()
    {
        var clock = new TestClock(Now);
        var history = History(clock);

        Assert.True(history.Add("first"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(history.Add("second"));

        Assert.Equal(["second", "first"], history.Items.Select(item => item.Text));
        Assert.Equal([Now + TimeSpan.FromMinutes(1), Now], history.Items.Select(item => item.CopiedAt));
        var found = history.Find(history.Items[1].Id);
        Assert.Equal("first", found!.Text);
        Assert.Null(history.Find("nothing"));
    }

    [Fact]
    public void TheSameTextAgainMovesToTheFrontWithItsIdAndIsNotKeptTwice()
    {
        var clock = new TestClock(Now);
        var history = History(clock);
        history.Add("alpha");
        history.Add("beta");
        var id = history.Items[1].Id;

        clock.Advance(TimeSpan.FromMinutes(5));
        history.Add("alpha");

        Assert.Equal(["alpha", "beta"], history.Items.Select(item => item.Text));
        Assert.Equal(id, history.Items[0].Id);
        Assert.Equal(Now + TimeSpan.FromMinutes(5), history.Items[0].CopiedAt);

        // Not the same text when the case differs.
        history.Add("Alpha");
        Assert.Equal(3, history.Items.Count);
    }

    [Fact]
    public void OnlyTheLastFewItemsAreKept()
    {
        var history = History(limits: new ClipboardHistoryLimits(3, 100, TimeSpan.FromDays(1)));

        foreach (var text in new[] { "one", "two", "three", "four", "five" })
        {
            history.Add(text);
        }

        Assert.Equal(["five", "four", "three"], history.Items.Select(item => item.Text));
    }

    [Fact]
    public void TheDefaultLimitsAreTwentyItemsOfFourThousandCharactersForADay()
    {
        var limits = ClipboardHistoryLimits.Default;

        Assert.Equal(20, limits.MaxItems);
        Assert.Equal(4000, limits.MaxItemCharacters);
        Assert.Equal(TimeSpan.FromDays(1), limits.MaxAge);
        Assert.Equal(20, new ClipboardHistory().Limits.MaxItems);
    }

    [Fact]
    public void TextThatIsEmptyBlankOrTooLongIsNotKeptAndIsNeverCut()
    {
        var history = History(limits: new ClipboardHistoryLimits(20, 10, TimeSpan.FromDays(1)));

        Assert.False(history.Add(""));
        Assert.False(history.Add("   \r\n\t"));
        Assert.False(history.Add("12345678901"));
        Assert.True(history.Add("1234567890"));

        Assert.Equal(["1234567890"], history.Items.Select(item => item.Text));
    }

    [Fact]
    public void AnItemIsForgottenWhenItIsOlderThanTheLimit()
    {
        var clock = new TestClock(Now);
        var history = History(clock, new ClipboardHistoryLimits(20, 100, TimeSpan.FromHours(2)));
        history.Add("old");
        clock.Advance(TimeSpan.FromHours(1.5));
        history.Add("newer");

        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(["newer"], history.Items.Select(item => item.Text));
    }

    [Fact]
    public void TurningItOffForgetsEverythingAtOnce()
    {
        var history = History();
        history.Add("secret one");
        history.Add("secret two");
        var changes = 0;
        history.Changed += (_, _) => changes++;

        history.SetEnabled(false);

        Assert.False(history.IsEnabled);
        Assert.Empty(history.Items);
        Assert.Equal(1, changes);
        Assert.False(history.Add("later"));

        // Turned on again, it starts with nothing: what was copied before is not brought back.
        history.SetEnabled(true);
        Assert.Empty(history.Items);

        // Saying the same thing again changes nothing.
        history.SetEnabled(true);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ClearAndRemoveForgetAndSayWhenTheyDo()
    {
        var history = History();
        history.Add("one");
        history.Add("two");
        var changes = 0;
        history.Changed += (_, _) => changes++;

        Assert.True(history.Remove(history.Items[0].Id));
        Assert.False(history.Remove("nothing"));
        Assert.Equal(["one"], history.Items.Select(item => item.Text));
        Assert.Equal(1, changes);

        history.Clear();
        history.Clear();

        Assert.Empty(history.Items);
        Assert.True(history.IsEnabled);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void AddingFromManyThreadsKeepsToTheLimit()
    {
        var history = History(limits: new ClipboardHistoryLimits(10, 100, TimeSpan.FromDays(1)));

        Parallel.For(0, 200, i => history.Add("text " + (i % 25)));

        Assert.True(history.Items.Count <= 10);
        Assert.Equal(history.Items.Count, history.Items.Select(item => item.Text).Distinct().Count());
    }

    [Fact]
    public void TheLimitsMustBeSensible()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClipboardHistory(new ClipboardHistoryLimits(0, 10, TimeSpan.FromDays(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClipboardHistory(new ClipboardHistoryLimits(51, 10, TimeSpan.FromDays(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClipboardHistory(new ClipboardHistoryLimits(5, 0, TimeSpan.FromDays(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClipboardHistory(new ClipboardHistoryLimits(5, 10, TimeSpan.Zero)));
    }

    [Fact]
    public void WhatWasCopiedIsNeverPrinted()
    {
        var history = History();
        history.Add("my bank password is hunter2");

        var item = history.Items[0];

        Assert.DoesNotContain("hunter2", item.ToString());
        Assert.DoesNotContain("password", item.ToString());
        Assert.DoesNotContain("hunter2", new ClipboardCopiedEventArgs("hunter2").ToString());
    }

    // ---- The provider ----

    private static ClipboardQuickSearchProvider Provider(
        IClipboardHistory history, bool allowed = true, bool withPolicy = true) =>
        new(history, withPolicy ? new Policy(allowed) : null);

    private static Task<IReadOnlyList<QuickSearchResult>> Search(ClipboardQuickSearchProvider provider, string query, int max = 8) =>
        provider.SearchAsync(new QuickSearchRequest(query) { MaxResults = max }, CancellationToken.None);

    [Fact]
    public async Task NothingIsListedWhileTheHistoryIsOffOrTheUserHasNotAllowedIt()
    {
        var off = History(enabled: false);
        off.Add("x");
        var on = History();
        on.Add("copied text");

        Assert.Empty(await Search(Provider(off), ""));
        Assert.Empty(await Search(Provider(on, allowed: false), ""));
        Assert.Empty(await Search(Provider(on, withPolicy: false), ""));
        Assert.Single(await Search(Provider(on), ""));
    }

    [Fact]
    public async Task WithNothingTypedTheNewestItemsAreListedAsRowsThatCopyThemBack()
    {
        var clock = new TestClock(Now);
        var history = History(clock);
        history.Add("first copy");
        clock.Advance(TimeSpan.FromMinutes(3));
        history.Add("second copy");

        var results = await Search(Provider(history), "");

        Assert.Equal(["second copy", "first copy"], results.Select(result => result.Title));
        var newest = results[0];
        Assert.Equal("clip:" + history.Items[0].Id, newest.Id);
        Assert.Equal(QuickSearchResultType.Clipboard, newest.ResultType);
        Assert.Equal(new QuickSearchAction(QuickSearchActionKind.CopyClipboardItem, "Copy", history.Items[0].Id), newest.Primary);
        Assert.Equal(
            [QuickSearchActionKind.AttachClipboardItem, QuickSearchActionKind.RemoveClipboardItem],
            newest.Alternates.Select(action => action.Kind));
        Assert.Equal(["Attach to conversation", "Remove from history"], newest.Alternates.Select(action => action.Title));
        Assert.Equal(Now + TimeSpan.FromMinutes(3), newest.When);
        Assert.Equal(QuickSearchIconKind.Clipboard, newest.Icon.Kind);
        Assert.True(newest.Relevance > results[1].Relevance);
    }

    [Fact]
    public async Task TypedWordsAreLookedForAnywhereInTheWholeTextWithoutRegardToCaseOrAccents()
    {
        var history = History();
        history.Add("Meeting at the café on Friday, bring the quarterly report");
        history.Add("Shopping list: eggs, milk");
        var provider = Provider(history);

        Assert.Equal(["Shopping list: eggs, milk"], (await Search(provider, "MILK")).Select(result => result.Title));
        Assert.Single(await Search(provider, "cafe friday"));
        Assert.Single(await Search(provider, "quarterly"));
        Assert.Empty(await Search(provider, "milk quarterly"));
        Assert.Empty(await Search(provider, "zzz"));
    }

    [Fact]
    public async Task ARowShowsTheStartOfTheTextOnOneLine()
    {
        var history = History();
        history.Add("line one\r\n   line two\t\tline three");
        history.Add(new string('a', 300));

        var results = await Search(Provider(history), "");

        Assert.Equal(new string('a', ClipboardQuickSearchProvider.PreviewLength) + "...", results[0].Title);
        Assert.Equal("line one line two line three", results[1].Title);
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("a\r\nb\nc", "a b c")]
    [InlineData("", "")]
    [InlineData("\u0001\u0002 x", "x")]
    public void ThePreviewIsOneTrimmedLine(string text, string expected) => Assert.Equal(expected, ClipboardQuickSearchProvider.Preview(text));

    [Fact]
    public async Task OnlyAsManyAsAreAskedForAreListed()
    {
        var history = History();
        for (var i = 0; i < 10; i++)
        {
            history.Add("item " + i);
        }

        Assert.Equal(3, (await Search(Provider(history), "", max: 3)).Count);
    }

    [Fact]
    public async Task AWordLaterInTheTextStillFindsItWhenRanked()
    {
        var history = History();
        history.Add("a long sentence that only mentions the invoice at its very end");
        var provider = Provider(history);
        var results = await Search(provider, "invoice");

        var ranking = new QuickSearchRanker([provider]).Rank("invoice", results, Now);

        Assert.Single(ranking.Ordered);
        Assert.NotEqual(QuickSearchMatchKind.None, ranking.Ordered[0].Match.Kind);
    }

    [Fact]
    public void ANullHistoryIsRefused() => Assert.Throws<ArgumentNullException>(() => new ClipboardQuickSearchProvider(null!));

    private sealed class Policy(bool allowed) : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(
                capability,
                allowed && capability == PermissionCapability.ClipboardHistory ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
    }
}
