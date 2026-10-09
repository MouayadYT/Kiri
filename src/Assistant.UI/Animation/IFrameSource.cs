using System.Windows.Media;

namespace Assistant.UI.Animation;

/// <summary>Raises <see cref="Frame"/> once for each rendered frame, with the frame's time, while started.</summary>
internal interface IFrameSource
{
    event EventHandler<TimeSpan>? Frame;

    void Start();

    void Stop();
}

/// <summary>
/// Frames from WPF's rendering loop on the calling thread. It only listens while started, because listening keeps WPF
/// rendering every frame.
/// </summary>
internal sealed class RenderingFrameSource : IFrameSource
{
    private bool _started;

    public event EventHandler<TimeSpan>? Frame;

    public void Start()
    {
        if (!_started)
        {
            _started = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    public void Stop()
    {
        if (_started)
        {
            _started = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e) =>
        Frame?.Invoke(this, ((RenderingEventArgs)e).RenderingTime);
}
