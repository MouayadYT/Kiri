using Assistant.Core.Diagnostics;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost;

/// <summary>Builds the Generic Host that owns the model host's logging, dependency injection and lifetime.</summary>
internal static class ModelHostApp
{
#if DEBUG
    private const string DefaultEnvironment = "Development";
#else
    private const string DefaultEnvironment = "Production";
#endif

    public static IHost Create(ModelHostArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // An empty builder: no appsettings files, environment variables or command-line configuration, and no default
        // log providers. The command line belongs to the owner (ModelHostArguments), not to configuration.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant.ModelHost",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = DefaultEnvironment,
        });

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        // The process has no console; the owner decides when it stops.
        builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

        ConfigureLogging(builder.Logging, builder.Environment);
        builder.Services.AddModelHost(new ModelHostOptions(arguments.PipeName, arguments.OwnerProcessId));
        return builder.Build();
    }

    // Development logging, as in the app: the debugger output window, plus stdout when the owner's is redirected. Every
    // provider sits behind the privacy filter (PROJECT_SPEC §3.3), and the host never logs prompts or output.
    private static void ConfigureLogging(ILoggingBuilder logging, IHostEnvironment environment)
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
    }
}
