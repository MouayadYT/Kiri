using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Tray;

/// <summary>
/// The Assistant's icon in the notification area, made with <c>Shell_NotifyIcon</c> (PROJECT_SPEC §4.9, step 121). Windows tells a hidden
/// window of the icon's about the clicks: the left button, or Enter on the icon, is <see cref="Selected"/>; the right button, or the menu
/// key, opens a native menu of <see cref="Menu"/> at the icon, which follows the system's light or dark choice, and the line chosen is
/// <see cref="CommandInvoked"/>. When File Explorer starts again, or starts after the app does (at sign-in), the icon is put back.
/// </summary>
/// <remarks>
/// The window belongs to the thread that first shows the icon, which must pump messages, and the events come on it. The menu is modal on that
/// thread, as every native menu is, while the rest of the app's messages carry on. Nothing about the user's clicks is logged here.
/// </remarks>
public sealed unsafe class NotificationAreaIcon : INotificationAreaIcon
{
    private const string WindowClassName = "Assistant.NotificationAreaIcon";
    private const uint IconId = 1;
    private const uint CallbackMessage = User32.WM_APP + 1;
    private const int MaxTooltipLength = 127;

    private static readonly ConcurrentDictionary<nint, NotificationAreaIcon> Icons = new();
    private static readonly uint TaskbarCreated = User32.RegisterWindowMessage("TaskbarCreated");

    private readonly string? _iconPath;
    private IReadOnlyList<TrayMenuItem> _menu = [];
    private string _tooltip;
    private nint _window;
    private nint _icon;
    private bool _ownsIcon;
    private bool _wanted;
    private bool _shown;
    private bool _disposed;

    /// <summary>Creates an icon that is not shown yet.</summary>
    /// <param name="tooltip">The words that appear when the pointer rests on the icon.</param>
    /// <param name="iconPath">An <c>.ico</c> file with the sizes of the icon, or <see langword="null"/> or a missing file for the system's plain application icon.</param>
    public NotificationAreaIcon(string tooltip, string? iconPath = null)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        _tooltip = Clip(tooltip);
        _iconPath = iconPath;
    }

    /// <inheritdoc/>
    public event EventHandler? Selected;

    /// <inheritdoc/>
    public event EventHandler<int>? CommandInvoked;

    /// <inheritdoc/>
    public bool IsShown => _shown;

    /// <inheritdoc/>
    public string Tooltip
    {
        get => _tooltip;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _tooltip = Clip(value);
            if (_shown)
            {
                var data = Describe(Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
                Shell32.NotifyIcon(Shell32.NIM_MODIFY, in data);
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<TrayMenuItem> Menu
    {
        get => _menu;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _menu = [.. value];
        }
    }

    /// <summary>The hidden window that Windows tells about the icon, or 0 before the first <see cref="Show"/>. For tests.</summary>
    internal nint Window => _window;

    /// <summary>Shows the menu and returns the id of the line chosen, or 0 for none: the native menu, unless a test says otherwise.</summary>
    internal Func<nint, IReadOnlyList<TrayMenuItem>, int, int, int> MenuPresenter { get; set; } = PopupMenu.Show;

    /// <inheritdoc/>
    public bool Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureWindow();

        // Wanted from now on: if Windows cannot take the icon yet (the taskbar does not exist at sign-in until File Explorer has started),
        // it is added when the taskbar announces itself.
        _wanted = true;
        return _shown || Add();
    }

    /// <inheritdoc/>
    public void Hide()
    {
        _wanted = false;
        if (_shown)
        {
            var data = Describe(0);
            Shell32.NotifyIcon(Shell32.NIM_DELETE, in data);
            _shown = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
        if (_window != 0)
        {
            Icons.TryRemove(_window, out _);
            User32.DestroyWindow(_window);
            _window = 0;
        }

        if (_ownsIcon && _icon != 0)
        {
            User32.DestroyIcon(_icon);
        }

        _icon = 0;
    }

    private bool Add()
    {
        _icon = _icon != 0 ? _icon : LoadIcon();
        var data = Describe(Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        if (!Shell32.NotifyIcon(Shell32.NIM_ADD, in data))
        {
            return false;
        }

        // The behavior of Windows Vista and later: the click arrives with the point it was made at, and the tooltip is the icon's own.
        var version = Describe(0);
        version.TimeoutOrVersion = Shell32.NOTIFYICON_VERSION_4;
        Shell32.NotifyIcon(Shell32.NIM_SETVERSION, in version);
        _shown = true;
        return true;
    }

    /// <summary>Asks Windows whether the icon is in the notification area now, whatever this object believes. For tests.</summary>
    internal bool IsInNotificationArea()
    {
        var data = Describe(Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        return _window != 0 && Shell32.NotifyIcon(Shell32.NIM_MODIFY, in data);
    }

    /// <summary>Takes the icon away behind this object's back, as File Explorer closing does. For tests.</summary>
    internal void LoseIconForTest()
    {
        var data = Describe(0);
        Shell32.NotifyIcon(Shell32.NIM_DELETE, in data);
    }

    private Shell32.NotifyIconData Describe(uint flags)
    {
        var data = new Shell32.NotifyIconData
        {
            Size = (uint)sizeof(Shell32.NotifyIconData),
            Window = _window,
            Id = IconId,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        for (var i = 0; i < _tooltip.Length; i++)
        {
            data.Tip[i] = _tooltip[i];
        }

        return data;
    }

    // The icon of the size the notification area draws at this PC's scaling, from the file; else the system's plain one.
    private nint LoadIcon()
    {
        if (_iconPath is not null && File.Exists(_iconPath))
        {
            var dpi = User32.GetDpiForSystem();
            var width = User32.GetSystemMetricsForDpi(User32.SM_CXSMICON, dpi);
            var height = User32.GetSystemMetricsForDpi(User32.SM_CYSMICON, dpi);
            var icon = User32.LoadImage(0, _iconPath, User32.IMAGE_ICON, width, height, User32.LR_LOADFROMFILE);
            if (icon != 0)
            {
                _ownsIcon = true;
                return icon;
            }
        }

        _ownsIcon = false;
        return User32.LoadIcon(0, User32.IDI_APPLICATION);
    }

    private void EnsureWindow()
    {
        if (_window != 0)
        {
            return;
        }

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
                throw new InvalidOperationException("The notification-area icon's window class could not be registered.");
            }
        }

        // An ordinary hidden window, not a message-only one: only those hear that the taskbar was made again.
        var window = User32.CreateWindow(User32.WS_EX_TOOLWINDOW, WindowClassName, "Assistant", User32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        if (window == 0)
        {
            throw new InvalidOperationException("The notification-area icon's window could not be made.");
        }

        _window = window;
        Icons[window] = this;
    }

    // Windows calls this on the thread that made the window.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (Icons.TryGetValue(window, out var icon))
        {
            try
            {
                if (message == CallbackMessage)
                {
                    icon.OnNotification(unchecked((uint)lParam) & 0xFFFF, wParam);
                    return 0;
                }

                if (message == TaskbarCreated)
                {
                    icon.OnTaskbarCreated();
                    return 0;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A handler that fails must not unwind into Windows, which ends the process; the icon carries on.
            }
        }

        return User32.DefWindowProc(window, message, wParam, lParam);
    }

    // The event is the low word of the value, and with version 4 the point the click was made at is the two words of wParam (signed: a monitor
    // left of or above the main one has negative coordinates).
    private void OnNotification(uint notification, nuint point)
    {
        switch (notification)
        {
            case Shell32.NIN_SELECT:
            case Shell32.NIN_KEYSELECT:
                Selected?.Invoke(this, EventArgs.Empty);
                break;
            case User32.WM_CONTEXTMENU:
                OpenMenu(unchecked((short)(point & 0xFFFF)), unchecked((short)((point >> 16) & 0xFFFF)));
                break;
        }
    }

    private void OpenMenu(int x, int y)
    {
        if (_menu.Count == 0)
        {
            return;
        }

        var command = MenuPresenter(_window, _menu, x, y);
        if (command != 0)
        {
            CommandInvoked?.Invoke(this, command);
        }
    }

    // File Explorer made the taskbar again, after it was closed or crashed or, at sign-in, before it had started: its icons are gone.
    private void OnTaskbarCreated()
    {
        _shown = false;
        if (_wanted)
        {
            Add();
        }
    }

    private static string Clip(string tooltip) => tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
}
