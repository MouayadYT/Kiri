using System.Collections.Concurrent;
using Assistant.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// Keeps every log entry at every level: the formatted text, each structured value and the exception.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public string AllText => string.Join(Environment.NewLine, _entries);

    /// <summary>
    /// A factory whose loggers write here. Without the privacy filter the test sees everything the code hands to its
    /// loggers; with it, what the app's logs would hold.
    /// </summary>
    public ILoggerFactory CreateFactory(bool privacyFilter = false) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(this);
            if (privacyFilter)
            {
                builder.AddPrivacyFilter();
            }
        });

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
