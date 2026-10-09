using Assistant.Core.Settings;
using Assistant.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Hotkeys;

/// <summary>
/// Owns one configurable Win32 shortcut. Call on the owning window's thread and forward its window messages
/// to ProcessWindowMessage. The service does not create windows or depend on WPF.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    internal const int HotkeyId = 0x5341;

    /// <summary>The id of the second shortcut, the one that starts Visual Intelligence, beside the first (<c>0x5341</c>).</summary>
    public const int VisualIntelligenceHotkeyId = 0x5342;

    /// <summary>The id of the third shortcut, the one that reads the selected text and asks about it, beside the others (<c>0x5341</c>, <c>0x5342</c>).</summary>
    public const int SelectedTextHotkeyId = 0x5343;

    /// <summary>The id of the fourth shortcut, the one that reads the selected text by pressing Copy in the application in front (step 89).</summary>
    public const int SelectedTextByCopyHotkeyId = 0x5344;

    internal const uint NoRepeat = 0x4000;
    private const HotkeyModifiers KnownModifiers = HotkeyModifiers.Alt | HotkeyModifiers.Control |
        HotkeyModifiers.Shift | HotkeyModifiers.Windows;
    private readonly IHotkeyNativeMethods _native;
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly int _id;
    private nint _window;
    private uint _modifiers;
    private uint _key;
    private bool _disposed;

    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger) : this(logger, new HotkeyNativeMethods()) { }

    /// <summary>Creates a service for another shortcut than the first, which Windows tells apart by <paramref name="hotkeyId"/>.</summary>
    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger, int hotkeyId)
        : this(logger, new HotkeyNativeMethods(), hotkeyId)
    {
    }

    internal GlobalHotkeyService(ILogger<GlobalHotkeyService> logger, IHotkeyNativeMethods native, int hotkeyId = HotkeyId)
    {
        _logger = logger;
        _native = native;
        _id = hotkeyId;
    }

    public event EventHandler? Invoked;
    public bool IsRegistered { get; private set; }

    /// <summary>
    /// Registers or replaces the shortcut. Null disables it. Conflicts and invalid configuration return false
    /// and are logged without key names or input content. An unchanged registration is a no-op.
    /// </summary>
    public bool Register(nint window, Hotkey? shortcut)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint key = 0;
        if (shortcut is not null && (window == 0 || (shortcut.Modifiers & ~KnownModifiers) != 0 ||
            !HotkeyKey.TryParse(shortcut.Key, out key)))
        {
            HotkeyLog.InvalidConfiguration(_logger, _id);
            return false;
        }

        var modifiers = (uint)(shortcut?.Modifiers ?? HotkeyModifiers.None);
        if (IsRegistered && shortcut is not null && _window == window && _key == key && _modifiers == modifiers)
            return true;
        if (!Unregister()) return false;
        if (shortcut is null) return true;

        var error = _native.Register(window, _id, modifiers | NoRepeat, key);
        if (error != 0)
        {
            HotkeyLog.RegistrationFailed(_logger, _id, error);
            return false;
        }

        _window = window;
        _key = key;
        _modifiers = modifiers;
        IsRegistered = true;
        return true;
    }

    public bool Unregister()
    {
        VerifyThread();
        if (!IsRegistered) return true;
        var error = _native.Unregister(_window, _id);
        if (error != 0)
        {
            HotkeyLog.UnregistrationFailed(_logger, _id, error);
            return false;
        }

        IsRegistered = false;
        _window = 0;
        return true;
    }

    /// <summary>Returns true only for a matching WM_HOTKEY, raised on the window's UI thread.</summary>
    public bool ProcessWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        VerifyThread();
        if (_disposed || !IsRegistered || window != _window) return false;
        if (message == User32.WM_DESTROY)
        {
            Unregister();
            // Windows releases any remaining registration with the destroyed HWND.
            IsRegistered = false;
            _window = 0;
            return false;
        }

        var data = unchecked((uint)(long)lParam);
        if (message != User32.WM_HOTKEY || wParam != _id ||
            (data & 0xFFFF) != _modifiers || (data >> 16) != _key)
            return false;

        Invoked?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed) return;
        Unregister();
        _disposed = true;
        Invoked = null;
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Global hotkeys must be managed on their owning window thread.");
    }
}
