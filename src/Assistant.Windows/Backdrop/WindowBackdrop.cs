using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Assistant.Windows.Interop;
using Microsoft.Extensions.Logging;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Assistant.Windows.Backdrop;

/// <summary>
/// Blurs what lies behind part of a target window. A separate backdrop window, kept directly beneath the target,
/// shows the DWM host backdrop (the blurred desktop that Windows acrylic is made from) through a composition visual
/// clipped to the region. The target itself cannot host the visual: a window's composition visuals always draw above
/// its own content.
/// </summary>
internal sealed unsafe class WindowBackdrop : IWindowBackdrop
{
    // Each backdrop subclasses its target under an id of its own, so a window with several pieces of glass, each with a
    // backdrop, can have them all.
    private static uint s_nextSubclassId;

    private readonly nint _target;
    private readonly nuint _subclassId = Interlocked.Increment(ref s_nextSubclassId);
    private readonly TransparencySettings _settings;
    private readonly ILogger _logger;
    private readonly DesktopWindowTarget _compositionTarget;
    private readonly SpriteVisual _visual;
    private readonly CompositionRoundedRectangleGeometry _clip;
    private GCHandle _self;
    private BackdropRegion _region;
    private float _opacity = 1;
    private bool _targetVisible;
    private bool _following;
    private bool _disposed;

    public WindowBackdrop(
        nint target, BackdropWindowClass windowClass, ThreadCompositor compositor, TransparencySettings settings,
        ILogger logger)
    {
        _target = target;
        _settings = settings;
        _logger = logger;
        Handle = windowClass.CreateWindow(User32.IsTopmost(target));
        try
        {
            Marshal.ThrowExceptionForHR(DwmApi.SetWindowAttribute(Handle, DwmApi.DWMWA_USE_HOSTBACKDROPBRUSH, 1));

            // Square corners, no border and no show animation, like the target. Failures here only affect looks.
            DwmApi.SetWindowAttribute(Handle, DwmApi.DWMWA_WINDOW_CORNER_PREFERENCE, DwmApi.DWMWCP_DONOTROUND);
            DwmApi.SetWindowAttribute(Handle, DwmApi.DWMWA_BORDER_COLOR, DwmApi.DWMWA_COLOR_NONE);
            DwmApi.SetWindowAttribute(Handle, DwmApi.DWMWA_TRANSITIONS_FORCEDISABLED, 1);

            _clip = compositor.Compositor.CreateRoundedRectangleGeometry();
            _visual = compositor.Compositor.CreateSpriteVisual();
            _visual.Brush = compositor.Compositor.CreateHostBackdropBrush();
            _visual.Clip = compositor.Compositor.CreateGeometricClip(_clip);
            _visual.Opacity = 0;
            _compositionTarget = compositor.CreateTarget(Handle);
            _compositionTarget.Root = _visual;

            _self = GCHandle.Alloc(this);
            if (!ComCtl32.SetWindowSubclass(target, &TargetSubclassProc, _subclassId, (nuint)GCHandle.ToIntPtr(_self)))
            {
                throw new Win32Exception("The target window could not be subclassed.");
            }
        }
        catch
        {
            Release();
            throw;
        }

        _targetVisible = User32.IsWindowVisible(target);
        IsBlurred = settings.AllowsBlur;
        settings.Changed += OnSettingsChanged;
    }

    public nint Handle { get; private set; }

    public bool IsBlurred { get; private set; }

    public event EventHandler? IsBlurredChanged;

    public void SetRegion(BackdropRegion region)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (region == _region)
        {
            return;
        }

        _region = region;
        if (!region.IsEmpty)
        {
            // The backdrop window sits on whole pixels and the visual carries the fraction, so the blur's
            // anti-aliased edge lines up with the target's.
            var size = new Vector2((float)region.Width, (float)region.Height);
            _visual.Offset = new Vector3((float)(region.X - Math.Floor(region.X)), (float)(region.Y - Math.Floor(region.Y)), 0);
            _visual.Size = size;
            _clip.Size = size;
            _clip.CornerRadius = new Vector2((float)region.CornerRadiusX, (float)region.CornerRadiusY);
            _visual.Opacity = _opacity;
        }
        else
        {
            // The backdrop window is shown at once, and what its visual is told then is drawn a frame or two later: until it is, Windows draws
            // what the visual was told last. So with nothing to blur the visual is left unseen, or the next glass to be blurred would start
            // with a flash of the last one's blur, at its size and its full strength.
            _visual.Opacity = 0;
        }

        Follow();
    }

    public void SetOpacity(double opacity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _opacity = (float)Math.Clamp(opacity, 0, 1);
        if (!_region.IsEmpty)
        {
            _visual.Opacity = _opacity;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        Release();
    }

    // Exceptions must not unwind into native code, so they are logged here.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint TargetSubclassProc(
        nint window, uint message, nint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        if (GCHandle.FromIntPtr((nint)referenceData).Target is WindowBackdrop backdrop)
        {
            try
            {
                backdrop.OnTargetMessage(message, lParam);
            }
            catch (Exception exception)
            {
                BackdropLog.FollowFailed(backdrop._logger, exception);
            }
        }

        return ComCtl32.DefSubclassProc(window, message, wParam, lParam);
    }

    private void OnTargetMessage(uint message, nint lParam)
    {
        switch (message)
        {
            case User32.WM_WINDOWPOSCHANGING:
                var pending = (User32.WindowPos*)lParam;
                if ((pending->Flags & User32.SWP_SHOWWINDOW) != 0)
                {
                    // Show the blur before the target appears, so the target is never seen over an unblurred desktop.
                    // Stacking waits for WM_WINDOWPOSCHANGED: changing z-order here would reorder the target mid-move.
                    _targetVisible = true;
                    Follow(PendingOrigin(pending), restack: false);
                }

                break;

            case User32.WM_WINDOWPOSCHANGED:
                var flags = ((User32.WindowPos*)lParam)->Flags;
                if ((flags & User32.SWP_SHOWWINDOW) != 0)
                {
                    _targetVisible = true;
                }
                else if ((flags & User32.SWP_HIDEWINDOW) != 0)
                {
                    _targetVisible = false;
                }

                Follow();
                break;

            case User32.WM_SETTINGCHANGE:
            case User32.WM_THEMECHANGED:
                Refresh();
                break;

            case User32.WM_NCDESTROY:
                Dispose();
                break;
        }
    }

    // Shows, hides, moves and stacks the backdrop window to match the target.
    private void Follow((int X, int Y)? targetOrigin = null, bool restack = true)
    {
        if (_following || _disposed)
        {
            return;
        }

        _following = true;
        try
        {
            if (!_targetVisible || !IsBlurred || _region.IsEmpty)
            {
                User32.ShowWindow(Handle, User32.SW_HIDE);
                return;
            }

            var origin = targetOrigin ?? CurrentOrigin();
            var left = Math.Floor(_region.X);
            var top = Math.Floor(_region.Y);
            var width = (int)Math.Ceiling(_region.X + _region.Width - left);
            var height = (int)Math.Ceiling(_region.Y + _region.Height - top);
            var flags = User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW;
            if (restack)
            {
                var topmost = User32.IsTopmost(_target);
                if (User32.IsTopmost(Handle) != topmost)
                {
                    User32.SetWindowPos(
                        Handle, topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST, 0, 0, 0, 0,
                        User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
                }
            }
            else
            {
                flags |= User32.SWP_NOZORDER;
            }

            // Inserting after the target places the backdrop directly beneath it.
            User32.SetWindowPos(Handle, _target, origin.X + (int)left, origin.Y + (int)top, width, height, flags);
        }
        finally
        {
            _following = false;
        }
    }

    private (int X, int Y) CurrentOrigin()
    {
        User32.GetWindowRect(_target, out var bounds);
        return (bounds.Left, bounds.Top);
    }

    // Where the target's top-left corner will be once a pending WM_WINDOWPOSCHANGING takes effect.
    private (int X, int Y) PendingOrigin(User32.WindowPos* pending) =>
        (pending->Flags & User32.SWP_NOMOVE) != 0 ? CurrentOrigin() : (pending->X, pending->Y);

    private void OnSettingsChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var blurred = _settings.AllowsBlur;
        if (blurred == IsBlurred)
        {
            return;
        }

        IsBlurred = blurred;
        BackdropLog.BlurChanged(_logger, blurred);
        Follow();
        IsBlurredChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Release()
    {
        if (_self.IsAllocated)
        {
            ComCtl32.RemoveWindowSubclass(_target, &TargetSubclassProc, _subclassId);
            _self.Free();
        }

        _compositionTarget?.Dispose();
        if (Handle != 0)
        {
            User32.DestroyWindow(Handle);
            Handle = 0;
        }
    }
}
