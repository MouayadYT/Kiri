using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.Windows.Interop;
using Assistant.Windows.Selection;

namespace Assistant.Windows.Clipboard;

/// <summary>
/// The real clipboard as the watcher reads it: the Win32 clipboard, opened for the moment it takes to look and closed again. It is only
/// ever asked for text and for the flags applications put beside it.
/// </summary>
internal sealed unsafe class WindowsClipboardAccess : IClipboardAccess
{
    private readonly ClipboardNativeMethods _clipboard = new();

    /// <inheritdoc/>
    public bool HasRegisteredFormat(string name)
    {
        var format = User32.RegisterClipboardFormat(name);
        return format != 0 && User32.IsClipboardFormatAvailable(format);
    }

    /// <inheritdoc/>
    public int? ReadFlag(string name)
    {
        var format = User32.RegisterClipboardFormat(name);
        if (format == 0 || !User32.IsClipboardFormatAvailable(format) || !OpenWithPatience())
        {
            return null;
        }

        try
        {
            var handle = User32.GetClipboardData(format);
            if (handle == 0 || Kernel32.GlobalSize(handle) < sizeof(int))
            {
                return null;
            }

            var memory = Kernel32.GlobalLock(handle);
            if (memory == 0)
            {
                return null;
            }

            try
            {
                return *(int*)memory;
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    /// <inheritdoc/>
    public ClipboardTextOutcome ReadText(int maxLength, out string? text) => _clipboard.ReadText(maxLength, out text);

    // Another application may have the clipboard open for a moment, and it is asked again a few times, briefly.
    private static bool OpenWithPatience()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (User32.OpenClipboard(0))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }
}

/// <summary>
/// Notices that the user copied text (<see cref="IClipboardWatcher"/>), by asking Windows to say whenever the clipboard changes
/// (<c>AddClipboardFormatListener</c>) and never by looking again and again. It owns a thread of its own with a window that has no
/// face (a message-only window), which exists only between <see cref="Start"/> and <see cref="Stop"/>. After a change it waits a
/// moment for the copying application to finish, then reads the text only if it is fit to keep (<see cref="ClipboardTextFilter"/>),
/// and passes it on. What is copied is private content and is never logged; nothing is written anywhere.
/// </summary>
public sealed class ClipboardWatcher : IClipboardWatcher
{
    private const string WindowClassName = "Assistant.ClipboardWatcher";
    private const nuint SettleTimer = 1;
    private const uint SettleMilliseconds = 150;

    // The windows of the running watchers, by handle, so that the window procedure, which has no instance, finds its watcher.
    private static readonly ConcurrentDictionary<nint, ClipboardWatcher> Watchers = new();

    private readonly object _gate = new();
    private readonly ClipboardHistoryLimits _limits;
    private readonly IClipboardAccess _clipboard;
    private Thread? _thread;
    private nint _window;
    private bool _disposed;

    /// <summary>Creates the watcher, not yet watching.</summary>
    /// <param name="limits">What the history keeps; text longer than its limit is not passed on.</param>
    public ClipboardWatcher(ClipboardHistoryLimits? limits = null)
        : this(limits ?? ClipboardHistoryLimits.Default, new WindowsClipboardAccess())
    {
    }

    internal ClipboardWatcher(ClipboardHistoryLimits limits, IClipboardAccess clipboard)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    /// <inheritdoc/>
    public event EventHandler<ClipboardCopiedEventArgs>? TextCopied;

    /// <inheritdoc/>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _thread is not null;
            }
        }
    }

    /// <inheritdoc/>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                return;
            }

            using var ready = new ManualResetEventSlim();
            var failed = false;
            var thread = new Thread(() => Run(ready, () => failed = true))
            {
                IsBackground = true,
                Name = "Assistant clipboard watcher",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            if (failed)
            {
                thread.Join(TimeSpan.FromSeconds(1));
                throw new InvalidOperationException("The clipboard watcher could not start.");
            }

            _thread = thread;
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
        }

        var window = Interlocked.Exchange(ref _window, 0);

        if (thread is null)
        {
            return;
        }

        if (window != 0)
        {
            User32.PostMessage(window, User32.WM_CLOSE, 0, 0);
        }

        thread.Join(TimeSpan.FromSeconds(3));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    // The thread: makes the window, asks to be told of changes, and handles messages until the window is closed.
    private unsafe void Run(ManualResetEventSlim ready, Action fail)
    {
        nint window = 0;
        try
        {
            var instance = Kernel32.GetModuleHandle(null);
            fixed (char* name = WindowClassName)
            {
                var windowClass = new User32.WindowClass
                {
                    Size = (uint)sizeof(User32.WindowClass),
                    WindowProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc,
                    Instance = instance,
                    ClassName = (nint)name,
                };
                if (User32.RegisterClass(in windowClass) == 0 && Marshal.GetLastPInvokeError() != User32.ERROR_CLASS_ALREADY_EXISTS)
                {
                    fail();
                    return;
                }
            }

            window = User32.CreateWindow(0, WindowClassName, null, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, instance, 0);
            if (window == 0)
            {
                fail();
                return;
            }

            Watchers[window] = this;
            if (!User32.AddClipboardFormatListener(window))
            {
                fail();
                return;
            }

            // Not under the lock: Start holds it while it waits for this.
            Interlocked.Exchange(ref _window, window);
            ready.Set();
            while (User32.GetMessage(out var message, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(in message);
                User32.DispatchMessage(in message);
            }
        }
        finally
        {
            if (window != 0)
            {
                User32.RemoveClipboardFormatListener(window);
                Watchers.TryRemove(window, out _);
                User32.DestroyWindow(window);
            }

            // Whatever happened, whoever waits for the window is let go.
            ready.Set();
        }
    }

    // Windows calls this on the watcher's thread for the window's messages.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (Watchers.TryGetValue(window, out var watcher))
        {
            switch (message)
            {
                case User32.WM_CLIPBOARDUPDATE:
                    // A copy often arrives as several changes, and its owner may still be filling the clipboard: wait for it to settle.
                    User32.SetTimer(window, SettleTimer, SettleMilliseconds, 0);
                    return 0;
                case User32.WM_TIMER when wParam == SettleTimer:
                    User32.KillTimer(window, SettleTimer);
                    watcher.Settled();
                    return 0;
                case User32.WM_CLOSE:
                    User32.PostQuitMessage(0);
                    return 0;
            }
        }

        return User32.DefWindowProc(window, message, wParam, lParam);
    }

    // The clipboard has stopped changing: what is on it is passed on if it is fit to keep.
    private void Settled()
    {
        if (ClipboardSuppression.IsSuppressed)
        {
            return;
        }

        string? text;
        try
        {
            text = ClipboardTextFilter.TryRead(_clipboard, _limits);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A clipboard that cannot be read is nothing to keep; the watcher carries on for the next copy.
            return;
        }

        if (text is null)
        {
            return;
        }

        try
        {
            TextCopied?.Invoke(this, new ClipboardCopiedEventArgs(text));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // This runs inside a window procedure, which an exception must never leave.
        }
    }
}
