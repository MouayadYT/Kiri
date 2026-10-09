using Assistant.Core.Storage;
using Xunit;

namespace Assistant.Data.Tests;

public sealed class DatabaseOptionsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "assistant-options-tests");

    [Fact]
    public void ForAppPaths_PutsTheDatabaseAndItsBackupsInTheDataDirectory()
    {
        var paths = new AppPaths(Root);

        var options = DatabaseOptions.For(paths);

        Assert.Equal(Path.Combine(paths.DatabaseDirectory, "assistant.db"), options.DatabasePath);
        Assert.Equal(Path.Combine(paths.DatabaseDirectory, "backups"), options.BackupsDirectory);
        Assert.Equal(DatabaseOptions.DefaultBackupsToKeep, options.BackupsToKeep);
    }

    [Fact]
    public void RefusesRelativePaths()
    {
        var absolute = Path.Combine(Root, "x");

        Assert.Throws<ArgumentException>(() => new DatabaseOptions("assistant.db", absolute));
        Assert.Throws<ArgumentException>(() => new DatabaseOptions(absolute, "backups"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void KeepsAtLeastOneBackup(int backupsToKeep)
    {
        var absolute = Path.Combine(Root, "x");

        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseOptions(absolute, absolute, backupsToKeep));
    }
}
