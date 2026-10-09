using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Assistant.Data.Tests;

/// <summary>
/// Keeps every log entry at every level: the formatted text, each structured value and the exception, so a test sees
/// everything the code hands to its loggers.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public string AllText => string.Join(Environment.NewLine, _entries);

    public ILoggerFactory CreateFactory() =>
        LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(this));

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            owner._entries.Enqueue($"{category} {logLevel} {eventId.Id} {formatter(state, exception)} {exception}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (name, value) in values)
                {
                    owner._entries.Enqueue($"  {name} = {value}");
                }
            }
        }
    }
}
