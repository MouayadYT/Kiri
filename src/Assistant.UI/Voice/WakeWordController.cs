using System.Windows.Threading;
using Assistant.UI.Windowing;

namespace Assistant.UI.Voice;

/// <summary>
/// Opens the Assistant and starts listening when the wake word is heard (PROJECT_SPEC §4.2, step 125). The listener runs on its own thread; what it heard is
/// carried to the UI thread, where the window controller does the rest: the Assistant stops talking, opens where Alt+A would open it or listens where it is
/// if it is already up, and the request that follows is recognized.
/// </summary>
internal sealed class WakeWordController : IDisposable
{
    private readonly MicrophoneRouter _router;
    private readonly AssistantWindowStateController _window;
    private Dispatcher? _dispatcher;

    public WakeWordController(MicrophoneRouter router, AssistantWindowStateController window)
    {
        _router = router;
        _window = window;
    }

    /// <summary>Starts following the wake word, doing what it asks on <paramref name="dispatcher"/>'s thread.</summary>
    public void Start(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_dispatcher is not null)
        {
            return;
        }

        _dispatcher = dispatcher;
        _router.WakeWordHeard += OnWakeWordHeard;
    }

    /// <inheritdoc/>
    public void Dispose() => _router.WakeWordHeard -= OnWakeWordHeard;

    private void OnWakeWordHeard(object? sender, WakeHandoff handoff) =>
        _dispatcher?.BeginInvoke(DispatcherPriority.Send, () => _window.BeginVoiceFromWakeWord(handoff));
}
