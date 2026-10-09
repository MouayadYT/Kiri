using Assistant.Search.Index;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>Which paths the user's excluded folders hold.</summary>
public sealed class ExcludedFoldersTests
{
    [Theory]
    [InlineData(@"C:\Private", true)]
    [InlineData(@"C:\Private\", true)]
    [InlineData(@"c:\private\Taxes\2025.pdf", true)]
    [InlineData(@"C:\Private\a\..\b.txt", true)]
    [InlineData(@"C:\PrivateStuff\a.txt", false)]
    [InlineData(@"C:\Public\Private\a.txt", false)]
    [InlineData(@"D:\Private\a.txt", false)]
    public void AFolderHoldsItselfAndWhatIsInsideIt(string path, bool held)
    {
        var excluded = ExcludedFolders.Create([@"C:\Private"]);

        Assert.Equal(held, excluded.Contains(path));
    }

    [Fact]
    public void ADriveRootHoldsEverythingOnTheDrive()
    {
        var excluded = ExcludedFolders.Create([@"D:\"]);

        Assert.True(excluded.Contains(@"D:\a.txt"));
        Assert.True(excluded.Contains(@"D:\"));
        Assert.False(excluded.Contains(@"C:\a.txt"));
    }

    [Fact]
    public void AFolderThatIsNotAFullPathIsLeftOut()
    {
        var excluded = ExcludedFolders.Create(["Private", "", "   ", null!, "bad\0path", @"C:\Real"]);

        Assert.Equal([(@"file:C:/Real", "file:C:/Real/")], excluded.Urls(10));
    }

    [Fact]
    public void AFolderInsideAnotherIsNotListedTwice()
    {
        var excluded = ExcludedFolders.Create([@"C:\A\B", @"C:\A", @"c:\a\"]);

        Assert.Equal([("file:C:/A", "file:C:/A/")], excluded.Urls(10));
    }

    [Fact]
    public void ARootAndAUncFolderWriteTheirUrlsTheWayTheIndexDoes()
    {
        var excluded = ExcludedFolders.Create([@"D:\", @"\\server\share\Private"]);

        Assert.Equal(
            [("file:D:/", "file:D:/"), ("file://server/share/Private", "file://server/share/Private/")],
            excluded.Urls(10));
    }

    [Fact]
    public void NoFoldersExcludeNothing()
    {
        Assert.True(ExcludedFolders.Create(null).IsEmpty);
        Assert.False(ExcludedFolders.None.Contains(@"C:\a.txt"));
    }
}
