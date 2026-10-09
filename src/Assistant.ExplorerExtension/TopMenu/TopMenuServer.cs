using System.Runtime.InteropServices.Marshalling;

namespace Assistant.ExplorerExtension.TopMenu;

/// <summary>
/// The COM server File Explorer starts (from the package manifest) to ask <see cref="AskAssistantCommand"/> about a selection.
/// It registers the class, serves requests on COM's own threads, and ends once nothing has asked for a while.
/// </summary>
internal static unsafe class TopMenuServer
{
    /// <summary>The argument the package manifest passes, to tell a server start from a menu command.</summary>
    public const string ServeName = "serve";

    /// <summary>How long the server stays after the last request.</summary>
    public static readonly TimeSpan IdleTime = TimeSpan.FromSeconds(45);

    private const uint LocalServer = 0x4;
    private const uint MultipleUse = 0x1;
    private const uint MultiThreaded = 0x0;

    /// <summary>Whether the arguments are the server's: <c>serve</c>, or COM's <c>-Embedding</c>.</summary>
    public static bool IsServerStart(IReadOnlyList<string> args) =>
        args.Any(arg => arg.Equals(ServeName, StringComparison.OrdinalIgnoreCase)
                        || arg.Equals("-Embedding", StringComparison.OrdinalIgnoreCase)
                        || arg.Equals("/Embedding", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the server until it has been idle for <see cref="IdleTime"/>.</summary>
    public static int Run()
    {
        var last = Environment.TickCount64;
        AskAssistantCommand.Touched = () => Interlocked.Exchange(ref last, Environment.TickCount64);

        var result = ComNative.EFail;
        var thread = new Thread(() =>
        {
            ComNative.CoInitializeEx(0, MultiThreaded);
            var classId = AskAssistantCommand.ClassId;
            var factory = ComInterfaceMarshaller<IClassFactory>.ConvertToUnmanaged(new AskAssistantCommandFactory());
            if (ComNative.CoRegisterClassObject(classId, (nint)factory, LocalServer, MultipleUse, out var cookie) != ComNative.SOk)
            {
                return;
            }

            result = ComNative.SOk;
            while (Environment.TickCount64 - Interlocked.Read(ref last) < IdleTime.TotalMilliseconds)
            {
                Thread.Sleep(1000);
            }

            ComNative.CoRevokeClassObject(cookie);
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return result == ComNative.SOk ? Program.Succeeded : Program.Failed;
    }
}
