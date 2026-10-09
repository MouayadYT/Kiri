using Assistant.Core.Domain;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The History window's list comes from the saved history: listed without messages, brought up to date when the window
/// is shown, and each conversation's messages read when it is opened.
/// </summary>
public sealed class SavedHistoryViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeHistorySource _source = new();
    private readonly SteppingClock _clock = new(Now);

    private HistoryViewModel Create(IAnswerProvider? answers = null) => new(_clock, _source, answers);

    private HistoryConversation Saved(string title, int minutesAgo, string? answer = null, ImageItem? image = null) =>
        new(Guid.NewGuid(), null, Now.AddMinutes(-minutesAgo), title) { LatestAnswerText = answer, Image = image };

    private static MessageViewModel[] Turn(string question, string answer) =>
        [new(MessageRole.User, question), new(MessageRole.Assistant, answer)];

    [Fact]
    public async Task TheListComesFromTheSavedHistory_NewestFirst_WithoutTheirMessages()
    {
        var old = Saved("Old", 600, "Old answer");
        var newest = Saved("Newest", 1, "Newest answer");
        var middle = Saved("Middle", 120, "Middle answer");
        _source.Listed.AddRange([old, newest, middle]);
        var history = Create();

        await history.RefreshAsync();

        Assert.Equal(["Newest", "Middle", "Old"], history.Conversations.Select(conversation => conversation.Title));
        Assert.Equal([newest.Id, middle.Id, old.Id], history.Conversations.Select(conversation => conversation.Id));
        Assert.All(history.Conversations, conversation =>
        {
            Assert.False(conversation.IsLoaded);
            Assert.Empty(conversation.Messages);
        });
        Assert.Null(history.Selected);
    }

    [Fact]
    public async Task ACardShowsTheLatestAnswersProseOnOneLine_WithoutItsMarkdownMarks()
    {
        _source.Listed.Add(Saved("Weather", 5, "# Weekend\n\nSaturday looks **sunny** with a `high` of 22°.\n\n- Sunday: light rain"));
        var history = Create();

        await history.RefreshAsync();

        Assert.Equal("Weekend Saturday looks sunny with a high of 22°. Sunday: light rain", history.Conversations[0].Preview);
    }

    [Fact]
    public async Task ACardShowsTheImageMostRecentlyAttached_InPlaceOfThePreview()
    {
        var image = new ImageItem("lake.jpg", @"C:\p\lake.jpg");
        _source.Listed.Add(Saved("Photos", 5, "A lake", image));
        var history = Create();

        await history.RefreshAsync();

        Assert.Same(image, history.Conversations[0].Image);
    }

    [Fact]
    public async Task ACardShowsTheTimeAndSectionTheConversationLastChangedIn()
    {
        _source.Listed.AddRange([Saved("Today", 5), Saved("Last week", 3 * 24 * 60), Saved("Long ago", 40 * 24 * 60)]);
        var history = Create();

        await history.RefreshAsync();

        var sections = history.Conversations.Select(conversation => conversation.Section).ToArray();
        Assert.Equal(HistorySection.Today, sections[0]);
        Assert.Equal(HistorySection.PreviousSevenDays, sections[1]);

        // Beyond thirty days a conversation is under the month it changed in, which comes after the spans.
        Assert.True(sections[2].Order > HistorySection.PreviousThirtyDays.Order);
    }

    [Fact]
    public void AConversationWithNoTitleGetsAProvisionalOneFromWhatTheUserFirstAsked()
    {
        var request = string.Join(' ', Enumerable.Repeat("summarize", 40));
        var conversation = new HistoryConversationViewModel(Guid.NewGuid(), Turn(request, "Done."), Now, _clock);

        Assert.True(conversation.Title.Length <= 80, conversation.Title);
        Assert.EndsWith("…", conversation.Title, StringComparison.Ordinal);
        Assert.StartsWith("summarize summarize", conversation.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ATitleTheConversationWasGivenIsKept()
    {
        var conversation = new HistoryConversationViewModel(Guid.NewGuid(), Turn("What is the weather?", "Sunny."), Now, _clock, "  Weekend\nplans ");

        Assert.Equal("Weekend plans", conversation.Title);
    }

    [Fact]
    public async Task OpeningAConversationReadsItsMessages_AndTheyAppearOnceRead()
    {
        var saved = Saved("Trip", 5, "Book it");
        _source.Listed.Add(saved);
        var release = new TaskCompletionSource();
        _source.BeforeLoad = (_, _) => release.Task;
        _source.Messages[saved.Id] = Turn("Plan a trip", "Book it");
        var history = Create();
        await history.RefreshAsync();
        var loaded = new List<HistoryConversationViewModel>();
        history.MessagesLoaded += (_, conversation) => loaded.Add(conversation);

        history.Selected = history.Conversations[0];

        Assert.True(history.Selected.IsLoading);
        Assert.False(history.Selected.IsLoaded);
        Assert.Empty(history.Selected.Messages);
        release.SetResult();
        await WaitUntil(() => history.Selected.IsLoaded);
        Assert.False(history.Selected.IsLoading);
        Assert.Equal(["Plan a trip", "Book it"], history.Selected.Messages.Select(message => message.Text));
        Assert.Equal([history.Selected], loaded);
        Assert.Equal([saved.Id], _source.Loads);
    }

    [Fact]
    public async Task OpeningItAgainWhileItLoadsDoesNotReadItTwice_AndOnceLoadedItIsNotReadAgain()
    {
        var saved = Saved("Trip", 5);
        _source.Listed.Add(saved);
        var release = new TaskCompletionSource();
        _source.BeforeLoad = (_, _) => release.Task;
        _source.Messages[saved.Id] = Turn("Plan a trip", "Book it");
        var history = Create();
        await history.RefreshAsync();
        var conversation = history.Conversations[0];

        history.Selected = conversation;
        history.Selected = null;
        history.Selected = conversation;
        release.SetResult();
        await WaitUntil(() => conversation.IsLoaded);
        history.Selected = null;
        history.Selected = conversation;

        Assert.Single(_source.Loads);
    }

    [Fact]
    public async Task TheComposerWaitsForTheMessagesOfAConversationThatIsStillLoading()
    {
        var saved = Saved("Trip", 5);
        _source.Listed.Add(saved);
        var release = new TaskCompletionSource();
        _source.BeforeLoad = (_, _) => release.Task;
        _source.Messages[saved.Id] = Turn("Plan a trip", "Book it");
        var answers = new ScriptedAnswers { Respond = (_, _, show, _) => { show(new MessageViewModel(MessageRole.Assistant, "Ok")); return Task.CompletedTask; } };
        var history = Create(answers);
        await history.RefreshAsync();
        history.Selected = history.Conversations[0];
        history.Draft = "Another question";

        Assert.False(history.SendCommand.CanExecute(null));
        Assert.False(history.Send("Another question"));
        Assert.Empty(answers.Asked);

        release.SetResult();
        await WaitUntil(() => history.Selected!.IsLoaded);
        Assert.True(history.SendCommand.CanExecute(null));
        Assert.True(history.Send("Another question"));
        await WaitUntil(() => history.Selected!.Messages.Count == 4);
        Assert.Equal(["Plan a trip", "Book it", "Another question", "Ok"], history.Selected!.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task AConversationThatCannotBeReadStaysUnloaded_AndOpeningItAgainTriesAgain()
    {
        var saved = Saved("Trip", 5);
        _source.Listed.Add(saved);
        _source.LoadFailure = new HistoryUnavailableException();
        _source.Messages[saved.Id] = Turn("Plan a trip", "Book it");
        var history = Create();
        await history.RefreshAsync();
        var conversation = history.Conversations[0];

        history.Selected = conversation;
        await WaitUntil(() => !conversation.IsLoading);
        Assert.False(conversation.IsLoaded);

        _source.LoadFailure = null;
        history.Selected = null;
        history.Selected = conversation;
        await WaitUntil(() => conversation.IsLoaded);
        Assert.Equal(2, _source.Loads.Count);
    }

    [Fact]
    public async Task AConversationSavedButGoneFromTheHistoryOpensEmpty()
    {
        var saved = Saved("Gone", 5);
        _source.Listed.Add(saved);
        var history = Create();
        await history.RefreshAsync();

        history.Selected = history.Conversations[0];
        await WaitUntil(() => history.Selected!.IsLoaded);

        Assert.Empty(history.Selected!.Messages);
    }

    [Fact]
    public async Task OpeningAListedConversationFromThePanelUsesTheMessagesGiven_WithoutReadingThem()
    {
        var saved = Saved("Trip", 5, "Old answer");
        _source.Listed.Add(saved);
        var history = Create();
        await history.RefreshAsync();

        var opened = history.Open(saved.Id, Turn("Plan a trip", "New answer"), Now);

        Assert.Same(history.Conversations[0], opened);
        Assert.True(opened.IsLoaded);
        Assert.Equal("New answer", opened.Preview);
        Assert.Same(opened, history.Selected);
        Assert.Empty(_source.Loads);
    }

    [Fact]
    public async Task AConversationOpenedFromThePanelWhileItsMessagesWereLoadingKeepsTheMessagesGiven()
    {
        var saved = Saved("Trip", 5);
        _source.Listed.Add(saved);
        var release = new TaskCompletionSource();
        _source.BeforeLoad = (_, _) => release.Task;
        _source.Messages[saved.Id] = Turn("Old question", "Old answer");
        var history = Create();
        await history.RefreshAsync();
        history.Selected = history.Conversations[0];

        history.Open(saved.Id, Turn("Question from the panel", "Answer from the panel"), Now);
        release.SetResult();
        await WaitUntil(() => !history.Conversations[0].IsLoading);

        Assert.Equal(["Question from the panel", "Answer from the panel"], history.Conversations[0].Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task RefreshingAddsWhatWasSavedSince_UpdatesWhatIsListed_AndKeepsWhatOnlyTheWindowHas()
    {
        var kept = Saved("Kept", 500, "Kept answer");
        var renamed = Saved("Before", 100, "Before answer");
        _source.Listed.AddRange([kept, renamed]);
        var history = Create();
        await history.RefreshAsync();
        var onlyHere = history.Open(Guid.NewGuid(), Turn("Only in this window", "Yes"), Now.AddMinutes(-50));
        history.Selected = null;

        _source.Listed.Clear();
        var added = Saved("Added since", 2, "Added answer");
        _source.Listed.AddRange([kept, renamed with { Title = "After", UpdatedAt = Now.AddMinutes(-10), LatestAnswerText = "After answer" }, added]);
        await history.RefreshAsync();

        Assert.Equal(["Added since", "After", "Only in this window", "Kept"], history.Conversations.Select(conversation => conversation.Title));
        var updated = history.Conversations.Single(conversation => conversation.Id == renamed.Id);
        Assert.Equal("After answer", updated.Preview);
        Assert.Equal(Now.AddMinutes(-10), updated.UpdatedAt);
        Assert.Contains(onlyHere, history.Conversations);
    }

    [Fact]
    public async Task ALoadedConversationKeepsItsMessagesAndItsOwnPreview_WhenTheListingIsRefreshed()
    {
        var saved = Saved("Trip", 5, "Listed answer");
        _source.Listed.Add(saved);
        var history = Create();
        await history.RefreshAsync();
        var conversation = history.Open(saved.Id, Turn("Plan a trip", "Answer written here"), Now);

        _source.Listed[0] = saved with { Title = "Renamed", LatestAnswerText = "Older listing" };
        await history.RefreshAsync();

        Assert.Equal("Answer written here", conversation.Preview);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal("Renamed", conversation.Title);
    }

    [Fact]
    public async Task RefreshingKeepsTheOpenConversationOpen()
    {
        var saved = Saved("Trip", 5);
        _source.Listed.Add(saved);
        var history = Create();
        await history.RefreshAsync();
        var conversation = history.Open(saved.Id, Turn("Plan", "Ok"), Now);

        _source.Listed.Add(Saved("Another", 1));
        await history.RefreshAsync();

        Assert.Same(conversation, history.Selected);
        // The open one changed just now, so it is first.
        Assert.Equal(["Trip", "Another"], history.Conversations.Select(item => item.Title));
    }

    [Fact]
    public async Task AListingThatArrivesAfterANewerOneIsIgnored()
    {
        var first = new TaskCompletionSource();
        _source.BeforeList = (call, _) => call == 1 ? first.Task : Task.CompletedTask;
        var saved = Saved("Old title", 5, "Old answer");
        _source.Listed.Add(saved);
        var history = Create();

        // The first reading was asked when the conversation had its old title, and answers last.
        var slow = history.RefreshAsync();
        _source.Listed[0] = saved with { Title = "New title", LatestAnswerText = "New answer" };
        await history.RefreshAsync();
        first.SetResult();
        await slow;

        var conversation = Assert.Single(history.Conversations);
        Assert.Equal("New title", conversation.Title);
        Assert.Equal("New answer", conversation.Preview);
    }

    [Fact]
    public async Task ASourceThatCannotReadLeavesTheListAsItWas()
    {
        _source.Listed.Add(Saved("Kept", 5));
        var history = Create();
        await history.RefreshAsync();

        _source.ListFailure = new HistoryUnavailableException();
        await history.RefreshAsync();

        Assert.Equal(["Kept"], history.Conversations.Select(conversation => conversation.Title));
    }

    [Fact]
    public async Task WithNoSourceThereIsNothingToRefresh()
    {
        var history = new HistoryViewModel(_clock);

        await history.RefreshAsync();

        Assert.Empty(history.Conversations);
    }

    [Fact]
    public async Task ConversationsAreOrderedNewestFirstEvenWhenTheyAreListedInAnyOrder()
    {
        var times = new[] { 30, 5, 500, 90, 1, 250 };
        _source.Listed.AddRange(times.Select(minutes => Saved($"C{minutes}", minutes)));
        var history = Create();

        await history.RefreshAsync();

        Assert.Equal(["C1", "C5", "C30", "C90", "C250", "C500"], history.Conversations.Select(conversation => conversation.Title));
    }

    [Fact]
    public async Task AConversationContinuedHereMovesToTheTop_AndItsCardTakesTheNewAnswer()
    {
        var older = Saved("Older", 500, "Old answer");
        var newer = Saved("Newer", 5, "Newer answer");
        _source.Listed.AddRange([older, newer]);
        _source.Messages[older.Id] = Turn("Older question", "Old answer");
        var answers = new ScriptedAnswers { Respond = (_, _, show, _) => { show(new MessageViewModel(MessageRole.Assistant, "Fresh answer")); return Task.CompletedTask; } };
        var history = Create(answers);
        await history.RefreshAsync();
        history.Selected = history.Conversations[1];
        await WaitUntil(() => history.Selected!.IsLoaded);

        Assert.True(history.Send("A follow-up"));
        await WaitUntil(() => !history.IsAnswering);

        Assert.Equal(older.Id, history.Conversations[0].Id);
        Assert.Equal("Fresh answer", history.Conversations[0].Preview);
    }

    [Fact]
    public async Task ALongSavedHistoryIsListedAndSearchedQuickly()
    {
        const int count = 5000;
        _source.Listed.AddRange(Enumerable.Range(0, count).Select(index => Saved($"Conversation {index}", index * 7, $"Answer {index}")));
        _source.Found = _ => [new HistorySearchHit(_source.Listed[4321], "Answer 4321", [new TextMatch(0, 6)], null)];
        var history = new HistoryViewModel(_clock, _source, searchDelay: TimeSpan.Zero);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await history.RefreshAsync();
        var listed = watch.Elapsed;
        history.SearchText = "answer 4321";
        await history.WhenSearchSettledAsync();
        var searched = watch.Elapsed - listed;

        Assert.Equal(count, history.Conversations.Count);
        Assert.Equal("Conversation 0", history.Conversations[0].Title);
        Assert.Single(history.Conversations, conversation => conversation.IsShown);
        Assert.True(listed < TimeSpan.FromSeconds(3), $"Listing {count} conversations took {listed.TotalSeconds:0.0} s.");
        Assert.True(searched < TimeSpan.FromSeconds(3), $"Searching {count} conversations took {searched.TotalSeconds:0.0} s.");
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
