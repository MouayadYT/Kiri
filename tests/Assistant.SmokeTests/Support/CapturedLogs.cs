using Microsoft.Extensions.Logging;

namespace Assistant.SmokeTests.Support;

/// <summary>Everything the app logs while a check runs, as the app wrote it (before the privacy filter the real app puts in front of its providers).</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];

    /// <summary>One line per message: its level, its category and its text; an exception shows as its type.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Capturing(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{logLevel} {category}: {formatter(state, null)}" + (exception is null ? "" : $" [{exception.GetType().Name}]");
            lock (owner._gate)
            {
                owner._lines.Add(line);
            }
        }
    }
}
