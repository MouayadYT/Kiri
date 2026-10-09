using Assistant.Windows.Backdrop;
using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Frame;

/// <summary>
/// Gives windows a dark DWM frame with a system backdrop. Frames report changes to the transparency setting on the
/// thread that applied the first one, through its synchronization context.
/// </summary>
public sealed class WindowFrameFactory : IWindowFrameFactory, IDisposable
{
    private readonly ILogger<WindowFrameFactory> _logger;
    private TransparencySettings? _settings;

    public WindowFrameFactory(ILogger<WindowFrameFactory> logger) => _logger = logger;

    /// <inheritdoc/>
    public IWindowFrame Apply(nint window, WindowFrameStyle style)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);
        ArgumentNullException.ThrowIfNull(style);
        if (_settings is null)
        {
            var context = SynchronizationContext.Current;
            _settings = new TransparencySettings(action =>
            {
                if (context is null) action();
                else context.Post(_ => action(), null);
            });
        }

        return new WindowFrame(window, style, _settings, _logger);
    }

    /// <summary>Stops following the transparency setting. Dispose the frames first.</summary>
    public void Dispose() => _settings?.Dispose();
}
