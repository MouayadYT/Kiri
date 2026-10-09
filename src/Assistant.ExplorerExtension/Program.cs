using Assistant.Core.Contracts;
using Assistant.Core.Ipc;
using Assistant.ExplorerExtension.Forwarding;
using Assistant.ExplorerExtension.Registration;
using Assistant.ExplorerExtension.Selection;
using Assistant.ExplorerExtension.TopMenu;
using Microsoft.Win32;

namespace Assistant.ExplorerExtension;

/// <summary>
/// Out-of-process File Explorer entry point for selected-file actions (PROJECT_SPEC §4.4). It never runs inside
/// <c>explorer.exe</c> and does no work of its own: <c>ask "&lt;file&gt;"</c>, which File Explorer's Ask Assistant runs, hands the
/// file to the running app over its pipe (starting the app if needed) and exits; <c>register</c> and <c>unregister</c>, which the
/// app runs from its settings, add and remove that menu entry for the current user. It reads no file, keeps no log, and
/// shows a message only when the app cannot be reached.
/// </summary>
internal static class Program
{
    /// <summary>The command did what it was asked.</summary>
    public const int Succeeded = 0;

    /// <summary>The command could not do it.</summary>
    public const int Failed = 1;

    /// <summary>The arguments were not a command.</summary>
    public const int Usage = 2;

    /// <summary>The name of this entry point's executable.</summary>
    public const string ExecutableName = "Assistant.ExplorerExtension.exe";

    // The executable the menu entry runs: this one, in the folder the app ships it in.
    private static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, ExecutableName);

    [STAThread]
    private static int Main(string[] args)
    {
        if (TopMenuServer.IsServerStart(args))
        {
            return TopMenuServer.Run();
        }

        var command = ExtensionCommand.Parse(args);
        return command.Kind switch
        {
            ExtensionCommandKind.Ask => Ask(command.Paths),
            ExtensionCommandKind.Register => RegisterEverywhere(),
            ExtensionCommandKind.Unregister => UnregisterEverywhere(),
            _ => Usage,
        };
    }

    private static int Ask(IReadOnlyList<string> paths)
    {
        // This process was started by File Explorer while the user was using it, so it may let the app take the foreground.
        var forwarder = new InvocationForwarder(
            AppPipe.ForCurrentUser(), new ProcessAppStarter(AppContext.BaseDirectory), new ForwarderOptions(), AllowAppForeground);
        var handler = new AskHandler(
            new ExplorerWindowSelection(), new NamedMutexSelectionGate(), forwarder, Thread.Sleep, AskHandler.DefaultClaimHold);
        var outcome = handler.Run(paths);
        if (outcome.Result is { } result)
        {
            FailureMessage.Show(result);
        }

        return outcome.Result is null or ForwardResult.Forwarded ? Succeeded : Failed;
    }

    private static void AllowAppForeground(System.IO.Pipes.NamedPipeClientStream pipe)
    {
        if (NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var appProcessId))
        {
            NativeMethods.AllowSetForegroundWindow(appProcessId);
        }
    }

    // The classic entry ("Show more options") always; the Windows 11 first-menu entry too, when Windows accepts the package
    // (it needs Developer Mode or a trusted signature). Without it the classic entry is what the user has.
    private static int RegisterEverywhere()
    {
        var classic = ChangeRegistration(registration => registration.Register(ExecutablePath, ExplorerFileTypes.Extensions));
        TopMenuPackage.Register(AppContext.BaseDirectory, ExecutableName);
        return classic;
    }

    private static int UnregisterEverywhere()
    {
        var classic = ChangeRegistration(registration => registration.Unregister());
        return TopMenuPackage.Unregister() ? classic : Failed;
    }

    private static int ChangeRegistration(Action<ExplorerMenuRegistration> change)
    {
        try
        {
            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true))
            {
                change(new ExplorerMenuRegistration(classes));
            }

            // File Explorer reads the verbs again for menus it opens from now on.
            NativeMethods.SHChangeNotify(NativeMethods.ShcneAssocChanged, NativeMethods.ShcnfIdList, 0, 0);
            return Succeeded;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException
                                              or IOException or ArgumentException)
        {
            return Failed;
        }
    }
}
