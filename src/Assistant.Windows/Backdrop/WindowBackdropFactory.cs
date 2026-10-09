using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Backdrop;

/// <summary>
/// Creates backdrops from the DWM host backdrop and Windows.UI.Composition. Everything is set up on the thread that
/// creates the first backdrop, which must run a message loop; later backdrops must be created on the same thread.
/// </summary>
public sealed class WindowBackdropFactory : IWindowBackdropFactory, IDisposable
{
    private readonly ILogger<WindowBackdropFactory> _logger;
    private ThreadCompositor? _compositor;
    private TransparencySettings? _settings;
    private BackdropWindowClass? _windowClass;
    private bool _unavailable;

    public WindowBackdropFactory(ILogger<WindowBackdropFactory> logger) => _logger = logger;

    /// <inheritdoc/>
    public IWindowBackdrop Create(nint window)
    {
        if (_compositor is { Queue.HasThreadAccess: false })
        {
            throw new InvalidOperationException("Backdrops must be created on the thread that created the first one.");
        }

        if (_unavailable)
        {
            return new UnavailableBackdrop();
        }

        try
        {
            _compositor ??= new ThreadCompositor();
            var queue = _compositor.Queue;
            _settings ??= new TransparencySettings(action => queue.TryEnqueue(() => action()));
            _windowClass ??= BackdropWindowClass.Register();
            var backdrop = new WindowBackdrop(window, _windowClass, _compositor, _settings, _logger);
            BackdropLog.Created(_logger, backdrop.IsBlurred);
            return backdrop;
        }
        catch (Exception exception)
        {
            // An older Windows, a DWM without host backdrops, or no composition. Glass draws opaque instead
            // (PROJECT_SPEC §4.0), for the rest of the session.
            BackdropLog.Unavailable(_logger, exception);
            _unavailable = true;
            return new UnavailableBackdrop();
        }
    }

    /// <summary>Releases the shared composition resources. Dispose the backdrops first, on the same thread.</summary>
    public void Dispose()
    {
        _settings?.Dispose();
        _windowClass?.Dispose();
        _compositor?.Dispose();
    }
}
