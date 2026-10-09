using System.Text;
using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Xunit;

namespace Assistant.Data.Tests;

public sealed class DatabaseBackupTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void ABackupIsNamedForWhenItWasTakenAndTheVersionItHolds_AndLeavesNothingElseBehind()
    {
        using var connection = _database.Initialize();
        _database.Time.Advance(TimeSpan.FromMilliseconds(123));

        var path = _database.CreateBackup().Create(connection, schemaVersion: 1);

        Assert.Equal("assistant-20260930T101500123Z-v0001.db", Path.GetFileName(path));
        Assert.Equal([path], _database.BackupFiles());
        using var backup = SqlHelpers.OpenReadOnly(path);
        Assert.Equal(SqlHelpers.AllMigrationVersions(), backup.AppliedVersions());
    }

    [Fact]
    public void ABackupOfADatabaseInUseHoldsItsCommittedRows()
    {
        using var connection = _database.Initialize();
        using var writer = _database.Open();
        connection.InsertConversation();
        connection.InsertMessage("m1");
        connection.InsertMessage("m2", position: 1);
        using var transaction = writer.BeginTransaction();
        writer.InsertConversation("22222222-2222-2222-2222-222222222222", "Not committed");

        var path = _database.CreateBackup().Create(connection, 1);
        transaction.Rollback();

        using var backup = SqlHelpers.OpenReadOnly(path);
        Assert.Equal(1, backup.Count("conversations"));
        Assert.Equal(2, backup.Count("messages"));
    }

    [Fact]
    public void ContentTheUserDeletedIsNotCarriedIntoABackup()
    {
        const string secret = "the-pin-is-4-7-1-1-unmistakable";
        using var connection = _database.Initialize();
        connection.InsertConversation();
        connection.InsertMessage("m1", text: secret);
        var before = _database.CreateBackup().Create(connection, 1);
        connection.Execute("DELETE FROM conversations");
        _database.Time.Advance(TimeSpan.FromMinutes(1));

        var after = _database.CreateBackup().Create(connection, 1);

        // The first backup shows the search can find the text; the second, taken after the deletion, must not have it.
        Assert.True(Contains(before, secret));
        Assert.False(Contains(after, secret));
    }

    [Fact]
    public void OnlyTheNewestBackupsAreKept_AndOtherFilesInTheFolderAreNeverTouched()
    {
        using var database = new TestDatabase(backupsToKeep: 2);
        using var connection = database.Initialize();
        Directory.CreateDirectory(database.Options.BackupsDirectory);
        var bystanders = new[] { "notes.txt", "assistant-old.db", "assistant-20200101T000000000Z-v0001.txt" }
            .Select(name => Path.Combine(database.Options.BackupsDirectory, name))
            .ToArray();
        foreach (var bystander in bystanders)
        {
            File.WriteAllText(bystander, "keep me");
        }

        var backup = database.CreateBackup();
        var paths = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            database.Time.Advance(TimeSpan.FromMinutes(1));
            paths.Add(backup.Create(connection, 1));
        }

        var backups = database.BackupFiles().Where(file => !bystanders.Contains(file)).ToArray();
        Assert.Equal(paths.TakeLast(2), backups);
        Assert.All(bystanders, bystander => Assert.True(File.Exists(bystander)));
    }

    [Fact]
    public void ABackupTakenAtTheSameMomentFailsWithoutDamagingTheFirst_AndLeavesNoPartialFile()
    {
        using var connection = _database.Initialize();
        var backup = _database.CreateBackup();
        var first = backup.Create(connection, 1);

        Assert.ThrowsAny<IOException>(() => backup.Create(connection, 1));

        Assert.Equal([first], _database.BackupFiles());
        using var opened = SqlHelpers.OpenReadOnly(first);
        Assert.Equal(SqlHelpers.AllMigrationVersions(), opened.AppliedVersions());
    }

    [Fact]
    public void DeleteAllRemovesEveryBackupAndAnyUnfinishedOne_ButNothingElse()
    {
        using var connection = _database.Initialize();
        var backup = _database.CreateBackup();
        backup.Create(connection, 1);
        _database.Time.Advance(TimeSpan.FromMinutes(1));
        backup.Create(connection, 1);
        var unfinished = Path.Combine(_database.Options.BackupsDirectory, "assistant-20260930T000000000Z-v0001.db.tmp");
        var bystander = Path.Combine(_database.Options.BackupsDirectory, "notes.txt");
        File.WriteAllText(unfinished, "half");
        File.WriteAllText(bystander, "keep me");

        var deleted = backup.DeleteAll();

        Assert.Equal(3, deleted);
        Assert.Equal([bystander], _database.BackupFiles());
    }

    [Fact]
    public void DeleteAllWithoutABackupsFolderDeletesNothing()
    {
        Assert.Equal(0, _database.CreateBackup().DeleteAll());
    }

    [Fact]
    public void AFileThatIsNotADatabaseFailsTheCheck()
    {
        var path = Path.Combine(_database.Root, "not-a-database.db");
        Directory.CreateDirectory(_database.Root);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('x', 4096)));

        Assert.ThrowsAny<Exception>(() => DatabaseBackup.Verify(path));
    }

    private static bool Contains(string path, string text)
    {
        // Reads the file's bytes, not through SQLite, since a deleted row's text can still sit in an unused page.
        var bytes = File.ReadAllBytes(path);
        return bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
    }
}
