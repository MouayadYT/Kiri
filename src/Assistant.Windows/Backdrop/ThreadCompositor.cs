using Assistant.Windows.Interop;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Assistant.Windows.Backdrop;

/// <summary>
/// The Windows.UI.Composition compositor of the calling thread, with the <see cref="DispatcherQueue"/> it requires.
/// Create it on a thread that runs a message loop, such as a WPF UI thread.
/// </summary>
internal sealed class ThreadCompositor : IDisposable
{
    // Null when the thread already had a queue. Otherwise it owns the queue, which lives until the thread ends.
    private readonly DispatcherQueueController? _controller;

    public ThreadCompositor()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            _controller = CoreMessaging.CreateForCurrentThread();
            queue = _controller.DispatcherQueue;
        }

        Queue = queue;
        Compositor = new Compositor();
    }

    public DispatcherQueue Queue { get; }

    public Compositor Compositor { get; }

    /// <summary>Creates a composition target that shows visuals in <paramref name="window"/>.</summary>
    public DesktopWindowTarget CreateTarget(nint window) =>
        CompositorDesktopInterop.CreateDesktopWindowTarget(Compositor, window, isTopmost: false);

    public void Dispose() => Compositor.Dispose();
}
