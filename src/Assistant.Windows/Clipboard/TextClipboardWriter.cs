using Assistant.Windows.Interop;

namespace Assistant.Windows.Clipboard;

/// <summary>
/// Puts text on the clipboard the plain Win32 way: open, empty, one block of Unicode text, close. It takes well under a millisecond when nobody else
/// has the clipboard open. WPF's own <c>Clipboard.SetText</c> goes through OLE and then waits for the clipboard to be flushed, sleeping a tenth of a
/// second at a time on the thread that called it whenever a clipboard manager (Windows' own history among them) is looking at what was just put
/// there, which is what made the window stand still after Copy was pressed. What is copied is private content and is never logged.
/// </summary>
public static unsafe class TextClipboardWriter
{
    // Another application may have the clipboard open for a moment: it is asked again a few times, briefly.
    private const int OpenAttempts = 12;
    private const int OpenDelayMilliseconds = 10;

    /// <summary>Puts <paramref name="text"/> on the clipboard. Returns whether it is there; false when another application kept the clipboard open.</summary>
    public static bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // A clipboard emptied with no owner window cannot be written to, so a message-only window of this thread owns it for the moment.
        var owner = User32.CreateWindow(0, "STATIC", null, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, Kernel32.GetModuleHandle(null), 0);
        if (owner == 0)
        {
            return false;
        }

        try
        {
            if (!Open(owner))
            {
                return false;
            }

            try
            {
                return User32.EmptyClipboard() && Put(text);
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
        finally
        {
            User32.DestroyWindow(owner);
        }
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard from a thread of the pool, so that the caller (a button's click) never waits for a clipboard that
    /// another application is holding. The task says whether it got there.
    /// </summary>
    public static Task<bool> SetTextInBackground(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.Run(() => TrySetText(text));
    }

    private static bool Open(nint owner)
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (User32.OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(OpenDelayMilliseconds);
        }

        return false;
    }

    // The text as a block of UTF-16 with its ending zero, which Windows owns once it takes it.
    private static bool Put(string text)
    {
        var bytes = checked((text.Length + 1) * sizeof(char));
        var block = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)bytes);
        if (block == 0)
        {
            return false;
        }

        var memory = Kernel32.GlobalLock(block);
        if (memory == 0)
        {
            Kernel32.GlobalFree(block);
            return false;
        }

        var target = new Span<char>((void*)memory, text.Length + 1);
        text.AsSpan().CopyTo(target);
        target[text.Length] = '\0';
        Kernel32.GlobalUnlock(block);
        if (User32.SetClipboardData(User32.CF_UNICODETEXT, block) == 0)
        {
            Kernel32.GlobalFree(block);
            return false;
        }

        return true;
    }
}
