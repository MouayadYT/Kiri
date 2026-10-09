using Assistant.Core.Ipc;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The one written form of a path (PROJECT_SPEC §4.4, §5.7).</summary>
public sealed class InvokedPathsTests
{
    [Theory]
    [InlineData(@"C:\Users\Ana\a.png", @"C:\Users\Ana\a.png")]
    [InlineData("  \"C:\\Users\\Ana\\a.png\"  ", @"C:\Users\Ana\a.png")]
    [InlineData("C:/Users/Ana/a.png", @"C:\Users\Ana\a.png")]
    [InlineData(@"c:\Users\Ana\..\Bo\.\a.png", @"c:\Users\Bo\a.png")]
    [InlineData(@"C:\Users\Ana\", @"C:\Users\Ana")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\?\C:\Users\Ana\a.png", @"C:\Users\Ana\a.png")]
    [InlineData(@"\\?\UNC\server\share\a.png", @"\\server\share\a.png")]
    [InlineData(@"\\?\unc\server\share\a.png", @"\\server\share\a.png")]
    [InlineData("//server/share/a.png", @"\\server\share\a.png")]
    [InlineData(@"C:\Users\Ana\caf" + "\u00e9" + @"\Lisbon photo (1).png", @"C:\Users\Ana\caf" + "\u00e9" + @"\Lisbon photo (1).png")]
    public void APathIsWrittenInItsNormalForm(string path, string expected) => Assert.Equal(expected, InvokedPaths.Normalize(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a.png")]
    [InlineData(@"..\a.png")]
    [InlineData(@"\a.png")]
    [InlineData(@"C:a.png")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\a.png")]
    [InlineData(@"\??\C:\a.png")]
    [InlineData(@"C:\a.png:stream")]
    [InlineData("C:\\a|b.png")]
    [InlineData("file:///C:/a.png")]
    public void WhatIsNotAnOrdinaryFullPathIsNotAPath(string? path) => Assert.Null(InvokedPaths.Normalize(path));

    [Fact]
    public void ANormalPathStaysTheSameWhenNormalizedAgain_AndTheLongestPathIsTheLimit()
    {
        var path = InvokedPaths.Normalize(@"\\?\C:\a\.\b\..\c.png");
        Assert.Equal(@"C:\a\c.png", path);
        Assert.Equal(path, InvokedPaths.Normalize(path));

        // Paths past the old 260-character limit are ordinary paths; ones past the longest a path can be are not paths.
        var long300 = @"C:\Users\Ana\" + string.Join('\\', Enumerable.Repeat(new string('a', 40), 7)) + @"\a.png";
        Assert.True(long300.Length > 260);
        Assert.Equal(long300, InvokedPaths.Normalize(long300));
        Assert.Equal(long300, InvokedPaths.Normalize(@"\\?\" + long300));
        Assert.Null(InvokedPaths.Normalize("C:\\" + new string('a', InvocationProtocol.MaxPathLength)));
    }
}
