using System.Diagnostics;
using Assistant.Core.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class TemporaryCaptureCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-cleaner-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public TemporaryCaptureCleanerTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string InCaptures(string name) => Path.Combine(_paths.TemporaryCapturesDirectory, name);

    [Fact]
    public void DeletesEveryFileAndFolderInTheCapturesFolder_AndNothingElse()
    {
        File.WriteAllBytes(InCaptures("a.png"), [1]);
        File.WriteAllBytes(InCaptures("b.tmp"), [2]);
        Directory.CreateDirectory(InCaptures("nested"));
        File.WriteAllBytes(Path.Combine(InCaptures("nested"), "c.png"), [3]);
        File.WriteAllText(_paths.SettingsFilePath, "{}");
        File.WriteAllBytes(Path.Combine(_paths.CacheDirectory, "kept.bin"), [4]);

        var deleted = new TemporaryCaptureCleaner(_paths).DeleteAll();

        Assert.Equal(3, deleted);
        Assert.Empty(Directory.GetFileSystemEntries(_paths.TemporaryCapturesDirectory));
        Assert.True(Directory.Exists(_paths.TemporaryCapturesDirectory));
        Assert.True(File.Exists(_paths.SettingsFilePath));
        Assert.True(File.Exists(Path.Combine(_paths.CacheDirectory, "kept.bin")));
    }

    [Fact]
    public void ReadOnlyFilesGoToo()
    {
        File.WriteAllBytes(InCaptures("locked-down.png"), [1]);
        File.SetAttributes(InCaptures("locked-down.png"), FileAttributes.ReadOnly);

        Assert.Equal(1, new TemporaryCaptureCleaner(_paths).DeleteAll());

        Assert.Empty(Directory.GetFileSystemEntries(_paths.TemporaryCapturesDirectory));
    }

    [Fact]
    public void AFileThatIsInUse_IsLeftForTheNextTime_AndTheRestGo()
    {
        File.WriteAllBytes(InCaptures("free.png"), [1]);
        File.WriteAllBytes(InCaptures("busy.png"), [2]);
        using (new FileStream(InCaptures("busy.png"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, new TemporaryCaptureCleaner(_paths).DeleteAll());
            Assert.Equal([InCaptures("busy.png")], Directory.GetFileSystemEntries(_paths.TemporaryCapturesDirectory));
        }

        Assert.Equal(1, new TemporaryCaptureCleaner(_paths).DeleteAll());
        Assert.Empty(Directory.GetFileSystemEntries(_paths.TemporaryCapturesDirectory));
    }

    [Fact]
    public void AFolderThatIsNotThere_HasNothingToDelete()
    {
        Directory.Delete(_paths.TemporaryCapturesDirectory);

        Assert.Equal(0, new TemporaryCaptureCleaner(_paths).DeleteAll());
    }

    [Fact]
    public void ALinkIsRemoved_ButWhatItLeadsToIsNotTouched()
    {
        var outside = Path.Combine(_root, "somewhere-else");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "precious.txt"), [1]);
        if (!TryMakeJunction(InCaptures("link"), outside))
        {
            return;
        }

        Assert.Equal(1, new TemporaryCaptureCleaner(_paths).DeleteAll());

        Assert.Empty(Directory.GetFileSystemEntries(_paths.TemporaryCapturesDirectory));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public void ItLogsHowManyWereDeleted_NeverTheirNames()
    {
        File.WriteAllBytes(InCaptures("my-secret-bank-statement.png"), [1]);
        var log = new LineLog<TemporaryCaptureCleaner>();

        new TemporaryCaptureCleaner(_paths, log).DeleteAll();

        var line = Assert.Single(log.Lines);
        Assert.Contains("1 deleted", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNothingToDelete_ItSaysNothing()
    {
        var log = new LineLog<TemporaryCaptureCleaner>();

        new TemporaryCaptureCleaner(_paths, log).DeleteAll();

        Assert.Empty(log.Lines);
    }

    // A directory junction needs no privilege, where a symbolic link does; false when it could not be made.
    private static bool TryMakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private sealed class LineLog<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
