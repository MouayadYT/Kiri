using Assistant.Core.Domain;
using Assistant.Data.Persistence;
using Xunit;
using static Assistant.Data.Tests.HistoryHarness;

namespace Assistant.Data.Tests;

/// <summary>Saving conversations a message at a time, and reading them back as they were.</summary>
public sealed class ConversationPersistenceTests : IDisposable
{
    private readonly HistoryHarness _history = new();

    private SqliteConversationService Service => _history.Service;

    public void Dispose() => _history.Dispose();

    [Fact]
    public async Task ASavedTurnComesBackWithItsOrderTimesRolesAndText()
    {
        var conversation = Guid.NewGuid();
        var asked = User("What’s the weather this weekend?", 0);
        var answered = Answer("Saturday looks **sunny**.", 1);
        var followUp = User("And Sunday?", 5);
        await _history.SayAsync(conversation, asked, answered, followUp);

        var loaded = await Service.GetAsync(conversation);

        Assert.NotNull(loaded);
        Assert.Equal(conversation, loaded.Id);
        Assert.Equal(asked.CreatedAt, loaded.CreatedAt);
        Assert.Equal(followUp.CreatedAt, loaded.UpdatedAt);
        Assert.Equal([asked.Id, answered.Id, followUp.Id], loaded.Messages.Select(message => message.Id));
        Assert.Equal([MessageRole.User, MessageRole.Assistant, MessageRole.User], loaded.Messages.Select(message => message.Role));
        Assert.Equal(["What’s the weather this weekend?", "Saturday looks **sunny**.", "And Sunday?"], loaded.Messages.Select(message => message.Text));
        Assert.Equal([asked.CreatedAt, answered.CreatedAt, followUp.CreatedAt], loaded.Messages.Select(message => message.CreatedAt));
    }

    [Fact]
    public async Task ATimestampKeepsItsFullPrecision_AndItsInstantWhateverOffsetItCameWith()
    {
        var conversation = Guid.NewGuid();
        var moment = new DateTimeOffset(2026, 9, 30, 12, 34, 56, TimeSpan.FromHours(3)).AddTicks(1234567);
        var message = new Message(Guid.NewGuid(), MessageRole.User, "Hi", moment);
        await Service.SaveMessageAsync(conversation, message, moment);

        var loaded = await Service.GetAsync(conversation);

        Assert.NotNull(loaded);
        Assert.Equal(moment, loaded.Messages[0].CreatedAt);
        Assert.Equal(moment.UtcTicks, loaded.Messages[0].CreatedAt.UtcTicks);
        Assert.Equal(moment.UtcTicks, loaded.UpdatedAt.UtcTicks);
    }

    [Fact]
    public async Task ANewConversationIsCreatedByItsFirstMessage()
    {
        var conversation = Guid.NewGuid();

        await Service.SaveMessageAsync(conversation, User("Plan my weekend", 3), HistoryHarness.At(4));

        var summary = Assert.Single(await Service.ListAsync());
        Assert.Equal(conversation, summary.Id);
        Assert.Equal(HistoryHarness.At(3), summary.CreatedAt);
        Assert.Equal(HistoryHarness.At(4), summary.UpdatedAt);
        Assert.Equal(1, summary.MessageCount);
    }

    [Fact]
    public async Task ATurnIsAddedWithoutTouchingTheMessagesBeforeIt()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Question one", 0), Answer("Answer one", 1), User("Question two", 2));

        // From here on, every write to the messages is recorded.
        using (var connection = _history.Open())
        {
            connection.Execute("CREATE TABLE write_log (what TEXT NOT NULL, message TEXT NOT NULL)");
            connection.Execute("CREATE TRIGGER log_insert AFTER INSERT ON messages BEGIN INSERT INTO write_log VALUES ('insert', new.text); END");
            connection.Execute("CREATE TRIGGER log_update AFTER UPDATE ON messages BEGIN INSERT INTO write_log VALUES ('update', new.text); END");
            connection.Execute("CREATE TRIGGER log_delete AFTER DELETE ON messages BEGIN INSERT INTO write_log VALUES ('delete', old.text); END");
        }

        await _history.SayAsync(conversation, Answer("Answer two", 3));

        using var check = _history.Open();
        var writes = check.Query("SELECT what, message FROM write_log", reader => (reader.GetString(0), reader.GetString(1)));
        Assert.Equal([("insert", "Answer two")], writes);
        Assert.Equal(4, check.Count("messages"));
    }

    [Fact]
    public async Task ASavedMessageIsChangedWhereItStands_WhenItIsSavedAgain()
    {
        var conversation = Guid.NewGuid();
        var question = User("Tell me a story", 0);
        var answer = Answer("Once upon", 1, MessageOutcome.Stopped);
        var next = User("Another one", 2);
        await _history.SayAsync(conversation, question, answer, next);

        await Service.SaveMessageAsync(conversation, answer with { Text = "Once upon a time.", Outcome = MessageOutcome.Complete }, HistoryHarness.At(3));

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Equal([question.Id, answer.Id, next.Id], loaded.Messages.Select(message => message.Id));
        Assert.Equal("Once upon a time.", loaded.Messages[1].Text);
        Assert.Equal(MessageOutcome.Complete, loaded.Messages[1].Outcome);
        Assert.Equal(HistoryHarness.At(3), loaded.UpdatedAt);
    }

    [Theory]
    [InlineData(MessageOutcome.Complete)]
    [InlineData(MessageOutcome.Stopped)]
    [InlineData(MessageOutcome.Failed)]
    public async Task HowAnAnswerEndedIsKept_AnEmptyStoppedAnswerToo(MessageOutcome outcome)
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Hi", 0), Answer(string.Empty, 1, outcome));

        var loaded = await Service.GetAsync(conversation);

        Assert.NotNull(loaded);
        Assert.Equal(outcome, loaded.Messages[1].Outcome);
        Assert.Equal(string.Empty, loaded.Messages[1].Text);
    }

    [Fact]
    public async Task TheUpdateTimeNeverGoesBack()
    {
        var conversation = Guid.NewGuid();
        var first = User("One", 0);
        var second = User("Two", 1);
        await Service.SaveMessageAsync(conversation, first, HistoryHarness.At(10));
        await Service.SaveMessageAsync(conversation, second, HistoryHarness.At(4));

        var summary = Assert.Single(await Service.ListAsync());

        Assert.Equal(HistoryHarness.At(10), summary.UpdatedAt);
        Assert.Equal(HistoryHarness.At(0), summary.CreatedAt);
    }

    [Fact]
    public async Task StructuredCardsAreKeptInOrder_AmongTheProseTheyWereShownBetween()
    {
        var conversation = Guid.NewGuid();
        var text = "Here is the result.\n\nCalling it prints 19.";
        var answer = new Message(Guid.NewGuid(), MessageRole.Assistant, text, HistoryHarness.At(1))
        {
            Cards =
            [
                new CardMetadata("code", "{\"code\":\"Add(9, 10)\",\"language\":\"C#\"}", 19),
                new CardMetadata("rich_answer_card", "{\"label\":\"Calculation\",\"result\":\"19\"}", 19),
                new CardMetadata("file_list", "{\"files\":[]}", text.Length),
            ],
        };
        await _history.SayAsync(conversation, User("9+10", 0), answer);

        var loaded = await Service.GetAsync(conversation);

        Assert.NotNull(loaded);
        var cards = loaded.Messages[1].Cards;
        Assert.Equal(["code", "rich_answer_card", "file_list"], cards.Select(card => card.Kind));
        Assert.Equal([19, 19, text.Length], cards.Select(card => card.TextOffset));
        Assert.Equal("{\"code\":\"Add(9, 10)\",\"language\":\"C#\"}", cards[0].DataJson);
        Assert.Equal(text, loaded.Messages[1].Text);
    }

    [Fact]
    public async Task ACardsDataIsReplacedWhenTheMessageIsSavedAgain()
    {
        var conversation = Guid.NewGuid();
        var answer = Answer("Done.", 1, cards: [new CardMetadata("rich_answer_card", "{\"result\":\"1\"}", 0)]);
        await _history.SayAsync(conversation, User("Go", 0), answer);

        await Service.SaveMessageAsync(
            conversation,
            answer with { Cards = [new CardMetadata("rich_answer_card", "{\"result\":\"2\"}", 0)] },
            HistoryHarness.At(2));

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Equal("{\"result\":\"2\"}", Assert.Single(loaded.Messages[1].Cards).DataJson);
    }

    [Fact]
    public async Task AContextItemIsKeptAsADescriptor_NeverWithWhatItHeld()
    {
        const string selected = "the-selected-passage-that-must-not-be-stored";
        const string extracted = "extracted-text-of-the-report-that-must-not-be-stored";
        var conversation = Guid.NewGuid();
        var image = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF, 0x51, 0x52, 0x53 };
        var question = User(
            "Summarize these",
            0,
            new ContextItem(Guid.NewGuid(), ContextItemType.File, "report.docx") { FilePath = @"C:\Users\me\Documents\report.docx", Text = extracted },
            new ContextItem(Guid.NewGuid(), ContextItemType.Selection, "Selected text") { Text = selected },
            new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, "Screenshot") { ImageData = image },
            new ContextItem(Guid.NewGuid(), ContextItemType.Image, "photo.png") { FilePath = @"C:\Users\me\Pictures\photo.png", ImageData = image });
        await _history.SayAsync(conversation, question);

        var loaded = await Service.GetAsync(conversation);

        Assert.NotNull(loaded);
        var items = loaded.Messages[0].ContextItems;
        Assert.Equal(
            [ContextItemType.File, ContextItemType.Selection, ContextItemType.Screenshot, ContextItemType.Image],
            items.Select(item => item.Type));
        Assert.Equal(["report.docx", "Selected text", "Screenshot", "photo.png"], items.Select(item => item.DisplayName));
        Assert.Equal([@"C:\Users\me\Documents\report.docx", null, null, @"C:\Users\me\Pictures\photo.png"], items.Select(item => item.FilePath));

        // Nothing of the content was kept, in memory or on disk.
        Assert.All(items, item =>
        {
            Assert.Null(item.Text);
            Assert.True(item.ImageData.IsEmpty);
        });
        Assert.False(_history.FilesContain(selected));
        Assert.False(_history.FilesContain(extracted));
        Assert.False(_history.Files().Any(file => file.AsSpan().IndexOf(image.AsSpan(8)) >= 0), "Image bytes are in the database.");
    }

    [Fact]
    public async Task TheSameContextItemOnTwoMessagesIsKeptOnEach()
    {
        var conversation = Guid.NewGuid();
        var shared = new ContextItem(Guid.NewGuid(), ContextItemType.File, "notes.txt") { FilePath = @"C:\notes.txt" };

        await _history.SayAsync(conversation, User("First", 0, shared), User("Second", 1, shared));

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Equal(["notes.txt", "notes.txt"], loaded.Messages.Select(message => Assert.Single(message.ContextItems).DisplayName));
    }

    [Fact]
    public async Task AttachingAnItemToASavedMessageAndSavingAgainReplacesItsDescriptors()
    {
        var conversation = Guid.NewGuid();
        var question = User("What is this?", 0);
        await _history.SayAsync(conversation, question);

        var image = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "sign.jpg") { FilePath = @"C:\sign.jpg" };
        await Service.SaveMessageAsync(conversation, question with { ContextItems = [image] }, HistoryHarness.At(1));
        await Service.SaveMessageAsync(conversation, question with { ContextItems = [image] }, HistoryHarness.At(2));

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Equal("sign.jpg", Assert.Single(loaded.Messages[0].ContextItems).DisplayName);
    }

    [Fact]
    public async Task ManySavesAtOnceKeepEveryMessageInItsOwnPlace()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("First", 0));
        var messages = Enumerable.Range(1, 24).Select(index => User($"Message {index}", index)).ToArray();

        await Task.WhenAll(messages.Select(message => Service.SaveMessageAsync(conversation, message, message.CreatedAt)));

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Equal(25, loaded.Messages.Count);
        using var connection = _history.Open();
        Assert.Equal(
            Enumerable.Range(0, 25),
            connection.Query("SELECT position FROM messages ORDER BY position", reader => reader.GetInt32(0)));
        Assert.Equal(25, loaded.Messages.Select(message => message.Id).Distinct().Count());
    }

    [Fact]
    public async Task AMessageBelongsToOneConversation()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var message = User("Hello", 0);
        await Service.SaveMessageAsync(first, message, message.CreatedAt);

        var exception = await Assert.ThrowsAsync<DatabaseException>(
            () => Service.SaveMessageAsync(second, message, message.CreatedAt));

        Assert.DoesNotContain("Hello", exception.Message, StringComparison.Ordinal);
        Assert.Equal(first, Assert.Single(await Service.ListAsync()).Id);
    }

    [Fact]
    public async Task ACardBeyondTheEndOfTheTextIsRefused_AndNothingIsSaved()
    {
        var conversation = Guid.NewGuid();
        var answer = Answer("Short", 1, cards: [new CardMetadata("code", "{}", 6)]);

        await Assert.ThrowsAsync<ArgumentException>(() => Service.SaveMessageAsync(conversation, answer, answer.CreatedAt));

        Assert.Empty(await Service.ListAsync());
    }

    [Fact]
    public async Task ASaveThatFailsHalfWayLeavesNothingBehind()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("First", 0));
        using (var connection = _history.Open())
        {
            // A card that cannot be stored, after the message itself was: the whole save is rolled back.
            connection.Execute(
                "CREATE TRIGGER refuse_cards BEFORE INSERT ON message_cards BEGIN SELECT RAISE(ABORT, 'no cards today'); END");
        }

        var answer = Answer("Answer", 1, cards: [new CardMetadata("code", "{}", 0)]);
        await Assert.ThrowsAsync<DatabaseException>(() => Service.SaveMessageAsync(conversation, answer, answer.CreatedAt));

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Messages);
        Assert.Equal(HistoryHarness.At(0), loaded.UpdatedAt);
    }

    [Fact]
    public async Task ACancelledSaveWritesNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service.SaveMessageAsync(Guid.NewGuid(), User("Hi", 0), HistoryHarness.At(0), cancelled.Token));

        Assert.Empty(await Service.ListAsync());
    }

    [Fact]
    public async Task ASavedConversationSurvivesARestart()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Remember me", 0), Answer("I will", 1));

        var restarted = _history.CreateService();
        var loaded = await restarted.GetAsync(conversation);

        Assert.NotNull(loaded);
        Assert.Equal(["Remember me", "I will"], loaded.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task AConversationThatIsNotSavedIsNotFound()
    {
        Assert.Null(await Service.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task SavingAWholeConversationReplacesWhatWasStored()
    {
        var conversation = Guid.NewGuid();
        var kept = User("Kept", 0);
        await _history.SayAsync(conversation, kept, Answer("Dropped", 1), User("Also dropped", 2));

        var replacement = Answer("Replacement", 3);
        await Service.SaveAsync(
            new Conversation(conversation, "A title", HistoryHarness.At(0), HistoryHarness.At(3)) { Messages = [kept, replacement] });

        var loaded = await Service.GetAsync(conversation);
        Assert.NotNull(loaded);
        Assert.Equal("A title", loaded.Title);
        Assert.Equal(["Kept", "Replacement"], loaded.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task AMessageOrItemOfAKindThisBuildDoesNotKnowIsLeftOut_NotFailed()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Known", 0), Answer("Also known", 1));
        using (var connection = _history.Open())
        {
            // What a newer build may have written: a role, an outcome, an item type and a card kind that do not exist here.
            connection.Execute(
                "INSERT INTO messages (id, conversation_id, position, role, text, created_at, outcome) " +
                "VALUES ('99999999-9999-9999-9999-999999999999', $c, 2, 'Narrator', 'from the future', $at, 'Complete')",
                ("$c", conversation.ToString("D")),
                ("$at", "2026-09-30T10:00:00.0000000Z"));
            connection.Execute("UPDATE messages SET outcome = 'Paused' WHERE position = 1");
            connection.Execute(
                "INSERT INTO message_context_items (id, message_id, position, type, display_name, file_path) " +
                "SELECT '88888888-8888-8888-8888-888888888888', id, 5, 'Hologram', 'hologram', NULL FROM messages WHERE position = 0");
            connection.Execute(
                "INSERT INTO message_cards (message_id, position, kind, text_offset, data) " +
                "SELECT id, 0, 'Weird-Kind', 0, '{}' FROM messages WHERE position = 1");
        }

        var loaded = await Service.GetAsync(conversation);

        Assert.NotNull(loaded);
        Assert.Equal(["Known", "Also known"], loaded.Messages.Select(message => message.Text));
        Assert.Equal(MessageOutcome.Complete, loaded.Messages[1].Outcome);
        Assert.Empty(loaded.Messages[0].ContextItems);
        Assert.Empty(loaded.Messages[1].Cards);
    }

    [Fact]
    public async Task ACardOffsetPastTheTextIsBroughtBackToItsEnd()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Hi", 0), Answer("Text", 1, cards: [new CardMetadata("code", "{}", 4)]));
        using (var connection = _history.Open())
        {
            connection.Execute("UPDATE message_cards SET text_offset = 400");
        }

        var loaded = await Service.GetAsync(conversation);

        Assert.NotNull(loaded);
        Assert.Equal(4, Assert.Single(loaded.Messages[1].Cards).TextOffset);
    }
}
