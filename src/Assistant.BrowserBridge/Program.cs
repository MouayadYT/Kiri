using Assistant.BrowserBridge.Host;
using Assistant.BrowserBridge.Registration;
using Assistant.Core.Ipc;
using Assistant.Core.Storage;
using Microsoft.Win32;

namespace Assistant.BrowserBridge;

/// <summary>
/// Browser native-messaging host (PROJECT_SPEC §4.5, §5.7). A Chromium browser starts it for the extension's messages, which it reads on
/// the standard input and answers on the standard output (<see cref="Protocol.BrowserMessageProtocol"/>), handing each selection to the
/// running app over its pipe (starting the app if needed). <c>register</c> and <c>unregister</c>, which the app runs from its settings, add
/// and remove the host's registration for the current user. It has no server, no network connection, no log and no file of its own but the
/// manifest; the standard output carries nothing but replies.
/// </summary>
internal static class Program
{
    /// <summary>The command did what it was asked, or the browser closed the input.</summary>
    public const int Succeeded = 0;

    /// <summary>The command could not do it, or the browser's input or output broke.</summary>
    public const int Failed = 1;

    /// <summary>The arguments were not a command.</summary>
    public const int Usage = 2;

    /// <summary>The name of this executable.</summary>
    public const string ExecutableName = "Assistant.BrowserBridge.exe";

    // The host the manifest names: this executable, in the folder the app ships it in.
    private static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, ExecutableName);

    private static async Task<int> Main(string[] args)
    {
        var command = HostCommand.Parse(args);
        return command.Kind switch
        {
            HostCommandKind.Serve => await ServeAsync(command.PipeName).ConfigureAwait(false),
            HostCommandKind.Register => ChangeRegistration(registration =>
                registration.Register(ExecutablePath, [ExtensionIdentity.Id, .. command.ExtensionIds])),
            HostCommandKind.Unregister => ChangeRegistration(registration => registration.Unregister()),
            _ => Usage,
        };
    }

    private static async Task<int> ServeAsync(string? pipeName)
    {
        var forwarder = new InvocationForwarder(
            pipeName ?? AppPipe.ForCurrentUser(), new ProcessAppStarter(AppContext.BaseDirectory), new ForwarderOptions());
        var host = new NativeMessagingHost(forwarder.Forward);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var exit = await host.RunAsync(input, output).ConfigureAwait(false);
        return exit == HostExit.InputClosed ? Succeeded : Failed;
    }

    private static int ChangeRegistration(Action<NativeHostRegistration> change)
    {
        try
        {
            using var software = Registry.CurrentUser.CreateSubKey("Software", writable: true);
            change(new NativeHostRegistration(software, AppPaths.ForCurrentUser().BrowserBridgeDirectory));
            return Succeeded;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException
                                              or IOException or ArgumentException or InvalidOperationException)
        {
            return Failed;
        }
    }
}
