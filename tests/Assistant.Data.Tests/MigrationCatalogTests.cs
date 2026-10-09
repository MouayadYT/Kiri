using Assistant.Data.Migrations;
using Xunit;

namespace Assistant.Data.Tests;

public sealed class MigrationCatalogTests
{
    private static Migration Step(int version, string name = "step", string sql = "SELECT 1") => new(version, name, sql);

    [Fact]
    public void TheAppsMigrationsAreEmbeddedInOrderStartingWithTheInitialSchema()
    {
        var catalog = MigrationCatalog.LoadDefault();

        Assert.Equal(Enumerable.Range(1, catalog.Migrations.Count), catalog.Migrations.Select(migration => migration.Version));
        Assert.Equal("initial_schema", catalog.Migrations[0].Name);
        Assert.Equal(catalog.Migrations.Count, catalog.LatestVersion);
        Assert.All(catalog.Migrations, migration => Assert.False(string.IsNullOrWhiteSpace(migration.Sql)));
    }

    [Fact]
    public void AfterListsTheMigrationsADatabaseStillNeeds()
    {
        var catalog = new MigrationCatalog([Step(1), Step(2), Step(3)]);

        Assert.Equal([1, 2, 3], catalog.After(0).Select(migration => migration.Version));
        Assert.Equal([3], catalog.After(2).Select(migration => migration.Version));
        Assert.Empty(catalog.After(3));
        Assert.Empty(catalog.After(9));
        Assert.Equal(3, catalog.LatestVersion);
    }

    [Fact]
    public void ACatalogNeedsMigrations()
    {
        Assert.Throws<ArgumentException>(() => new MigrationCatalog([]));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void VersionsMustBeOneTwoThreeInOrder(int first, int second)
    {
        Assert.Throws<ArgumentException>(() => new MigrationCatalog([Step(first), Step(second)]));
    }

    [Fact]
    public void AMigrationNeedsANameAndAScript()
    {
        Assert.Throws<ArgumentException>(() => new MigrationCatalog([Step(1, name: " ")]));
        Assert.Throws<ArgumentException>(() => new MigrationCatalog([Step(1, sql: "")]));
    }

    [Theory]
    [InlineData("migrations/0001_initial_schema.sql", 1, "initial_schema")]
    [InlineData("migrations/0012_add_notes2.sql", 12, "add_notes2")]
    public void ReadsTheVersionAndNameOutOfAScriptsName(string resource, int version, string name)
    {
        Assert.True(MigrationScripts.TryParseName(resource, out var parsedVersion, out var parsedName));
        Assert.Equal(version, parsedVersion);
        Assert.Equal(name, parsedName);
    }

    [Theory]
    [InlineData("migrations/1_initial.sql")]
    [InlineData("migrations/0001_Initial.sql")]
    [InlineData("migrations/0001_.sql")]
    [InlineData("migrations/0001_initial.txt")]
    [InlineData("migrations/0001 initial.sql")]
    [InlineData("0001_initial.sql")]
    public void RefusesScriptsThatAreNotNamedLikeMigrations(string resource)
    {
        Assert.False(MigrationScripts.TryParseName(resource, out _, out _));
    }
}
