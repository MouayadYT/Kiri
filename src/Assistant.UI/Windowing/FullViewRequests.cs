namespace Assistant.UI.Windowing;

/// <summary>
/// Where a request to show the Assistant's full window is handed to it (like <see cref="Explorer.ExplorerFileRequests"/> for files): the app's pipe
/// posts it from a background thread when the app is opened again while it is running, and the window's controller, once connected, takes it on
/// the UI thread. One that comes before the windows exist waits for them.
/// </summary>
internal sealed class FullViewRequests
{
    private readonly object _gate = new();
    private Action? _show;
    private Action<Action>? _post;
    private bool _pending;

    /// <summary>Connects <paramref name="show"/>, which <paramref name="post"/> runs on the UI thread, and runs it for a request that was waiting.</summary>
    public void Connect(Action show, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(post);
        bool pending;
        lock (_gate)
        {
            _show = show;
            _post = post;
            pending = _pending;
            _pending = false;
        }

        if (pending)
        {
            post(show);
        }
    }

    /// <summary>Asks for the full window to be shown, now or as soon as it can be.</summary>
    public void Post()
    {
        Action show;
        Action<Action> post;
        lock (_gate)
        {
            if (_show is null || _post is null)
            {
                _pending = true;
                return;
            }

            show = _show;
            post = _post;
        }

        post(show);
    }
}
