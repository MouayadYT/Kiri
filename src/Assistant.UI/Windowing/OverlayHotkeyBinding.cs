using System.Windows.Interop;
using Assistant.Core.Settings;
using Assistant.UI.Views;
using Assistant.Windows.Hotkeys;

namespace Assistant.UI.Windowing;

/// <summary>
/// Forwards the existing overlay's native messages; never creates another overlay. The hotkey shows that overlay, or
/// runs the invocation the host supplies.
/// </summary>
internal sealed class OverlayHotkeyBinding : IDisposable
{
    private readonly AssistantWindow _window;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly Hotkey? _shortcut;
    private readonly Action _invoke;
    private HwndSource? _source;
    private bool _disposed;

    public OverlayHotkeyBinding(AssistantWindow window, GlobalHotkeyService hotkeys, Hotkey? shortcut, Action? invoke = null)
    {
        _window = window;
        _hotkeys = hotkeys;
        _shortcut = shortcut;
        _invoke = invoke ?? (() => window.ShowAndFocus());
        _hotkeys.Invoked += OnInvoked;
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnClosed;
        if (new WindowInteropHelper(window).Handle != 0) Attach();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Attach();

    private void Attach()
    {
        if (_source is not null) return;
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        if (_source is null) return;
        _source.AddHook(OnMessage);
        _hotkeys.Register(handle, _shortcut);
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_hotkeys.ProcessWindowMessage(hwnd, unchecked((uint)message), wParam, lParam)) handled = true;
        return 0;
    }

    private void OnInvoked(object? sender, EventArgs e) => _invoke();
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hotkeys.Invoked -= OnInvoked;
        _hotkeys.Unregister();
        if (_source is { IsDisposed: false }) _source.RemoveHook(OnMessage);
        _source = null;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
    }
}
