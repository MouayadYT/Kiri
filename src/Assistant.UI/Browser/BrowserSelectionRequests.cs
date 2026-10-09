using Assistant.Core.Ipc;
using Assistant.Windows.Placement;

namespace Assistant.UI.Browser;

/// <summary>
/// Where the text a browser sends is handed to the window (like <see cref="Explorer.ExplorerFileRequests"/> for File Explorer's files): the
/// pipe posts it from a background thread, and the window's controller, once connected, takes it on the UI thread. A selection that comes
/// before the window exists, as when the browser's click started the app, waits for it; only the newest waits, since a panel can only
/// show one. Each comes with the browser's window as it was when the selection arrived, which is what the panel opens beside.
/// </summary>
internal sealed class BrowserSelectionRequests
{
    private readonly object _gate = new();
    private Action<BrowserSelection, NearWindowTarget?>? _handler;
    private Action<Action>? _post;
    private (BrowserSelection Selection, NearWindowTarget? Browser)? _pending;

    /// <summary>
    /// Connects <paramref name="handler"/>, which <paramref name="post"/> runs on the UI thread, and hands it the selection that was
    /// waiting, if any.
    /// </summary>
    public void Connect(Action<BrowserSelection, NearWindowTarget?> handler, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(post);
        (BrowserSelection Selection, NearWindowTarget? Browser)? pending;
        lock (_gate)
        {
            _handler = handler;
            _post = post;
            pending = _pending;
            _pending = null;
        }

        if (pending is { } waiting)
        {
            post(() => handler(waiting.Selection, waiting.Browser));
        }
    }

    /// <summary>Hands <paramref name="selection"/> to the window, or keeps it until the window connects.</summary>
    /// <param name="selection">What the user selected in the browser.</param>
    /// <param name="browser">The browser's window, or <see langword="null"/> when none was found in front.</param>
    public void Post(BrowserSelection selection, NearWindowTarget? browser = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Action<BrowserSelection, NearWindowTarget?> handler;
        Action<Action> post;
        lock (_gate)
        {
            if (_handler is null || _post is null)
            {
                _pending = (selection, browser);
                return;
            }

            handler = _handler;
            post = _post;
        }

        post(() => handler(selection, browser));
    }
}
