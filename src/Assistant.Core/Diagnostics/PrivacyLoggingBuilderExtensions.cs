using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Diagnostics;

/// <summary>Registers the logging privacy filter.</summary>
public static class PrivacyLoggingBuilderExtensions
{
    /// <summary>
    /// Routes every logger resolved from the container through <see cref="LogPrivacy"/>, whichever providers are
    /// configured.
    /// </summary>
    public static ILoggingBuilder AddPrivacyFilter(this ILoggingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.RemoveAll<ILoggerFactory>();
        builder.Services.TryAddSingleton<LoggerFactory>();
        builder.Services.AddSingleton<ILoggerFactory>(
            services => new PrivacyLoggerFactory(services.GetRequiredService<LoggerFactory>()));
        return builder;
    }
}
