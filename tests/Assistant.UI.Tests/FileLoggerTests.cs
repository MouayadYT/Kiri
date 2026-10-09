using System.IO;
using Assistant.UI.Bootstrap;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>The log file the Assistant writes so that what went wrong can be read afterwards: a line for each event, a file a day, kept for a week, and never in the way.</summary>
public sealed class FileLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fl-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void AnEventIsAppendedAsALineWithItsOutcome_AndDebugLinesAreLeftOut()
    {
        using var provider = new FileLoggerProvider(_folder, () => _now);
        var logger = provider.CreateLogger("Assistant.Tools.Messaging.ConnectedApps.McpMessagingProvider");

        logger.Log(LogLevel.Information, new EventId(3260), "state", null, (_, _) => "Messaging through a connected app: no_chat_found");
        logger.Log(LogLevel.Debug, new EventId(1), "state", null, (_, _) => "chatter");
        logger.Log(LogLevel.Error, new EventId(2), "state", new IOException("secret path"), (_, _) => "It broke");

        var lines = File.ReadAllLines(Path.Combine(_folder, "assistant-20261004.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("INFO McpMessagingProvider[3260] Messaging through a connected app: no_chat_found", lines[0], StringComparison.Ordinal);
        Assert.Contains("ERR", lines[1], StringComparison.Ordinal);
        Assert.Contains("(IOException)", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("secret path", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOlderThanAWeekIsDeletedAtStart_AndAFileThatIsFullStopsGrowing()
    {
        Directory.CreateDirectory(_folder);
        var old = Path.Combine(_folder, "assistant-20260901.log");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, _now.UtcDateTime.AddDays(-8));
        var full = Path.Combine(_folder, "assistant-20261004.log");
        File.WriteAllBytes(full, new byte[2 * 1024 * 1024 + 1]);

        using var provider = new FileLoggerProvider(_folder, () => _now);
        provider.CreateLogger("X").Log(LogLevel.Information, new EventId(1), "s", null, (_, _) => "more");

        Assert.False(File.Exists(old));
        Assert.Equal(2 * 1024 * 1024 + 1, new FileInfo(full).Length);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenIsNotAProblem()
    {
        var blocked = Path.Combine(_folder, "file-not-folder");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(blocked, "x");

        using var provider = new FileLoggerProvider(Path.Combine(blocked, "logs"), () => _now);

        provider.CreateLogger("X").Log(LogLevel.Information, new EventId(1), "s", null, (_, _) => "never written");
    }
}
