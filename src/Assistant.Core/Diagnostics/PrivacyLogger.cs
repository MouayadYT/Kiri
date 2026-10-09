using Microsoft.Extensions.Logging;

namespace Assistant.Core.Diagnostics;

/// <summary>Applies <see cref="LogPrivacy"/> to everything written through another logger, including scopes.</summary>
internal sealed class PrivacyLogger(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        inner.BeginScope(RedactedLogState.From(state));

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!inner.IsEnabled(logLevel))
        {
            return;
        }

        inner.Log(
            logLevel,
            eventId,
            RedactedLogState.From(state),
            exception is null ? null : SanitizedException.From(exception),
            RedactedLogState.Formatter);
    }
}
