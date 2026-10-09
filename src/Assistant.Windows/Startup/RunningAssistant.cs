using System.IO.Pipes;
using System.Runtime.InteropServices;
using Assistant.Core.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Assistant.Windows.Startup;

/// <summary>
/// The Assistant that is already running, as a second start of the app sees it (PROJECT_SPEC §4.9): the app is opened again from Start or its
/// shortcut while it waits in the notification area. The second start asks the first, over the app's own pipe, to show its full window, lets it
/// come to the front, and ends; so opening the app always shows it, and there is only ever one of it.
/// </summary>
public static partial class RunningAssistant
{
    /// <summary>How long a running app is given to take the connection. With no app running, this is how long the start waits to find that out.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a running app is given to answer.</summary>
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Asks the app serving <paramref name="pipeName"/> to show its full window. <see langword="true"/> when one is running and took the request;
    /// <see langword="false"/> when none is (or it is an older one that does not know the request), and this start should go on to be the app.
    /// </summary>
    public static bool TryShowFullView(string pipeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, LocalPipe.Options);
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);

            // Windows lets a window take the foreground only from the process that has it, or one that process allows: this start has it (the user has
            // just opened it), and gives it to the app that will show the window.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            {
                AllowSetForegroundWindow(processId);
            }

            using var timeout = new CancellationTokenSource(ReplyTimeout);
            return InvocationClient.SendAsync(pipe, InvocationRequest.ShowFullView, timeout.Token).GetAwaiter().GetResult().IsAccepted;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or IpcProtocolException or OperationCanceledException)
        {
            // Nothing is serving the pipe, or what is did not answer as the app does.
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
