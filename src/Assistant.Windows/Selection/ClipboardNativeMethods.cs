using System.Runtime.InteropServices;
using System.Windows.Automation;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Selection;

/// <summary>
/// The real thing: the Win32 clipboard, <c>SendInput</c> and the foreground window. The clipboard is used the old way, format by format, since
/// that is the one way to copy a clipboard out and put it back exactly. What cannot be copied exactly is refused up front
/// (<see cref="TrySnapshot"/>) and never gambled with.
/// </summary>
internal sealed unsafe class ClipboardNativeMethods : IClipboardNativeMethods
{
    /// <summary>The most bytes of the clipboard kept for the moment of a copy.</summary>
    internal const long MaxSnapshotBytes = 32L * 1024 * 1024;

    /// <summary>The most formats kept; a clipboard with more is not an ordinary one.</summary>
    internal const int MaxSnapshotFormats = 64;

    // The clipboard is opened by one application at a time: others are given a moment.
    private const int OpenAttempts = 20;
    private const int OpenDelayMilliseconds = 15;

    // Registered formats that tell Windows' clipboard history and cloud sync, and clipboard monitors, to leave what is restored alone: it is
    // the user's own clipboard coming back, not something new to remember or to send to their other devices.
    private const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private readonly SelectionNativeMethods _windows = new();

    public ForegroundApp? GetForegroundApp() => _windows.GetForegroundApp();

    public string WindowClassOf(nint window)
    {
        if (window == 0)
        {
            return string.Empty;
        }

        var buffer = stackalloc char[256];
        var length = User32.GetClassName(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public bool IsFocusedControlProtected()
    {
        try
        {
            return AutomationElement.FocusedElement?.Current.IsPassword ?? false;
        }
        catch (Exception)
        {
            // Not being able to tell is not a reason to refuse: an ordinary password box does not copy its text anyway.
            return false;
        }
    }

    public bool AreKeysHeld()
    {
        // 0x01-0x06 are mouse buttons, which are not the keys of a shortcut; every other key code up to 0xFE is looked at.
        for (var key = 0x08; key <= 0xFE; key++)
        {
            if ((User32.GetAsyncKeyState(key) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    public uint SequenceNumber() => User32.GetClipboardSequenceNumber();

    public SnapshotFailure TrySnapshot(out ClipboardSnapshot? snapshot)
    {
        snapshot = null;
        if (!Open(0))
        {
            return SnapshotFailure.Busy;
        }

        try
        {
            var picture = User32.IsClipboardFormatAvailable(User32.CF_DIB) || User32.IsClipboardFormatAvailable(User32.CF_DIBV5);
            var formats = new List<ClipboardFormatData>();
            long total = 0;
            for (var format = User32.EnumClipboardFormats(0); format != 0; format = User32.EnumClipboardFormats(format))
            {
                switch (Classify(format, picture))
                {
                    case FormatKind.Derived:
                        // A bitmap or palette Windows makes from the device-independent bitmap that is also there: putting that back brings them back.
                        continue;
                    case FormatKind.Unsupported:
                        return SnapshotFailure.Unsupported;
                }

                if (formats.Count >= MaxSnapshotFormats)
                {
                    return SnapshotFailure.TooLarge;
                }

                var handle = User32.GetClipboardData(format);
                var size = handle == 0 ? 0 : Kernel32.GlobalSize(handle);
                if (handle == 0 || size == 0)
                {
                    // Nothing to copy out: the format is rendered on demand by another application, or its handle is not memory.
                    return SnapshotFailure.Unsupported;
                }

                total += (long)size;
                if (total > MaxSnapshotBytes)
                {
                    return SnapshotFailure.TooLarge;
                }

                var memory = Kernel32.GlobalLock(handle);
                if (memory == 0)
                {
                    return SnapshotFailure.Unsupported;
                }

                try
                {
                    formats.Add(new ClipboardFormatData(format, new ReadOnlySpan<byte>((void*)memory, (int)size).ToArray()));
                }
                finally
                {
                    Kernel32.GlobalUnlock(handle);
                }
            }

            snapshot = new ClipboardSnapshot(formats);
            return SnapshotFailure.None;
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    public bool SendCopy()
    {
        var control = (ushort)User32.MapVirtualKey(User32.VK_CONTROL, User32.MAPVK_VK_TO_VSC);
        var c = (ushort)User32.MapVirtualKey(User32.VK_C, User32.MAPVK_VK_TO_VSC);
        var inputs = stackalloc User32.Input[4];
        inputs[0] = Key(User32.VK_CONTROL, control, 0);
        inputs[1] = Key(User32.VK_C, c, 0);
        inputs[2] = Key(User32.VK_C, c, User32.KEYEVENTF_KEYUP);
        inputs[3] = Key(User32.VK_CONTROL, control, User32.KEYEVENTF_KEYUP);
        var sent = User32.SendInput(4, inputs, sizeof(User32.Input));
        if (sent == 4)
        {
            return true;
        }

        // Windows took part of it (another thread's input got in between): never leave Ctrl pressed.
        var release = stackalloc User32.Input[2];
        release[0] = Key(User32.VK_C, c, User32.KEYEVENTF_KEYUP);
        release[1] = Key(User32.VK_CONTROL, control, User32.KEYEVENTF_KEYUP);
        User32.SendInput(2, release, sizeof(User32.Input));
        return false;
    }

    public ClipboardTextOutcome ReadText(int maxLength, out string? text)
    {
        text = null;
        if (!Open(0))
        {
            return ClipboardTextOutcome.Busy;
        }

        try
        {
            if (!User32.IsClipboardFormatAvailable(User32.CF_UNICODETEXT))
            {
                return ClipboardTextOutcome.NoText;
            }

            var handle = User32.GetClipboardData(User32.CF_UNICODETEXT);
            if (handle == 0)
            {
                return ClipboardTextOutcome.NoText;
            }

            var bytes = Kernel32.GlobalSize(handle);
            var memory = Kernel32.GlobalLock(handle);
            if (memory == 0)
            {
                return ClipboardTextOutcome.NoText;
            }

            try
            {
                // The text ends at its first NUL, or at the end of the block, and is read no further than was asked for.
                var span = new ReadOnlySpan<char>((void*)memory, (int)Math.Min(bytes / 2, (nuint)int.MaxValue));
                var end = span.IndexOf('\0');
                if (end >= 0)
                {
                    span = span[..end];
                }

                text = new string(span[..Math.Min(span.Length, maxLength)]);
                return ClipboardTextOutcome.Text;
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

    public bool Restore(ClipboardSnapshot snapshot)
    {
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
                if (!User32.EmptyClipboard())
                {
                    return false;
                }

                foreach (var item in snapshot.Formats)
                {
                    if (!Put(item.Format, item.Data))
                    {
                        return false;
                    }
                }

                // The user's own clipboard coming back is not news for clipboard history, cloud sync or clipboard monitors.
                PutFlag(ExcludeFromMonitoring, [1, 0, 0, 0]);
                PutFlag(CanIncludeInHistory, [0, 0, 0, 0]);
                PutFlag(CanUploadToCloud, [0, 0, 0, 0]);
                return true;
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

    private enum FormatKind
    {
        Memory,
        Derived,
        Unsupported,
    }

    // Which formats can be copied out and put back as bytes. Handles of other kinds (bitmaps, metafiles, owner-drawn data, application
    // private ones) cannot, and are refused; a bitmap or palette that Windows derives from a device-independent bitmap is simply left out.
    private static FormatKind Classify(uint format, bool hasDeviceIndependentBitmap) => format switch
    {
        User32.CF_BITMAP or User32.CF_PALETTE => hasDeviceIndependentBitmap ? FormatKind.Derived : FormatKind.Unsupported,
        User32.CF_METAFILEPICT or User32.CF_ENHMETAFILE or User32.CF_OWNERDISPLAY => FormatKind.Unsupported,
        >= User32.CF_DSPFIRST and <= User32.CF_DSPLAST => FormatKind.Unsupported,
        >= User32.CF_PRIVATEFIRST and <= User32.CF_GDIOBJLAST => FormatKind.Unsupported,
        _ => FormatKind.Memory,
    };

    private static User32.Input Key(ushort virtualKey, ushort scan, uint flags) => new()
    {
        Type = User32.INPUT_KEYBOARD,
        Data = new User32.InputUnion { Keyboard = new User32.KeyboardInput { VirtualKey = virtualKey, Scan = scan, Flags = flags } },
    };

    // Opens the clipboard, giving another application that has it a moment to let go.
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

    private static bool Put(uint format, byte[] data)
    {
        var block = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
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

        data.CopyTo(new Span<byte>((void*)memory, data.Length));
        Kernel32.GlobalUnlock(block);

        // Windows owns the block once it takes it; when it does not, the block is still this code's to free.
        if (User32.SetClipboardData(format, block) == 0)
        {
            Kernel32.GlobalFree(block);
            return false;
        }

        return true;
    }

    // The flags are best effort: failing to set one leaves the restored clipboard as complete as it was.
    private static void PutFlag(string name, byte[] value)
    {
        var format = User32.RegisterClipboardFormat(name);
        if (format != 0)
        {
            Put(format, value);
        }
    }
}
