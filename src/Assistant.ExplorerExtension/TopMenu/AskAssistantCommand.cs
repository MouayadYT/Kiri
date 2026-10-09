using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Assistant.Core.Contracts;
using Assistant.Core.Ipc;
using Assistant.ExplorerExtension.Forwarding;
using Assistant.ExplorerExtension.Registration;

namespace Assistant.ExplorerExtension.TopMenu;

/// <summary>
/// Ask Assistant in File Explorer's Windows 11 first menu (PROJECT_SPEC §4.4). Explorer asks this <c>IExplorerCommand</c> whether to
/// show itself for the selection, and runs it with the whole selection at once, so no per-file processes are involved. It is hosted
/// by this entry point's own COM server (<see cref="TopMenuServer"/>), never inside <c>explorer.exe</c>.
/// </summary>
[GeneratedComClass]
internal sealed partial class AskAssistantCommand : IExplorerCommand
{
    /// <summary>The command's class ID, the one the package manifest names.</summary>
    public static readonly Guid ClassId = new("6c9f2f4b-5b1e-4a53-9a3e-2f0d7a5b8c11");

    private const int CommandEnabled = 0;
    private const int CommandHidden = 2;

    /// <summary>Called after each request File Explorer makes, so the server can end when it is idle.</summary>
    public static Action Touched { get; set; } = () => { };

    /// <summary>The menu text.</summary>
    public int GetTitle(nint items, out nint name)
    {
        name = Marshal.StringToCoTaskMemUni(ExplorerMenuRegistration.Title);
        return ComNative.SOk;
    }

    /// <summary>The Assistant's mark, in the colour that suits the menu.</summary>
    public int GetIcon(nint items, out nint icon)
    {
        var name = ExplorerMenuRegistration.SystemUsesLightTheme()
            ? ExplorerMenuRegistration.LightMenuIconName
            : ExplorerMenuRegistration.DarkMenuIconName;
        var path = Path.Combine(AppContext.BaseDirectory, name);
        icon = File.Exists(path) ? Marshal.StringToCoTaskMemUni(path) : 0;
        return icon == 0 ? ComNative.EFail : ComNative.SOk;
    }

    /// <summary>No tooltip.</summary>
    public int GetToolTip(nint items, out nint tip)
    {
        tip = 0;
        return ComNative.ENotImpl;
    }

    /// <summary>No canonical name.</summary>
    public int GetCanonicalName(out Guid name)
    {
        name = ClassId;
        return ComNative.SOk;
    }

    /// <summary>Shown when every selected item is a file the Assistant can read, hidden otherwise.</summary>
    public int GetState(IShellItemArray? items, int okToBeSlow, out int state)
    {
        Touched();
        state = CommandHidden;
        if (items is null)
        {
            return ComNative.SOk;
        }

        var paths = ReadPaths(items);
        state = paths.Count > 0 && paths.All(ExplorerFileTypes.IsSupported) ? CommandEnabled : CommandHidden;
        return ComNative.SOk;
    }

    /// <summary>Hands the selection to the app.</summary>
    public int Invoke(IShellItemArray? items, nint bindContext)
    {
        Touched();
        if (items is null)
        {
            return ComNative.EFail;
        }

        var paths = ReadPaths(items).Take(InvocationProtocol.MaxPaths).ToArray();
        if (paths.Length == 0)
        {
            return ComNative.EFail;
        }

        var forwarder = new InvocationForwarder(
            AppPipe.ForCurrentUser(), new ProcessAppStarter(AppContext.BaseDirectory), new ForwarderOptions(), AllowAppForeground);
        var result = forwarder.Forward(new InvocationRequest(InvocationAction.AskAboutFiles, paths));
        if (result != ForwardResult.Forwarded)
        {
            FailureMessage.Show(result);
        }

        Touched();
        return ComNative.SOk;
    }

    /// <summary>A plain command: no flags.</summary>
    public int GetFlags(out int flags)
    {
        flags = 0;
        return ComNative.SOk;
    }

    /// <summary>No sub-commands.</summary>
    public int EnumSubCommands(out nint commands)
    {
        commands = 0;
        return ComNative.ENotImpl;
    }

    private static List<string> ReadPaths(IShellItemArray items)
    {
        var paths = new List<string>();
        if (items.GetCount(out var count) != ComNative.SOk)
        {
            return paths;
        }

        for (uint index = 0; index < count && paths.Count < InvocationProtocol.MaxPaths; index++)
        {
            if (items.GetItemAt(index, out var item) != ComNative.SOk || item is null)
            {
                continue;
            }

            if (item.GetDisplayName(ComNative.FileSystemPath, out var raw) == ComNative.SOk && raw != 0)
            {
                try
                {
                    if (Marshal.PtrToStringUni(raw) is { Length: > 0 } path)
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    ComNative.CoTaskMemFree(raw);
                }
            }
        }

        return paths;
    }

    private static void AllowAppForeground(System.IO.Pipes.NamedPipeClientStream pipe)
    {
        if (NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var appProcessId))
        {
            NativeMethods.AllowSetForegroundWindow(appProcessId);
        }
    }
}

/// <summary>Makes <see cref="AskAssistantCommand"/> for File Explorer.</summary>
[GeneratedComClass]
internal sealed unsafe partial class AskAssistantCommandFactory : IClassFactory
{
    private static readonly Guid IUnknownId = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid ExplorerCommandId = new("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");

    /// <inheritdoc/>
    public int CreateInstance(nint outer, in Guid riid, out nint instance)
    {
        instance = 0;
        if (outer != 0)
        {
            return ComNative.ClassNoAggregation;
        }

        if (riid != IUnknownId && riid != ExplorerCommandId)
        {
            return ComNative.ENoInterface;
        }

        instance = (nint)ComInterfaceMarshaller<IExplorerCommand>.ConvertToUnmanaged(new AskAssistantCommand());
        return ComNative.SOk;
    }

    /// <inheritdoc/>
    public int LockServer(int lockServer) => ComNative.SOk;
}
