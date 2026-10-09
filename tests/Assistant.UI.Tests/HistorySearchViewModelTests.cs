using System.ComponentModel;
using Assistant.Core.Domain;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// Searching the History window: what is typed searches the saved history, the sidebar lists only the conversations that
/// matched, each shows the words that matched, and the open conversation stays open.
/// </summary>
public sealed class HistorySearchViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeHistorySource _source = new();
    private readonly SteppingClock _clock = new(Now);
    private readonly HistoryConversation _weather;
    private readonly HistoryConversation _pasta;
    private readonly HistoryConversation _photos;

    public HistorySearchViewModelTests()
    {
        _weather = new HistoryConversation(Guid.NewGuid(), null, Now.AddMinutes(-10), "Weekend weather") { LatestAnswerText = "Sunny" };
        _pasta = new HistoryConversation(Guid.NewGuid(), null, Now.AddMinutes(-100), "Pasta dinner") { LatestAnswerText = "Cook it" };
        _photos = new HistoryConversation(Guid.NewGuid(), null, Now.AddMinutes(-300), "Photos")
        {
            LatestAnswerText = "Four photos",
            Image = new Assistant.UI.Messages.ImageItem("lake.jpg", @"C:\p\lake.jpg"),
        };
        _source.Listed.AddRange([_weather, _pasta, _photos]);
    }

    private async Task<HistoryViewModel> CreateAsync(TimeSpan? delay = null)
    {
        var history = new HistoryViewModel(_clock, _source, searchDelay: delay ?? TimeSpan.Zero);
        await history.RefreshAsync();
        return history;
    }

    private static HistorySearchHit Hit(HistoryConversation conversation, string? snippet = null, Guid? message = null, params TextMatch[] matches) =>
        new(conversation, snippet, matches, message);

    private static string[] Shown(HistoryViewModel history) =>
        [.. history.Conversations.Where(conversation => conversation.IsShown).Select(conversation => conversation.Title)];

    [Fact]
    public async Task TypingSearchesTheSavedHistory_AndTheSidebarListsOnlyWhatMatched()
    {
        _source.Found = _ => [Hit(_pasta, "Cook the pasta with garlic", null, new TextMatch(9, 5))];
        var history = await CreateAsync();

        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();

        Assert.Equal(["pasta"], _source.Searches);
        Assert.True(history.IsSearching);
        Assert.Equal(["Pasta dinner"], Shown(history));
        Assert.False(history.HasNoResults);
    }

    [Fact]
    public async Task AResultShowsTheWordsThatMatched_InPlaceOfThePreviewAndTheImage()
    {
        var message = Guid.NewGuid();
        _source.Found = _ => [Hit(_photos, "Four photos of a lake", message, new TextMatch(5, 6), new TextMatch(16, 4))];
        var history = await CreateAsync();

        history.SearchText = "photos lake";
        await history.WhenSearchSettledAsync();

        var result = history.Conversations.Single(conversation => conversation.IsShown);
        Assert.Equal("Four photos of a lake", result.Preview);
        Assert.Equal([new TextMatch(5, 6), new TextMatch(16, 4)], result.PreviewMatches);
        Assert.Null(result.Image);
        Assert.Equal(message, result.MatchedMessageId);

        // The conversations that did not match are left out, and keep what they show.
        Assert.Equal("Cook it", history.Conversations.Single(conversation => conversation.Title == "Pasta dinner").Preview);
    }

    [Fact]
    public async Task AConversationFoundByItsTitleAloneShowsItsOwnPreview()
    {
        _source.Found = _ => [Hit(_weather)];
        var history = await CreateAsync();

        history.SearchText = "weekend";
        await history.WhenSearchSettledAsync();

        var result = Assert.Single(history.Conversations, conversation => conversation.IsShown);
        Assert.Equal("Sunny", result.Preview);
        Assert.Empty(result.PreviewMatches);
        Assert.Null(result.MatchedMessageId);
    }

    [Fact]
    public async Task ResultsStayNewestFirst()
    {
        _source.Found = _ => [Hit(_photos), Hit(_weather), Hit(_pasta)];
        var history = await CreateAsync();

        history.SearchText = "a";
        await history.WhenSearchSettledAsync();

        Assert.Equal(["Weekend weather", "Pasta dinner", "Photos"], Shown(history));
        Assert.Equal(["Weekend weather", "Pasta dinner", "Photos"], history.Conversations.Select(conversation => conversation.Title));
    }

    [Fact]
    public async Task ClearingTheSearchListsEveryConversationAgain_EachWithItsOwnPreviewAndImage()
    {
        _source.Found = _ => [Hit(_photos, "Four photos of a lake", null, new TextMatch(5, 6))];
        var history = await CreateAsync();
        history.SearchText = "photos";
        await history.WhenSearchSettledAsync();

        history.SearchText = "";
        await history.WhenSearchSettledAsync();

        Assert.False(history.IsSearching);
        Assert.Equal(["Weekend weather", "Pasta dinner", "Photos"], Shown(history));
        var photos = history.Conversations.Single(conversation => conversation.Title == "Photos");
        Assert.Equal("Four photos", photos.Preview);
        Assert.Empty(photos.PreviewMatches);
        Assert.Equal("lake.jpg", photos.Image?.Name);
        Assert.Null(photos.MatchedMessageId);
        Assert.False(history.HasNoResults);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ? ")]
    [InlineData("\"\"")]
    public async Task WhatHasNoWordsInItDoesNotSearch_AndEveryConversationStaysListed(string text)
    {
        var history = await CreateAsync();

        history.SearchText = text;
        await history.WhenSearchSettledAsync();

        Assert.Empty(_source.Searches);
        Assert.False(history.IsSearching);
        Assert.Equal(3, Shown(history).Length);
    }

    [Fact]
    public async Task ASearchWithNoResultsSaysSo_AndAnotherWithSomeTakesItBack()
    {
        var history = await CreateAsync();
        var changes = new List<string?>();
        history.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        history.SearchText = "nothing";
        await history.WhenSearchSettledAsync();

        Assert.True(history.HasNoResults);
        Assert.Empty(Shown(history));
        Assert.Contains(nameof(HistoryViewModel.HasNoResults), changes);

        _source.Found = _ => [Hit(_pasta)];
        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();

        Assert.False(history.HasNoResults);
        Assert.Equal(["Pasta dinner"], Shown(history));
    }

    [Fact]
    public async Task NoResultsIsNotSaidWhileTheSearchIsStillRunning()
    {
        var release = new TaskCompletionSource();
        _source.BeforeSearch = (_, _) => release.Task;
        var history = await CreateAsync();

        history.SearchText = "slow";

        Assert.True(history.IsSearching);
        Assert.False(history.HasNoResults);
        release.SetResult();
        await history.WhenSearchSettledAsync();
        Assert.True(history.HasNoResults);
    }

    [Fact]
    public async Task ASearchTheUserHasTypedPastNeverShowsItsResults()
    {
        var first = new TaskCompletionSource();
        _source.BeforeSearch = (query, _) => query == "pa" ? first.Task : Task.CompletedTask;
        _source.Found = query => query == "pa" ? [Hit(_pasta)] : [Hit(_photos)];
        var history = await CreateAsync();

        history.SearchText = "pa";
        var stale = history.WhenSearchSettledAsync();
        history.SearchText = "pho";
        await history.WhenSearchSettledAsync();
        first.SetResult();
        await stale;

        Assert.Equal(["Photos"], Shown(history));
    }

    [Fact]
    public async Task TheSearchWaitsForAPauseInTyping_SoItRunsForWhatWasTypedNotForEachLetter()
    {
        _source.Found = _ => [Hit(_pasta)];
        var history = await CreateAsync(TimeSpan.FromMilliseconds(120));

        history.SearchText = "p";
        history.SearchText = "pa";
        history.SearchText = "pas";
        await history.WhenSearchSettledAsync();

        Assert.Equal(["pas"], _source.Searches);
    }

    [Fact]
    public async Task AResultForAConversationTheListDoesNotHaveYetIsAddedToIt()
    {
        var fresh = new HistoryConversation(Guid.NewGuid(), null, Now.AddMinutes(-1), "Brand new chat") { LatestAnswerText = "Hello" };
        _source.Found = _ => [Hit(fresh, "Hello there", null, new TextMatch(0, 5))];
        var history = await CreateAsync();

        history.SearchText = "hello";
        await history.WhenSearchSettledAsync();

        Assert.Contains(history.Conversations, conversation => conversation.Id == fresh.Id);
        Assert.Equal(["Brand new chat"], Shown(history));
        Assert.Equal("Brand new chat", history.Conversations[0].Title);
    }

    [Fact]
    public async Task TheOpenConversationStaysOpenWhenASearchLeavesItOutOfTheSidebar()
    {
        var history = await CreateAsync();
        var open = history.Open(_weather.Id, [new MessageViewModel(MessageRole.User, "Weather?"), new MessageViewModel(MessageRole.Assistant, "Sunny")], Now);
        _source.Found = _ => [Hit(_pasta)];

        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();

        Assert.False(open.IsShown);

        // The list drops what it does not show, and reports that nothing is selected; the workspace does not close.
        history.Selected = null;
        Assert.Same(open, history.Selected);
        Assert.True(history.HasSelection);

        // Once the search is over the open conversation is listed again, and the user can close it as before.
        history.SearchText = "";
        await history.WhenSearchSettledAsync();
        Assert.True(open.IsShown);
        history.Selected = null;
        Assert.False(history.HasSelection);
    }

    [Fact]
    public async Task ChoosingAResultOpensItsConversation_AtTheMessageThatMatched()
    {
        var message = Guid.NewGuid();
        _source.Found = _ => [Hit(_pasta, "Cook the pasta", message, new TextMatch(9, 5))];
        _source.Messages[_pasta.Id] = [new MessageViewModel(MessageRole.User, "Dinner?"), new MessageViewModel(MessageRole.Assistant, "Cook the pasta") { Id = message }];
        var history = await CreateAsync();
        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();

        history.Selected = history.Conversations.Single(conversation => conversation.IsShown);
        await WaitUntil(() => history.Selected!.IsLoaded);

        Assert.Equal(message, history.Selected!.MatchedMessageId);
        Assert.Equal(message, history.Selected.Messages.Single(item => item.Id == message).Id);
    }

    [Fact]
    public async Task TheListChangedEventIsRaisedWhenResultsArriveAndWhenTheSearchEnds()
    {
        _source.Found = _ => [Hit(_pasta)];
        var history = await CreateAsync();
        var raised = 0;
        history.ListedConversationsChanged += (_, _) => raised++;

        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();
        Assert.Equal(1, raised);

        history.SearchText = "pasta dinner";
        await history.WhenSearchSettledAsync();
        Assert.Equal(2, raised);

        history.SearchText = "";
        Assert.Equal(3, raised);

        // Nothing changes when there was no search to end.
        history.SearchText = "  ";
        Assert.Equal(3, raised);
    }

    [Fact]
    public async Task ASearchTheHistoryCannotAnswerShowsNoResults_AndTheNextOneTriesAgain()
    {
        var history = await CreateAsync();
        _source.SearchFailure = new HistoryUnavailableException();

        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();

        Assert.Empty(Shown(history));
        Assert.True(history.HasNoResults);

        _source.SearchFailure = null;
        _source.Found = _ => [Hit(_pasta)];
        history.SearchText = "pasta dinner";
        await history.WhenSearchSettledAsync();
        Assert.Equal(["Pasta dinner"], Shown(history));
    }

    [Fact]
    public async Task ARefreshDuringASearchKeepsTheSearchInForce_AndListsWhatWasSavedSinceOnlyIfItMatches()
    {
        _source.Found = query => query == "pasta" ? [Hit(_pasta)] : [];
        var history = await CreateAsync();
        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();

        var fresh = new HistoryConversation(Guid.NewGuid(), null, Now.AddMinutes(-1), "Unrelated chat");
        _source.Listed.Add(fresh);
        await history.RefreshAsync();
        Assert.False(history.Conversations.Single(conversation => conversation.Id == fresh.Id).IsShown);
        await history.WhenSearchSettledAsync();

        Assert.Equal(["Pasta dinner"], Shown(history));
        Assert.True(history.IsSearching);
    }

    [Fact]
    public async Task ClosingTheSearchClearsItAndPutsTheFieldAway()
    {
        _source.Found = _ => [Hit(_pasta)];
        var history = await CreateAsync();
        history.OpenSearchCommand.Execute(null);
        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();
        Assert.True(history.IsSearchOpen);

        history.CloseSearchCommand.Execute(null);

        Assert.False(history.IsSearchOpen);
        Assert.Equal(string.Empty, history.SearchText);
        Assert.False(history.IsSearching);
        Assert.Equal(3, Shown(history).Length);
    }

    [Fact]
    public async Task OpeningTheSearchShowsTheField_WithoutSearchingYet()
    {
        var history = await CreateAsync();

        history.OpenSearchCommand.Execute(null);

        Assert.True(history.IsSearchOpen);
        Assert.Empty(_source.Searches);
        Assert.False(history.IsSearching);
    }

    [Fact]
    public async Task ANewConversationOpenedFromThePanelDuringASearchIsListedOnlyIfItMatches()
    {
        _source.Found = query => [Hit(_pasta)];
        var history = await CreateAsync();
        history.SearchText = "pasta";
        await history.WhenSearchSettledAsync();

        var opened = history.Open(Guid.NewGuid(), [new MessageViewModel(MessageRole.User, "Something else"), new MessageViewModel(MessageRole.Assistant, "Ok")], Now);
        await history.WhenSearchSettledAsync();

        Assert.False(opened.IsShown);
        Assert.Same(opened, history.Selected);
    }

    [Fact]
    public async Task TheSearchTextRaisesItsChange()
    {
        var history = await CreateAsync();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)history).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        history.SearchText = "x";

        Assert.Contains(nameof(HistoryViewModel.SearchText), raised);
        Assert.Contains(nameof(HistoryViewModel.IsSearching), raised);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }
}
