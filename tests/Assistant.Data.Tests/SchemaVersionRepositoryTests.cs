using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Assistant.Data.Tests;

public sealed class SchemaVersionRepositoryTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SchemaVersionRepository _repository = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void ANewDatabaseIsAtVersionZero_AndEnsuringTheTableTwiceIsHarmless()
    {
        using var connection = _database.Open();

        _repository.EnsureTable(connection);
        _repository.EnsureTable(connection);

        Assert.Equal(0, _repository.GetCurrentVersion(connection));
        Assert.Empty(_repository.GetHistory(connection));
        Assert.Equal(["schema_version"], connection.Tables());
    }

    [Fact]
    public void TheCurrentVersionIsTheHighestRecorded()
    {
        using var connection = _database.Open();
        _repository.EnsureTable(connection);
        var at = new DateTimeOffset(2026, 9, 30, 10, 15, 0, TimeSpan.Zero);

        _repository.Record(connection, 2, "second", at);
        _repository.Record(connection, 1, "first", at);

        Assert.Equal(2, _repository.GetCurrentVersion(connection));
    }

    [Fact]
    public void HistoryListsEachMigrationOldestFirst_WithItsTimeInUtc()
    {
        using var connection = _database.Open();
        _repository.EnsureTable(connection);
        var first = new DateTimeOffset(2026, 9, 30, 12, 15, 0, TimeSpan.FromHours(2));
        var second = first.AddDays(3);

        _repository.Record(connection, 2, "second", second);
        _repository.Record(connection, 1, "first", first);

        var history = _repository.GetHistory(connection);
        Assert.Equal([1, 2], history.Select(entry => entry.Version));
        Assert.Equal(["first", "second"], history.Select(entry => entry.Name));
        Assert.Equal(first, history[0].AppliedAt);
        Assert.Equal(TimeSpan.Zero, history[0].AppliedAt.Offset);
        Assert.Equal(second, history[1].AppliedAt);
    }

    [Fact]
    public void AVersionIsRecordedOnlyOnce()
    {
        using var connection = _database.Open();
        _repository.EnsureTable(connection);
        _repository.Record(connection, 1, "first", DateTimeOffset.UnixEpoch);

        Assert.Throws<SqliteException>(() => _repository.Record(connection, 1, "again", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ValuesTravelAsParameters_SoSqlInANameIsJustText()
    {
        using var connection = _database.Open();
        _repository.EnsureTable(connection);
        const string hostile = "x', 0); DROP TABLE schema_version; --";

        _repository.Record(connection, 1, hostile, DateTimeOffset.UnixEpoch);

        Assert.Equal(hostile, Assert.Single(_repository.GetHistory(connection)).Name);
    }

    [Fact]
    public void ANameIsRequired()
    {
        using var connection = _database.Open();
        _repository.EnsureTable(connection);

        Assert.Throws<ArgumentException>(() => _repository.Record(connection, 1, " ", DateTimeOffset.UnixEpoch));
    }
}
