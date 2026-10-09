using System.ComponentModel;
using System.Diagnostics;
using Assistant.Windows.Shell;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>Opening a result and showing it in File Explorer: only a full path that exists is handed to the shell.</summary>
public sealed class ShellFileLauncherTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "assistant-launcher-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingStarter _starter = new();

    public ShellFileLauncherTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
    }

    private string CreateFile(string name = "Budget 2026.txt")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void OpeningAFileHandsItsPathToTheShellToOpenWithItsOwnApp()
    {
        var path = CreateFile();

        Assert.True(new ShellFileLauncher(_starter).Open(path));

        var info = Assert.Single(_starter.Started);
        Assert.Equal(path, info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Equal(_folder, info.WorkingDirectory);
    }

    [Fact]
    public void OpeningAFolderOpensItInExplorer()
    {
        Assert.True(new ShellFileLauncher(_starter).Open(_folder));

        var info = Assert.Single(_starter.Started);
        Assert.Equal(_folder, info.FileName);
        Assert.True(info.UseShellExecute);
    }

    [Fact]
    public void RevealingAFileSelectsItInExplorerWithItsPathQuoted()
    {
        var path = CreateFile("My notes.txt");

        Assert.True(new ShellFileLauncher(_starter).Reveal(path));

        var info = Assert.Single(_starter.Started);
        Assert.Equal("explorer.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal($"/select,\"{path}\"", info.Arguments);
        Assert.Equal($"/select,\"{path}\"", ShellFileLauncher.RevealArguments(path));
    }

    [Fact]
    public void RevealingAFolderSelectsItToo()
    {
        Assert.True(new ShellFileLauncher(_starter).Reveal(_folder));

        Assert.Equal($"/select,\"{_folder}\"", Assert.Single(_starter.Started).Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\file.txt")]
    [InlineData("file.txt")]
    [InlineData(@"C:\definitely\not\here\missing.txt")]
    [InlineData("C:\folder\\\"quoted\".txt")]
    [InlineData("cmd.exe")]
    public void NothingIsHandedToTheShellForAPathThatIsNotAFullPathThatExists(string? path)
    {
        var launcher = new ShellFileLauncher(_starter);

        Assert.False(launcher.Open(path!));
        Assert.False(launcher.Reveal(path!));
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public void AShellThatCannotStartItIsAFailureAndNotAnException()
    {
        var path = CreateFile();
        _starter.Failure = new Win32Exception(2);

        var launcher = new ShellFileLauncher(_starter);

        Assert.False(launcher.Open(path));
        Assert.False(launcher.Reveal(path));
        _starter.Failure = new InvalidOperationException();
        Assert.False(launcher.Open(path));
    }

    private sealed class RecordingStarter : ShellFileLauncher.IProcessStarter
    {
        public List<ProcessStartInfo> Started { get; } = [];

        public Exception? Failure { get; set; }

        public bool Start(ProcessStartInfo info)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Started.Add(info);
            return true;
        }
    }
}
