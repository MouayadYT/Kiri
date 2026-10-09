using Assistant.Core.Domain;
using Assistant.Core.History;
using Xunit;
using static Assistant.Data.Tests.HistoryHarness;

namespace Assistant.Data.Tests;

/// <summary>Titles, the list a history window shows, and turning history off.</summary>
public sealed class ConversationTitleAndListTests : IDisposable
{
    private readonly HistoryHarness _history = new();

    private SqliteConversationService Service => _history.Service;

    public void Dispose() => _history.Dispose();

    [Fact]
    public async Task AConversationGetsAProvisionalTitleFromTheFirstThingTheUserAsked()
    {
        var conversation = Guid.NewGuid();

        await _history.SayAsync(conversation, User("  What’s the weather\nthis weekend?  ", 0), Answer("Sunny.", 1), User("And Sunday?", 2));

        var summary = Assert.Single(await Service.ListAsync());
        Assert.Equal("What’s the weather this weekend?", summary.Title);
    }

    [Fact]
    public async Task AVeryLongFirstRequestGivesAShortTitle()
    {
        var conversation = Guid.NewGuid();
        var request = string.Join(' ', Enumerable.Repeat("summarize", 40));

        await _history.SayAsync(conversation, User(request, 0));

        var title = Assert.Single(await Service.ListAsync()).Title;
        Assert.Equal(ConversationTitle.Provisional(request), title);
        Assert.True(title.Length <= ConversationTitle.MaxLength);
    }

    [Fact]
    public async Task AnAnswerAloneDoesNotTitleAConversation_TheFirstRequestThatFollowsDoes()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, Answer("Hello, I am here.", 0));
        Assert.Equal(string.Empty, Assert.Single(await Service.ListAsync()).Title);

        await _history.SayAsync(conversation, User("Are you there?", 1), User("A second request", 2));

        Assert.Equal("Are you there?", Assert.Single(await Service.ListAsync()).Title);
    }

    [Fact]
    public async Task ARequestThatIsOnlyAnAttachmentIsTitledByTheNameOfWhatWasAttached()
    {
        var conversation = Guid.NewGuid();
        var photo = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "beach.jpg") { FilePath = @"C:\beach.jpg" };

        await _history.SayAsync(conversation, User(string.Empty, 0, photo));

        Assert.Equal("beach.jpg", Assert.Single(await Service.ListAsync()).Title);
    }

    [Fact]
    public async Task ATitleTheUserChoseIsKept_WhateverIsAskedNext()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Plan my weekend", 0));

        await Service.RenameAsync(conversation, "  Lisbon\ntrip ");
        await _history.SayAsync(conversation, Answer("Sure", 1), User("Anything else?", 2));

        Assert.Equal("Lisbon trip", Assert.Single(await Service.ListAsync()).Title);
    }

    [Fact]
    public async Task RenamingNeedsAnExistingConversationAndAnActualTitle()
    {
        await Service.RenameAsync(Guid.NewGuid(), "Nothing to rename");
        Assert.Empty(await Service.ListAsync());

        await Assert.ThrowsAsync<ArgumentException>(() => Service.RenameAsync(Guid.NewGuid(), "   "));
    }

    [Fact]
    public async Task TheListIsNewestFirst_WithoutTheMessages()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var newest = Guid.NewGuid();
        await _history.SayAsync(older, User("Older", 0), Answer("Older answer", 1));
        await _history.SayAsync(newest, User("Newest", 50));
        await _history.SayAsync(newer, User("Newer", 20));

        var summaries = await Service.ListAsync();

        Assert.Equal([newest, newer, older], summaries.Select(summary => summary.Id));
        Assert.Equal([1, 1, 2], summaries.Select(summary => summary.MessageCount));
    }

    [Fact]
    public async Task ContinuingAnOldConversationBringsItToTheTop()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await _history.SayAsync(first, User("First", 0));
        await _history.SayAsync(second, User("Second", 10));

        await _history.SayAsync(first, Answer("Answer", 30));

        Assert.Equal([first, second], (await Service.ListAsync()).Select(summary => summary.Id));
    }

    [Fact]
    public async Task ASummaryCarriesTheLatestAnswersTextForAPreview_SkippingAnswersWithNone()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(
            conversation,
            User("Q1", 0),
            Answer("The **first** answer", 1),
            User("Q2", 2),
            Answer("The second answer", 3),
            User("Q3", 4),
            Answer(string.Empty, 5, MessageOutcome.Stopped));

        var summary = Assert.Single(await Service.ListAsync());

        Assert.Equal("The second answer", summary.LatestAnswerText);
    }

    [Fact]
    public async Task AConversationWithNoAnswerHasNoPreview()
    {
        await _history.SayAsync(Guid.NewGuid(), User("Anyone there?", 0));

        Assert.Null(Assert.Single(await Service.ListAsync()).LatestAnswerText);
    }

    [Fact]
    public async Task APreviewIsCutOffAfterAFewThousandCharacters()
    {
        await _history.SayAsync(Guid.NewGuid(), User("Long", 0), Answer(new string('x', 20_000), 1));

        var preview = Assert.Single(await Service.ListAsync()).LatestAnswerText;

        Assert.NotNull(preview);
        Assert.Equal(4000, preview.Length);
    }

    [Fact]
    public async Task ASummaryCarriesTheImageAttachedMostRecently_InWhicheverMessage()
    {
        var conversation = Guid.NewGuid();
        var valley = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "valley.jpg") { FilePath = @"C:\p\valley.jpg" };
        var lake = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "lake.jpg") { FilePath = @"C:\p\lake.jpg" };
        var screenshot = new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "Screenshot");
        var report = new ContextItem(Guid.NewGuid(), ContextItemType.File, "report.pdf") { FilePath = @"C:\p\report.pdf" };
        await _history.SayAsync(
            conversation,
            User("Where is this?", 0, valley),
            Answer("The Rockies", 1),
            User("And this?", 2, lake, screenshot),
            Answer("A lake", 3),
            User("Summarize", 4, report),
            Answer("Done", 5));

        var image = Assert.Single(await Service.ListAsync()).LatestImage;

        // Not the report, and not a screenshot, which has no file to show again.
        Assert.NotNull(image);
        Assert.Equal(ContextItemType.Image, image.Type);
        Assert.Equal("lake.jpg", image.DisplayName);
        Assert.Equal(@"C:\p\lake.jpg", image.FilePath);
        Assert.Null(image.Text);
    }

    [Fact]
    public async Task AConversationWithNoImageHasNoImage()
    {
        await _history.SayAsync(Guid.NewGuid(), User("Hi", 0, new ContextItem(Guid.NewGuid(), ContextItemType.File, "a.txt") { FilePath = @"C:\a.txt" }));

        Assert.Null(Assert.Single(await Service.ListAsync()).LatestImage);
    }

    [Fact]
    public async Task WithHistoryOff_NothingIsSaved_NotEvenTheDatabase()
    {
        _history.Settings.HistoryEnabled = false;
        var conversation = Guid.NewGuid();

        await _history.SayAsync(conversation, User("A private question", 0), Answer("A private answer", 1));
        await Service.SaveAsync(new Conversation(conversation, "Title", At(0), At(1)) { Messages = [User("Hidden", 0)] });

        Assert.False(File.Exists(_history.Database.Options.DatabasePath));
        Assert.Empty(await Service.ListAsync());
    }

    [Fact]
    public async Task WithHistoryOff_WhatWasSavedBeforeCanStillBeReadAndDeleted()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Saved while history was on", 0));

        _history.Settings.HistoryEnabled = false;
        await _history.SayAsync(conversation, Answer("Not saved", 1));

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Equal(["Saved while history was on"], loaded.Messages.Select(message => message.Text));

        await Service.DeleteAsync(conversation);
        Assert.Empty(await Service.ListAsync());
    }

    [Fact]
    public async Task TurningHistoryBackOnSavesAgain()
    {
        _history.Settings.HistoryEnabled = false;
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Skipped", 0));

        _history.Settings.HistoryEnabled = true;
        await _history.SayAsync(conversation, User("Saved", 1));

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Equal(["Saved"], loaded.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ADatabaseThatCannotBeOpenedIsReportedAsADatabaseError_AndTheNextCallTriesAgain()
    {
        // A file where the data folder should be.
        Directory.CreateDirectory(Path.GetDirectoryName(_history.Database.Options.DatabasePath)!);
        Directory.Delete(Path.GetDirectoryName(_history.Database.Options.DatabasePath)!);
        File.WriteAllText(Path.GetDirectoryName(_history.Database.Options.DatabasePath)!, "in the way");

        await Assert.ThrowsAsync<DatabaseException>(() => Service.ListAsync());

        File.Delete(Path.GetDirectoryName(_history.Database.Options.DatabasePath)!);
        Assert.Empty(await Service.ListAsync());
    }
}
