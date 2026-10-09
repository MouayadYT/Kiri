using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Gaming;

/// <summary>
/// Is told by Windows when the window in front changes (<c>SetWinEventHook</c> for <c>EVENT_SYSTEM_FOREGROUND</c>), instead of looking again and
/// again. It owns a thread of its own that sleeps in its message queue until Windows has something to say, so between two changes of the
/// window in front it uses no processor time at all. Nothing is loaded into other programs (the hook is out of context), and what is reported
/// is the window's handle and nothing of what it shows.
/// </summary>
internal sealed unsafe class ForegroundWatcher : IDisposable
{
    // The running watchers by their hook, so that the callback, which has no instance, finds its watcher.
    private static readonly ConcurrentDictionary<nint, ForegroundWatcher> Watchers = new();

    private readonly Action<nint> _changed;
    private readonly Thread _thread;
    private uint _threadId;
    private int _disposed;

    /// <summary>Starts watching; <paramref name="changed"/> is called on the watcher's thread with the window that came to the front.</summary>
    public ForegroundWatcher(Action<nint> changed)
    {
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "Assistant game detector",
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
        {
            User32.PostThreadMessage(threadId, User32.WM_QUIT, 0, 0);
        }

        // The callback may be the one disposing: a thread does not wait for itself.
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    private void Run(ManualResetEventSlim ready)
    {
        nint hook = 0;
        try
        {
            // Setting the hook also makes this thread's message queue, which is where Windows leaves the events and where Dispose leaves the quit.
            hook = User32.SetWinEventHook(
                User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, 0, &OnEvent, 0, 0, User32.WINEVENT_OUTOFCONTEXT);
            if (hook != 0)
            {
                Watchers[hook] = this;
            }

            Volatile.Write(ref _threadId, Kernel32.GetCurrentThreadId());
            ready.Set();
            if (hook == 0)
            {
                return;
            }

            while (User32.GetMessage(out var message, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(in message);
                User32.DispatchMessage(in message);
            }
        }
        finally
        {
            if (hook != 0)
            {
                Watchers.TryRemove(hook, out _);
                User32.UnhookWinEvent(hook);
            }

            // Whatever happened, whoever waits for the hook is let go.
            ready.Set();
        }
    }

    // Windows calls this on the watcher's thread, when it takes its messages.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (window == 0 || !Watchers.TryGetValue(hook, out var watcher) || Volatile.Read(ref watcher._disposed) != 0)
        {
            return;
        }

        try
        {
            watcher._changed(window);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An exception must not cross into Windows, which called this. The next change of the window in front is looked at afresh.
        }
    }
}
