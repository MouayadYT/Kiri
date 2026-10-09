using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Diagnostics;

/// <summary>Hands out <see cref="PrivacyLogger"/>s over the loggers of another factory.</summary>
internal sealed class PrivacyLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (name, factory) => new PrivacyLogger(factory.CreateLogger(name)), inner);

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose()
    {
        // The inner factory is registered in the container, which disposes it.
    }
}
