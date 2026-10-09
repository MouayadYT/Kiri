using System.IO;
using Assistant.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap;

/// <summary>
/// Writes the log to a file in the Assistant's own logs folder (<c>%LOCALAPPDATA%\Assistant\logs</c>), one file a day, so that what went wrong can be read afterwards. It sits
/// behind the privacy filter like every other provider (PROJECT_SPEC §3.3): what reaches it is event ids, outcomes and counts, never a name, a message, a path or an address.
/// A file is kept for a week and stops growing at 2 MB, and a log that cannot be written is silently not written: logging never stops the Assistant.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 2L * 1024 * 1024;
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    public FileLoggerProvider(string? directory = null, Func<DateTimeOffset>? now = null)
    {
        _directory = directory ?? AppPaths.ForCurrentUser().LogsDirectory;
        _now = now ?? (() => DateTimeOffset.Now);
        Tidy();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal void Write(string category, LogLevel level, EventId id, string message, Exception? exception)
    {
        var at = _now();
        var line = $"{at:yyyy-MM-dd HH:mm:ss.fff} {Short(level)} {category}[{id.Id}] {message.ReplaceLineEndings(" ")}"
            + (exception is null ? string.Empty : " (" + exception.GetType().Name + ")") + Environment.NewLine;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"assistant-{at:yyyyMMdd}.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                {
                    return;
                }

                File.AppendAllText(path, line);
            }
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Tidy()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(_directory, "assistant-*.log"))
            {
                if (_now().UtcDateTime - File.GetLastWriteTimeUtc(file) > Keep)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception tidy) when (tidy is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Critical => "CRIT",
        LogLevel.Error => "ERR ",
        LogLevel.Warning => "WARN",
        LogLevel.Information => "INFO",
        _ => "DBG ",
    };

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(category.Split('.')[^1], logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}
