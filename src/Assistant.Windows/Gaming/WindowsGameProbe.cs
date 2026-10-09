using Assistant.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Assistant.Windows.Gaming;

/// <summary>What the game detector asks of Windows. It is an interface so the detector's rules can be tried without a game.</summary>
internal interface IGameProbe
{
    /// <summary>Has <paramref name="changed"/> called, on a thread of the probe's, with each window that comes to the front, until the result is disposed.</summary>
    IDisposable WatchForeground(Action<nint> changed);

    /// <summary>The window in front now, or 0.</summary>
    nint Foreground();

    /// <summary>The windows on the desktop that can be seen, for the one look at what was already running when watching began.</summary>
    IReadOnlyList<nint> VisibleWindows();

    /// <summary>
    /// What can be seen of <paramref name="window"/> and of the program behind it, or <see langword="null"/> for a window that is gone or is the
    /// Assistant's own. <paramref name="processId"/> is the program's process.
    /// </summary>
    GameFacts? Observe(nint window, out int processId);

    /// <summary>
    /// What the program at <paramref name="executablePath"/> calls itself (the description in its file), or <see langword="null"/> when it does
    /// not say. A creative app is named by this: its window's title is the user's project.
    /// </summary>
    string? ProgramName(string executablePath);

    /// <summary>Opens the graphics card's counters for a process, or returns <see langword="null"/> when there are none.</summary>
    IGpuSampler? OpenGpu(int processId);

    /// <summary>Has <paramref name="exited"/> called once, on a background thread, when the process ends, unless the result is disposed first.</summary>
    IDisposable WatchExit(int processId, nint window, Action exited);
}

/// <summary>The real thing: Win32 for the windows and the processes, the registry for Windows' list of games, the performance counters for the graphics card.</summary>
internal sealed unsafe class WindowsGameProbe : IGameProbe
{
    // The longest path Windows has (32 767 characters) and its end, and a length that nearly every path is within.
    private const int MaxPath = 32768;
    private const int QuickPath = 520;

    // More windows than a desktop has; it only keeps a list that changes while it is walked from being walked forever.
    private const int MaxWindows = 4096;

    // How often a process that cannot be waited for (an anti-cheat took the right away) is asked after instead.
    private static readonly TimeSpan ExitPoll = TimeSpan.FromSeconds(5);

    private readonly WindowsGameList _windowsGames = new();
    private readonly int _ownProcessId = Environment.ProcessId;

    /// <inheritdoc/>
    public IDisposable WatchForeground(Action<nint> changed) => new ForegroundWatcher(changed);

    /// <inheritdoc/>
    public nint Foreground() => User32.GetForegroundWindow();

    /// <inheritdoc/>
    public IReadOnlyList<nint> VisibleWindows()
    {
        var windows = new List<nint>();
        nint window = 0;
        for (var count = 0; count < MaxWindows; count++)
        {
            window = User32.FindWindowEx(0, window, null, null);
            if (window == 0)
            {
                break;
            }

            if (User32.IsWindowVisible(window))
            {
                windows.Add(window);
            }
        }

        return windows;
    }

    /// <inheritdoc/>
    public GameFacts? Observe(nint window, out int processId)
    {
        processId = 0;
        if (window == 0 || !User32.IsWindow(window))
        {
            return null;
        }

        var windowClass = ClassOf(window);
        User32.GetWindowThreadProcessId(window, out var owner);

        // A Store app's window belongs to a host that frames every such app; the app itself is the process of the window inside it.
        if (windowClass == "ApplicationFrameWindow")
        {
            var core = User32.FindWindowEx(window, 0, "Windows.UI.Core.CoreWindow", null);
            if (core != 0 && User32.GetWindowThreadProcessId(core, out var hosted) != 0 && hosted != 0)
            {
                owner = hosted;
            }
        }

        if (owner == 0 || owner == _ownProcessId)
        {
            return null;
        }

        processId = (int)owner;
        var path = ImagePath(owner) ?? "";
        var covers = false;
        if (User32.GetWindowRect(window, out var rect) && !User32.IsIconic(window))
        {
            var monitor = User32.MonitorFromWindow(window, User32.MONITOR_DEFAULTTONEAREST);
            var info = new User32.MonitorInfo { Size = (uint)sizeof(User32.MonitorInfo) };
            if (monitor != 0 && User32.GetMonitorInfo(monitor, ref info))
            {
                // A pixel of slack: when Windows scales the numbers for this program, the two rectangles may round differently.
                covers = rect.Left <= info.Monitor.Left + 1 && rect.Top <= info.Monitor.Top + 1
                    && rect.Right >= info.Monitor.Right - 1 && rect.Bottom >= info.Monitor.Bottom - 1;
            }
        }

        var style = unchecked((uint)(long)User32.GetWindowLongPtr(window, User32.GWL_STYLE));
        var exclusive = window == User32.GetForegroundWindow()
            && Shell32.SHQueryUserNotificationState(out var state) == 0 && state == Shell32.QUNS_RUNNING_D3D_FULL_SCREEN;
        return new GameFacts
        {
            ExecutablePath = path,
            WindowClass = windowClass,
            Title = TitleOf(window),
            CoversMonitor = covers,
            HasCaption = (style & User32.WS_CAPTION) == User32.WS_CAPTION,
            ExclusiveFullscreen = exclusive,
            InWindowsGameList = _windowsGames.Contains(path),
            Files = GameFileProbe.Read(path),
        };
    }

    /// <inheritdoc/>
    public string? ProgramName(string executablePath)
    {
        try
        {
            var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(executablePath).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public IGpuSampler? OpenGpu(int processId) => GpuProcessCounter.Open(processId);

    /// <inheritdoc/>
    public IDisposable WatchExit(int processId, nint window, Action exited)
    {
        var handle = Kernel32.OpenProcess(Kernel32.SYNCHRONIZE, false, (uint)processId);
        return handle != 0 ? new ProcessExit(handle, exited) : new WindowGone(window, exited, ExitPoll);
    }

    private static string ClassOf(nint window)
    {
        var buffer = stackalloc char[256];
        var length = User32.GetClassName(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    // The title Windows keeps for the window; the program behind it is not asked for anything.
    private static string TitleOf(nint window)
    {
        var buffer = stackalloc char[256];
        var length = User32.GetWindowText(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    // The path comes from a limited-rights handle, which Windows gives for elevated processes too.
    private static string? ImagePath(uint processId)
    {
        var process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            // Nearly every path fits the short buffer; the long one is only made for a path that does not.
            var quick = stackalloc char[QuickPath];
            var size = (uint)QuickPath;
            if (Kernel32.QueryFullProcessImageName(process, 0, quick, ref size) && size > 0)
            {
                return new string(quick, 0, (int)size);
            }

            var buffer = new char[MaxPath];
            size = (uint)buffer.Length;
            fixed (char* path = buffer)
            {
                return Kernel32.QueryFullProcessImageName(process, 0, path, ref size) && size > 0 ? new string(path, 0, (int)size) : null;
            }
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    // Waits for the process to end without looking: Windows wakes a pool thread when the handle is signaled.
    private sealed class ProcessExit : IDisposable
    {
        private readonly ProcessWaitHandle _wait;
        private readonly RegisteredWaitHandle _registration;
        private int _disposed;

        public ProcessExit(nint handle, Action exited)
        {
            _wait = new ProcessWaitHandle(handle);
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _wait, (_, _) => { if (Volatile.Read(ref _disposed) == 0) exited(); }, null, Timeout.Infinite, executeOnlyOnce: true);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _registration.Unregister(null);
                _wait.Dispose();
            }
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    // For a process that cannot be waited for: its window is asked after every few seconds instead.
    private sealed class WindowGone : IDisposable
    {
        private readonly Timer _timer;
        private int _done;

        public WindowGone(nint window, Action exited, TimeSpan every)
        {
            // A handle is used again once its window is gone, so the window must also still be the same process's.
            User32.GetWindowThreadProcessId(window, out var ownerAtStart);
            _timer = new Timer(_ =>
            {
                if (User32.IsWindow(window) && User32.GetWindowThreadProcessId(window, out var owner) != 0 && owner == ownerAtStart)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _done, 1) == 0)
                {
                    exited();
                }
            }, null, every, every);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _done, 1);
            _timer.Dispose();
        }
    }
}
