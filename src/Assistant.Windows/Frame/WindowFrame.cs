using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Frame;

/// <summary>
/// A dark DWM frame for a window with custom chrome: rounded corners, a hairline border, the frame extended over the
/// whole client area and a system backdrop behind it, and no system menu, so Windows draws no caption buttons of its
/// own. Windows keeps drawing the shadow, and resizing, snapping and maximizing stay native.
/// </summary>
internal sealed unsafe class WindowFrame : IWindowFrame
{
    private const nuint SubclassId = 2;

    private readonly nint _window;
    private readonly TransparencySettings _settings;
    private readonly ILogger _logger;
    private readonly bool _hasBackdrop;
    private GCHandle _self;
    private bool _disposed;

    public WindowFrame(nint window, WindowFrameStyle style, TransparencySettings settings, ILogger logger)
    {
        _window = window;
        _settings = settings;
        _logger = logger;

        // Only looks depend on these, so a failure leaves the window as it was.
        DwmApi.SetWindowAttribute(window, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        DwmApi.SetWindowAttribute(window, DwmApi.DWMWA_WINDOW_CORNER_PREFERENCE, DwmApi.DWMWCP_ROUND);
        if (style.BorderColor is { } border)
        {
            DwmApi.SetWindowAttribute(window, DwmApi.DWMWA_BORDER_COLOR, ToColorRef(border));
        }

        // Windows before 11 22H2 have no system backdrops: the window then draws its surfaces opaque.
        _hasBackdrop = DwmApi.ExtendFrameIntoWholeClientArea(window) >= 0 &&
            DwmApi.SetWindowAttribute(window, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, ToBackdropType(style.Backdrop)) >= 0;

        _self = GCHandle.Alloc(this);
        if (!ComCtl32.SetWindowSubclass(window, &SubclassProc, SubclassId, (nuint)GCHandle.ToIntPtr(_self)))
        {
            _self.Free();
            throw new Win32Exception("The window could not be subclassed.");
        }

        // With the frame over the whole window, Windows would draw its own caption buttons faintly over the window's
        // content. It draws none for a window without a system menu, so the window never has one; Alt+F4, the taskbar's
        // Close window, Win+arrow keys and snapping still work.
        var windowStyle = User32.GetWindowLongPtr(window, User32.GWL_STYLE);
        if ((windowStyle & (nint)User32.WS_SYSMENU) != 0)
        {
            User32.SetWindowLongPtr(window, User32.GWL_STYLE, windowStyle & ~(nint)User32.WS_SYSMENU);
        }

        IsTranslucent = Allows();
        settings.Changed += OnSettingsChanged;
        WindowFrameLog.Applied(logger, style.Backdrop, _hasBackdrop, IsTranslucent);
    }

    public bool IsTranslucent { get; private set; }

    public event EventHandler? IsTranslucentChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        if (_self.IsAllocated)
        {
            ComCtl32.RemoveWindowSubclass(_window, &SubclassProc, SubclassId);
            _self.Free();
        }
    }

    // A COLORREF is 0x00BBGGRR.
    internal static int ToColorRef(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    internal static int ToBackdropType(SystemBackdropKind kind) => kind switch
    {
        SystemBackdropKind.Mica => DwmApi.DWMSBT_MAINWINDOW,
        SystemBackdropKind.MicaAlt => DwmApi.DWMSBT_TABBEDWINDOW,
        SystemBackdropKind.Acrylic => DwmApi.DWMSBT_TRANSIENTWINDOW,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // Exceptions must not unwind into native code, so they are logged here.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        if (GCHandle.FromIntPtr((nint)referenceData).Target is WindowFrame frame)
        {
            try
            {
                switch (message)
                {
                    // Whatever changes the window's style later, such as WPF, never gives it a system menu again.
                    case User32.WM_STYLECHANGING when (int)wParam == User32.GWL_STYLE:
                        ((User32.StyleStruct*)lParam)->StyleNew &= ~User32.WS_SYSMENU;
                        break;

                    // High contrast is reported to top-level windows only.
                    case User32.WM_SETTINGCHANGE:
                    case User32.WM_THEMECHANGED:
                        frame.Refresh();
                        break;

                    case User32.WM_NCDESTROY:
                        frame.Dispose();
                        break;
                }
            }
            catch (Exception exception)
            {
                WindowFrameLog.RefreshFailed(frame._logger, exception);
            }
        }

        return ComCtl32.DefSubclassProc(window, message, wParam, lParam);
    }

    private bool Allows() => _hasBackdrop && _settings.AllowsBlur;

    private void OnSettingsChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var translucent = Allows();
        if (_disposed || translucent == IsTranslucent)
        {
            return;
        }

        IsTranslucent = translucent;
        WindowFrameLog.TranslucencyChanged(_logger, translucent);
        IsTranslucentChanged?.Invoke(this, EventArgs.Empty);
    }
}
