using Assistant.Windows.Interop;

namespace Assistant.Windows.Foreground;

/// <summary>
/// Gives the keyboard to one of the app's own windows when the user has just asked for it by a shortcut. Windows lets only the
/// foreground application, or the one that received the last input, take the foreground; a shortcut that first has to read another
/// application's selection can find that right gone by the time it has an answer (the key was already released to the application in
/// front). This asks the way the foreground application's own thread would: it joins that thread's input for the instant of the
/// request, then leaves it. Nothing is sent to the other application, and its focus and selection are left as they are.
/// </summary>
public static class ForegroundWindow
{
    /// <summary>The window in front now, or 0 when there is none (a locked PC, a secure desktop).</summary>
    public static nint Current() => User32.GetForegroundWindow();

    /// <summary>Whether <paramref name="window"/> is the foreground window.</summary>
    public static bool IsForeground(nint window) => window != 0 && User32.GetForegroundWindow() == window;

    /// <summary>
    /// Makes <paramref name="window"/> the foreground window, joining the input of the thread of the window in front for the request when
    /// an ordinary one is refused. Call it on the thread that owns the window.
    /// </summary>
    /// <returns>Whether <paramref name="window"/> is the foreground window afterwards.</returns>
    public static bool TryActivate(nint window)
    {
        // A window that is gone is not worth joining another thread's input for.
        if (window == 0 || !User32.IsWindow(window))
        {
            return false;
        }

        if (IsForeground(window))
        {
            return true;
        }

        if (User32.SetForegroundWindow(window) && IsForeground(window))
        {
            return true;
        }

        // Refused: ask as the thread in front would. A window of an elevated application cannot be joined, which only means this fails.
        var front = User32.GetForegroundWindow();
        var frontThread = front == 0 ? 0 : User32.GetWindowThreadProcessId(front, out _);
        var thisThread = Kernel32.GetCurrentThreadId();
        var attached = frontThread != 0 && frontThread != thisThread && User32.AttachThreadInput(thisThread, frontThread, true);
        try
        {
            User32.BringWindowToTop(window);
            User32.SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                User32.AttachThreadInput(thisThread, frontThread, false);
            }
        }

        return IsForeground(window);
    }
}
