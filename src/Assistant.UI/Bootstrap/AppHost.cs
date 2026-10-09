using Assistant.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap;

/// <summary>Builds the Generic Host that owns configuration, logging and dependency injection.</summary>
internal static class AppHost
{
#if DEBUG
    private const string DefaultEnvironment = "Development";
#else
    private const string DefaultEnvironment = "Production";
#endif

    /// <param name="logToFile">Whether the log is also written to the logs folder: the running app does, and the tests that build a host do not, so that they leave nothing in the user's own log.</param>
    public static IHost Create(bool logToFile = false)
    {
        // An empty builder: no appsettings files, environment variables or command-line configuration, and no
        // default log providers. Command-line arguments are future IPC payloads (file paths), not configuration.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = DefaultEnvironment,
        });

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        // WPF owns the application lifetime; the host's console status messages do not apply.
        builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

        ConfigureLogging(builder.Logging, builder.Environment, logToFile);

        builder.Services
            .AddAssistantServices()
            .AddUserInterface();

        return builder.Build();
    }

    // Logging: the debugger output window, stdout when it is redirected, and a file in the logs folder that can be read afterwards. Every provider sits
    // behind the privacy filter (PROJECT_SPEC §3.3).
    private static void ConfigureLogging(ILoggingBuilder logging, IHostEnvironment environment, bool logToFile)
    {
        logging
            .SetMinimumLevel(environment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information)
            .AddFilter("Microsoft", LogLevel.Warning)
            .AddDebug()
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            })
            .AddPrivacyFilter();

        if (logToFile)
        {
            logging.AddProvider(new FileLoggerProvider());
        }
    }
}
