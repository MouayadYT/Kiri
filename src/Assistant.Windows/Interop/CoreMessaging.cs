using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.System;

namespace Assistant.Windows.Interop;

/// <summary>Creates the <see cref="DispatcherQueue"/> that Windows.UI.Composition needs on a Win32 thread.</summary>
internal static partial class CoreMessaging
{
    private const int DQTYPE_THREAD_CURRENT = 2;
    private const int DQTAT_COM_NONE = 0;

    /// <summary>Creates a queue that runs on the calling thread's existing message loop.</summary>
    public static DispatcherQueueController CreateForCurrentThread()
    {
        var options = new DispatcherQueueOptions
        {
            Size = Unsafe.SizeOf<DispatcherQueueOptions>(),
            ThreadType = DQTYPE_THREAD_CURRENT,
            ApartmentType = DQTAT_COM_NONE,
        };

        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
        try
        {
            return DispatcherQueueController.FromAbi(controller);
        }
        finally
        {
            Marshal.Release(controller);
        }
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }
}
