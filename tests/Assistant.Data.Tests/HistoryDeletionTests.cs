using Assistant.Core.Domain;
using Assistant.Data.Persistence;
using Xunit;
using static Assistant.Data.Tests.HistoryHarness;

namespace Assistant.Data.Tests;

/// <summary>Deleting history is real: the rows, the search index and the database's own files forget what was deleted.</summary>
public sealed class HistoryDeletionTests : IDisposable
{
    // Lower case, one word and unlike any other, so the search index holds it as it is, and a scan of the files finds it.
    private const string TitleWord = "zqtitlemarkerxx";
    private const string MessageWord = "zqmessagemarkerxx";
    private const string AnswerWord = "zqanswermarkerxx";
    private const string CardWord = "zqcardmarkerxx";
    private const string PathWord = "zqpathmarkerxx";

    private readonly HistoryHarness _history = new();

    private SqliteConversationService Service => _history.Service;

    public void Dispose() => _history.Dispose();

    private async Task<Guid> SaveMarkedConversationAsync()
    {
        var conversation = Guid.NewGuid();
        var photo = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "photo.png") { FilePath = $@"C:\{PathWord}\photo.png" };
        await _history.SayAsync(
            conversation,
            User($"{TitleWord} and then {MessageWord}", 0, photo),
            Answer($"An answer with {AnswerWord}", 1, MessageOutcome.Complete, new CardMetadata("code", $"{{\"code\":\"{CardWord}\"}}", 0)));
        return conversation;
    }

    [Fact]
    public async Task DeletingAConversationRemovesItsRows_AndItsPlaceInTheSearchIndex()
    {
        var doomed = await SaveMarkedConversationAsync();
        var kept = Guid.NewGuid();
        await _history.SayAsync(kept, User("A conversation to keep", 5), Answer("Kept answer", 6));

        await Service.DeleteAsync(doomed);

        Assert.Null(await Service.GetAsync(doomed));
        Assert.Equal([kept], (await Service.ListAsync()).Select(summary => summary.Id));
        Assert.Empty(await Service.SearchAsync(MessageWord));
        Assert.Empty(await Service.SearchAsync(TitleWord));
        using var connection = _history.Open();
        Assert.Equal(2, connection.Count("messages"));
        Assert.Equal(0, connection.Count("message_cards"));
        Assert.Equal(0, connection.Count("message_context_items"));
        Assert.Equal(1, connection.Count("conversations"));
        Assert.Equal(1, connection.Count("conversation_title_search"));
        Assert.Equal(1, IndexedTitles(connection, "keep"));
        Assert.Equal(0, IndexedMessages(connection, MessageWord));
    }

    [Fact]
    public async Task TheSearchIndexAgreesWithTheMessagesAfterDeletingAndEditing()
    {
        var doomed = await SaveMarkedConversationAsync();
        var kept = Guid.NewGuid();
        var answer = Answer("Before the edit", 6);
        await _history.SayAsync(kept, User("Keep this", 5), answer);
        await Service.SaveMessageAsync(kept, answer with { Text = "After the edit" }, At(7));
        await Service.DeleteAsync(doomed);

        using var connection = _history.Open();

        // Raises an error when the index and the messages it was made from disagree.
        connection.Execute("INSERT INTO message_search (message_search, rank) VALUES ('integrity-check', 1)");
        Assert.Equal(0, IndexedMessages(connection, "before"));
        Assert.Equal(1, IndexedMessages(connection, "after"));
    }

    [Fact]
    public async Task WhatWasDeletedIsNotLeftInTheDatabaseFiles()
    {
        var doomed = await SaveMarkedConversationAsync();
        await _history.SayAsync(Guid.NewGuid(), User("Another conversation", 9));
        var everything = new[] { TitleWord, MessageWord, AnswerWord, CardWord, PathWord };

        // Before: the text is in the file the way SQLite keeps text.
        Assert.All(everything, word => Assert.True(_history.FilesContain(word), $"'{word}' was not saved."));

        await Service.DeleteAsync(doomed);

        Assert.All(everything, word => Assert.False(_history.FilesContain(word), $"'{word}' is still in the database's files."));
    }

    [Fact]
    public async Task WhatWasDeletedIsNotLeftInTheWriteAheadLog_WhileAnotherConnectionIsOpen()
    {
        var doomed = await SaveMarkedConversationAsync();

        // With another connection open, closing the one that deleted does not empty the write-ahead log by itself, and the
        // log still holds the pages as they were before the delete.
        using var other = _history.Open();
        other.Execute("SELECT 1");
        await Service.DeleteAsync(doomed);

        Assert.All(
            new[] { TitleWord, MessageWord, AnswerWord, CardWord, PathWord },
            word => Assert.False(_history.FilesContain(word), $"'{word}' is still in the database's files."));
    }

    [Fact]
    public async Task DeletingEverythingLeavesNothingBehind_BackupsIncluded()
    {
        await SaveMarkedConversationAsync();
        await _history.SayAsync(Guid.NewGuid(), User($"Second {MessageWord}", 9), Answer("Second answer", 10));

        // A backup is a copy of the history, taken before an upgrade.
        Directory.CreateDirectory(_history.Database.Options.BackupsDirectory);
        string backup;
        using (var connection = _history.Open())
        {
            backup = _history.Database.CreateBackup().Create(connection, 1);
        }

        Assert.True(File.Exists(backup));

        await Service.DeleteAllAsync();

        Assert.Empty(await Service.ListAsync());
        Assert.False(File.Exists(backup));
        Assert.Empty(_history.Database.BackupFiles());
        Assert.All(
            new[] { TitleWord, MessageWord, AnswerWord, CardWord, PathWord },
            word => Assert.False(_history.FilesContain(word), $"'{word}' is still in the database's files."));
        using var check = _history.Open();
        Assert.Equal(0, check.Count("messages"));
        Assert.Equal(0, check.Count("conversation_title_search"));
        Assert.Empty(await Service.SearchAsync(MessageWord));
    }

    [Fact]
    public async Task DeletingEverythingKeepsWhatIsNotHistory()
    {
        await SaveMarkedConversationAsync();
        using (var connection = _history.Open())
        {
            connection.Execute("INSERT INTO people (id, display_name, created_at, updated_at) VALUES ('p1', 'Sara', $at, $at)", ("$at", SqlHelpers.Timestamp));
            connection.Execute("INSERT INTO tool_permissions (tool_name, decision, decided_at) VALUES ('copy_text', 'Allow', $at)", ("$at", SqlHelpers.Timestamp));
        }

        await Service.DeleteAllAsync();

        using var check = _history.Open();
        Assert.Equal(1, check.Count("people"));
        Assert.Equal(1, check.Count("tool_permissions"));
    }

    [Fact]
    public async Task ASearchIndexEntryOutlivesNoRow_EvenWhenTheRowIsRemovedByACascade()
    {
        var conversation = await SaveMarkedConversationAsync();

        // Not through the service: a plain delete of the conversation, as a cascade does for its messages.
        using (var connection = _history.Open())
        {
            connection.Execute("DELETE FROM conversations WHERE id = $id", ("$id", conversation.ToString("D")));
            Assert.Equal(0, IndexedMessages(connection, MessageWord));
            Assert.Equal(0, IndexedTitles(connection, TitleWord));
            connection.Execute("INSERT INTO message_search (message_search, rank) VALUES ('integrity-check', 1)");
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ABackupThatCannotBeDeleted_IsReportedAsTheHistoryFailingToDelete_AndTheNextTryDeletesIt()
    {
        await SaveMarkedConversationAsync();
        string backup;
        using (var connection = _history.Open())
        {
            backup = _history.Database.CreateBackup().Create(connection, 1);
        }

        // Another program holds the backup, so Windows refuses to delete it.
        using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Assert.ThrowsAsync<DatabaseException>(() => Service.DeleteAllAsync());

            Assert.IsType<IOException>(failure.InnerException);
            Assert.Equal("The conversation history could not be deleted.", failure.Message);
        }

        // The conversations went first; the backup stays until the next try, which deletes it.
        Assert.Empty(await Service.ListAsync());
        Assert.True(File.Exists(backup));
        Assert.Contains("could not be deleted: IOException", _history.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(PathWord, _history.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(backup), _history.Logs.AllText, StringComparison.Ordinal);

        await Service.DeleteAllAsync();

        Assert.False(File.Exists(backup));
    }

    [Fact]
    public async Task TheFilesWouldHoldTheWordsWithoutTheCleanUp_SoTheCleanUpIsWhatRemovesThem()
    {
        // Proves the file check above can see the leftovers: delete through SQL alone, which does not merge the index.
        var conversation = await SaveMarkedConversationAsync();
        using (var connection = _history.Open())
        {
            connection.Execute("DELETE FROM conversations WHERE id = $id", ("$id", conversation.ToString("D")));
        }

        Assert.True(
            _history.FilesContain(MessageWord) || _history.FilesContain(TitleWord),
            "The index no longer keeps deleted words, so the clean-up after a delete is not needed.");
        await Task.CompletedTask;
    }

    private static int IndexedMessages(Microsoft.Data.Sqlite.SqliteConnection connection, string word) =>
        connection.ExecuteScalar<int>("SELECT COUNT(*) FROM message_search WHERE message_search MATCH $q", ("$q", $"\"{word}\"*"));

    private static int IndexedTitles(Microsoft.Data.Sqlite.SqliteConnection connection, string word) =>
        connection.ExecuteScalar<int>("SELECT COUNT(*) FROM conversation_title_search WHERE conversation_title_search MATCH $q", ("$q", $"\"{word}\"*"));
}
