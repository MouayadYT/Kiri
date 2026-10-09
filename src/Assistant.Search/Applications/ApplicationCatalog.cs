using Assistant.Core.QuickSearch;

namespace Assistant.Search.Applications;

/// <summary>
/// The app's <see cref="IApplicationCatalog"/>: the applications read from an <see cref="IApplicationSource"/>, kept in memory. The
/// first reading is awaited by whoever needs it first (or is started early by <see cref="WarmUp"/>); after it every lookup answers
/// from memory, and a list that is older than <see cref="MaxAge"/>, or that the source said changed, is read again in the background
/// while the old one keeps answering. One reading runs at a time, a failed one keeps what was read before and is tried again no
/// sooner than <see cref="RetryAfter"/>, and what the applications are called is never logged.
/// </summary>
public sealed class ApplicationCatalog : IApplicationCatalog, IDisposable
{
    /// <summary>How old the list may get before the next lookup reads it again.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(15);

    /// <summary>How long after a failed reading the next lookup waits before it tries again.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly IApplicationSource _source;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _retryAfter;
    private readonly CancellationTokenSource _shutdown = new();
    private IReadOnlyList<InstalledApplication> _current = [];
    private Task? _reading;
    private DateTimeOffset _readAt;
    private DateTimeOffset _failedAt;
    private bool _loaded;
    private bool _stale;
    private bool _failed;
    private bool _disposed;

    /// <summary>Creates the catalog over <paramref name="source"/>.</summary>
    /// <param name="source">Where the applications are read.</param>
    /// <param name="clock">The clock that ages the list; the system's by default.</param>
    /// <param name="maxAge">How old the list may get; <see cref="DefaultMaxAge"/> by default.</param>
    /// <param name="retryAfter">The wait after a failed reading; <see cref="DefaultRetryAfter"/> by default.</param>
    public ApplicationCatalog(
        IApplicationSource source, TimeProvider? clock = null, TimeSpan? maxAge = null, TimeSpan? retryAfter = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? TimeProvider.System;
        _maxAge = maxAge ?? DefaultMaxAge;
        _retryAfter = retryAfter ?? DefaultRetryAfter;
        _source.Changed += OnSourceChanged;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IReadOnlyList<InstalledApplication> Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>The longest the list is trusted before it is read again.</summary>
    public TimeSpan MaxAge => _maxAge;

    /// <inheritdoc/>
    public void WarmUp()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                EnsureReading();
            }
        }
    }

    /// <inheritdoc/>
    public void Invalidate()
    {
        lock (_gate)
        {
            _stale = true;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? waitFor = null;
        lock (_gate)
        {
            if (!_loaded)
            {
                // Nothing to answer with yet: this lookup waits for the first reading. One that failed is answered with nothing
                // until it is time to try again, so a PC whose shell cannot be read is not asked on every keystroke.
                if (!_failed || _clock.GetUtcNow() - _failedAt >= _retryAfter)
                {
                    waitFor = EnsureReading();
                }
            }
            else if (IsStale())
            {
                EnsureReading();
            }

            if (waitFor is null)
            {
                return _current;
            }
        }

        await waitFor.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Current;
    }

    /// <summary>Stops reading and stops listening to the source.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _source.Changed -= OnSourceChanged;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    // Whether the list should be read again: said so, or grown old, or the last reading failed and it is time to try again.
    private bool IsStale() =>
        _stale || _clock.GetUtcNow() - _readAt >= _maxAge || (_failed && _clock.GetUtcNow() - _failedAt >= _retryAfter);

    // Starts a reading unless one is running; the task ends when the reading does, and never throws.
    private Task EnsureReading()
    {
        if (_reading is { IsCompleted: false } running)
        {
            return running;
        }

        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _stale = false;
        return _reading = Task.Run(ReadAsync);
    }

    private async Task ReadAsync()
    {
        IReadOnlyList<InstalledApplication>? read = null;
        try
        {
            read = await _source.GetApplicationsAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A source that cannot be read leaves what was read before, and says so by not changing it. What it said is not logged: it
            // can hold a path.
        }

        var changed = false;
        lock (_gate)
        {
            if (read is null)
            {
                _failed = true;
                _failedAt = _clock.GetUtcNow();
            }
            else
            {
                changed = !_loaded || !SameApplications(_current, read);
                _current = read;
                _loaded = true;
                _failed = false;
                _readAt = _clock.GetUtcNow();
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Invalidate();

    private static bool SameApplications(IReadOnlyList<InstalledApplication> first, IReadOnlyList<InstalledApplication> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair => pair.First.Id == pair.Second.Id && pair.First.DisplayName == pair.Second.DisplayName
            && pair.First.ExecutablePath == pair.Second.ExecutablePath);
}
