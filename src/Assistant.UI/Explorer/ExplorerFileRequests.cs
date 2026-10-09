namespace Assistant.UI.Explorer;

/// <summary>
/// Where the files File Explorer sends are handed to the window (like <see cref="Search.AttachRequests"/> for the bar): the pipe
/// posts them from a background thread, and the window's controller, once connected, takes them on the UI thread. A batch that
/// comes before the window exists, as when Ask Assistant started the app, waits for it.
/// </summary>
internal sealed class ExplorerFileRequests
{
    private readonly object _gate = new();
    private Action<ExplorerFiles>? _handler;
    private Action<Action>? _post;
    private ExplorerFiles? _pending;

    /// <summary>
    /// Connects <paramref name="handler"/>, which <paramref name="post"/> runs on the UI thread, and hands it the batch that was
    /// waiting, if any.
    /// </summary>
    public void Connect(Action<ExplorerFiles> handler, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(post);
        ExplorerFiles? pending;
        lock (_gate)
        {
            _handler = handler;
            _post = post;
            pending = _pending;
            _pending = null;
        }

        if (pending is not null)
        {
            post(() => handler(pending));
        }
    }

    /// <summary>Hands <paramref name="files"/> to the window, or keeps them until it connects (batches that wait are joined into one).</summary>
    public void Post(ExplorerFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Action<ExplorerFiles> handler;
        Action<Action> post;
        lock (_gate)
        {
            if (_handler is null || _post is null)
            {
                _pending = _pending is null ? files : ExplorerFiles.Combine(_pending, files);
                return;
            }

            handler = _handler;
            post = _post;
        }

        post(() => handler(files));
    }
}
