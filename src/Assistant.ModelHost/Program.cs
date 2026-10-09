using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Assistant.ModelHost;

/// <summary>
/// Entry point of the model host: the hidden process that runs local inference for the desktop app, which starts it
/// and owns it (PROJECT_SPEC §5.6). It serves the model-host protocol on the pipe its command line names, to its owner
/// only, and exits when the owner disconnects, asks it to shut down, or exits.
/// </summary>
internal static class Program
{
    /// <summary>
    /// The exit code when the process was started without its owner's arguments, for example by opening its file.
    /// </summary>
    internal const int InvalidArgumentsExitCode = 2;

    private static async Task<int> Main(string[] args)
    {
        if (!ModelHostArguments.TryParse(args, out var arguments))
        {
            return InvalidArgumentsExitCode;
        }

        using var host = ModelHostApp.Create(arguments);

        // Resolved first: running the host disposes its services when it stops.
        var service = host.Services.GetRequiredService<ModelHostService>();
        await host.RunAsync().ConfigureAwait(false);
        return service.ExitCode;
    }
}
