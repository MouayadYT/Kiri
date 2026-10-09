using Assistant.Core.Ipc;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What the app checks on the paths another process sent before it uses any (PROJECT_SPEC §4.4, §5.7).</summary>
public sealed class InvokedFilesTests
{
    private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Docs\plan.docx", @"C:\Docs\notes.md", @"C:\Pictures\cat.png", @"\\server\share\report.pdf", @"C:\Docs\tool.exe",
    };

    private static bool Exists(string path) => Existing.Contains(path);

    [Fact]
    public void FullPathsOfExistingSupportedFilesAreUsed_InOrder()
    {
        var files = InvokedFiles.Check([@"C:\Pictures\cat.png", @"C:\Docs\plan.docx", @"\\server\share\report.pdf"], Exists);
        Assert.All(files, file => Assert.True(file.IsUsable));
        Assert.Equal([@"C:\Pictures\cat.png", @"C:\Docs\plan.docx", @"\\server\share\report.pdf"], files.Select(file => file.Path));
        Assert.Equal("cat.png", files[0].Name);
    }

    [Theory]
    [InlineData("plan.docx")]
    [InlineData(@"Docs\plan.docx")]
    [InlineData(@"\Docs\plan.docx")]
    [InlineData(@"C:plan.docx")]
    [InlineData(@"\\.\C:\Docs\plan.docx")]
    [InlineData(@"\??\C:\Docs\plan.docx")]
    [InlineData(@"\\?\GLOBALROOT\Device\x\plan.docx")]
    [InlineData(@"\\?\Volume{1234}\plan.docx")]
    [InlineData("C:\\Docs\\pl|an.docx")]
    [InlineData("C:\\Docs\\plan.docx\0.txt")]
    [InlineData("   ")]
    [InlineData(@"C:\Docs\plan.docx:secret.txt")]
    [InlineData(@"\\server\share\report.pdf:x.md")]
    public void AnythingButAFullPathIsNotAPath_AndIsNeverLookedUp(string path)
    {
        var looked = new List<string>();
        var file = Assert.Single(InvokedFiles.Check([path], candidate =>
        {
            looked.Add(candidate);
            return true;
        }));
        Assert.Equal(InvokedFileProblem.NotAPath, file.Problem);
        Assert.Empty(looked);
    }

    [Fact]
    public void AFileNamedInAnotherSpellingIsTheSameFile_AndIsUsedByItsNormalPath()
    {
        var files = InvokedFiles.Check(
            [@"\\?\C:\Docs\plan.docx", "\"C:/Docs/notes.md\"", @"\\?\UNC\server\share\report.pdf", @"C:\Docs\.\plan.docx", @"c:\DOCS\plan.docx\"],
            Exists);
        Assert.Equal(
            [InvokedFileProblem.None, InvokedFileProblem.None, InvokedFileProblem.None, InvokedFileProblem.Repeated, InvokedFileProblem.Repeated],
            files.Select(file => file.Problem));
        Assert.Equal([@"C:\Docs\plan.docx", @"C:\Docs\notes.md", @"\\server\share\report.pdf"], files.Take(3).Select(file => file.Path));
    }

    [Fact]
    public void AFolderIsReportedAsOne_WhenFoldersCanBeTold()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Docs\Screenshots", @"C:\Docs\Archive.pdf" };
        var files = InvokedFiles.Check(
            [@"C:\Docs\Screenshots", @"C:\Docs\Archive.pdf", @"C:\Docs\plan.docx"], Exists, directoryExists: folders.Contains);
        Assert.Equal([InvokedFileProblem.Folder, InvokedFileProblem.Folder, InvokedFileProblem.None], files.Select(file => file.Problem));
        Assert.Equal("Screenshots", files[0].Name);

        // Without that, a folder is a name that is missing or of a type that is not read.
        Assert.Equal(InvokedFileProblem.NotSupported, InvokedFiles.Check([@"C:\Docs\Screenshots"], Exists)[0].Problem);
    }

    [Fact]
    public void MissingUnsupportedAndRepeatedFilesAreReportedEachByItsProblem()
    {
        var files = InvokedFiles.Check(
            [@"C:\Docs\gone.pdf", @"C:\Docs\tool.exe", @"C:\Docs\plan.docx", @"c:\docs\PLAN.docx", @"C:\Docs\..\Docs\plan.docx", @"C:\Docs\plan.docx:secret.txt"],
            Exists);
        Assert.Equal(
            [
                InvokedFileProblem.NotFound, InvokedFileProblem.NotSupported, InvokedFileProblem.None, InvokedFileProblem.Repeated,
                InvokedFileProblem.Repeated, InvokedFileProblem.NotAPath,
            ],
            files.Select(file => file.Problem));

        // A path with dots is used by its resolved form.
        Assert.Equal(@"C:\Docs\plan.docx", files[4].Path);
    }

    [Fact]
    public void OnlyTheFirstTenUsableFilesAreUsed_AndTheRestAreOverTheLimit()
    {
        var paths = Enumerable.Range(1, 13).Select(i => $@"C:\Docs\note{i}.md").ToList();
        paths.Insert(2, @"C:\Docs\gone.md");
        var files = InvokedFiles.Check(paths, path => path != @"C:\Docs\gone.md");
        Assert.Equal(10, files.Count(file => file.IsUsable));
        Assert.Equal(3, files.Count(file => file.Problem == InvokedFileProblem.OverLimit));
        Assert.Equal(InvokedFileProblem.NotFound, files[2].Problem);
        Assert.All(files.TakeLast(3), file => Assert.Equal(InvokedFileProblem.OverLimit, file.Problem));
    }
}
