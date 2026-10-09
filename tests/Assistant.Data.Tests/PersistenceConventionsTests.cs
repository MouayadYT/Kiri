using Assistant.Core.Domain;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>
/// The conventions every repository shares (PROJECT_SPEC §5.9): how an id and a list of ids are written, how a value is
/// read back, and how the files the Data layer sets aside are removed.
/// </summary>
public sealed class PersistenceConventionsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "assistant-data-tests", Guid.NewGuid().ToString("N"));

    public PersistenceConventionsTests()
    {
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void AnIdIsLowerCaseTextWithHyphens_AndReadsBackAsTheSameId()
    {
        var id = Guid.Parse("0F8FAD5B-D9CB-469F-A165-70867728950E");

        var text = DatabaseIds.ToText(id);

        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", text);
        Assert.Equal(id, DatabaseIds.FromText(text));
        Assert.Equal(id, Single(reader => reader.GetId(0), text));
    }

    [Fact]
    public void AListOfIdsIsOneParameter_ThatTheSqlReadsBackAsRowsInOrder()
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        var read = _connection.Query(
            "SELECT value FROM json_each($ids)", reader => reader.GetId(0), ("$ids", DatabaseIds.ToJsonArray(ids)));

        Assert.Equal(ids, read);
        Assert.Empty(_connection.Query("SELECT value FROM json_each($ids)", reader => reader.GetId(0), ("$ids", DatabaseIds.ToJsonArray([]))));
    }

    [Fact]
    public void AMomentReadsBackAsTheMomentThatWasWritten()
    {
        var moment = new DateTimeOffset(2026, 9, 30, 12, 15, 0, 123, TimeSpan.FromHours(2));

        Assert.Equal(moment, Single(reader => reader.GetTimestamp(0), DatabaseTimestamps.ToText(moment)));
    }

    [Fact]
    public void ANullColumnReadsAsNoText()
    {
        Assert.Null(Single(reader => reader.GetStringOrNull(0), null));
        Assert.Equal("text", Single(reader => reader.GetStringOrNull(0), "text"));
    }

    [Theory]
    [InlineData("User", true)]
    [InlineData("Assistant", true)]
    [InlineData("Tool", true)]
    [InlineData("Robot", false)] // a role a newer build knows
    [InlineData("assistant", false)]
    [InlineData(" Assistant", false)]
    [InlineData("Assistant ", false)]
    [InlineData("1", false)]
    [InlineData("7", false)]
    [InlineData("-1", false)]
    [InlineData("", false)]
    public void AnEnumIsReadOnlyByItsExactName(string stored, bool known)
    {
        var read = Single(reader => reader.TryGetEnum<MessageRole>(0, out var role) ? role : (MessageRole?)null, stored);

        Assert.Equal(known, read is not null);
        if (known)
        {
            Assert.Equal(stored, read.ToString());
        }
    }

    [Fact]
    public void CleaningUpKeepsTheNewestByName_AndDeletesTheRest()
    {
        var files = CreateFiles("b-20260930T100000.tmp", "b-20260930T100500.tmp", "b-20260929T235959.tmp", "b-20260930T101000.tmp");

        var left = FileCleanup.DeleteAllButNewest(files, keep: 2);

        Assert.Equal(0, left);
        Assert.Equal(["b-20260930T100500.tmp", "b-20260930T101000.tmp"], Remaining());
    }

    [Fact]
    public void AFileAnotherProgramHolds_IsLeftAndCounted_AndTheOthersAreStillDeleted()
    {
        var files = CreateFiles("b-1.tmp", "b-2.tmp", "b-3.tmp", "b-4.tmp");

        int left;
        using (new FileStream(files[1], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(FileCleanup.TryDelete(files[1]));
            left = FileCleanup.DeleteAllButNewest(files, keep: 1);
        }

        Assert.Equal(1, left);
        Assert.Equal(["b-2.tmp", "b-4.tmp"], Remaining());
        Assert.True(FileCleanup.TryDelete(files[1]));
        Assert.True(FileCleanup.TryDelete(Path.Combine(_folder, "never-there.tmp")));
    }

    // Reads the one value SQL gives back for a parameter.
    private T Single<T>(Func<SqliteDataReader, T> read, string? value) =>
        Assert.Single(_connection.Query("SELECT $value", read, ("$value", value)));

    private string[] CreateFiles(params string[] names)
    {
        Directory.CreateDirectory(_folder);
        var paths = names.Select(name => Path.Combine(_folder, name)).ToArray();
        foreach (var path in paths)
        {
            File.WriteAllText(path, "set aside");
        }

        return paths;
    }

    private string[] Remaining() =>
        [.. Directory.EnumerateFiles(_folder).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal)];
}
