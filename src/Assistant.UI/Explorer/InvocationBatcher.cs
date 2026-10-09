namespace Assistant.UI.Explorer;

/// <summary>
/// Gathers the files File Explorer sends one at a time into one batch. Explorer starts its entry point once for each selected
/// file, so choosing Ask Assistant on five files sends five requests within moments of each other: they are held until none
/// has come for <see cref="Quiet"/>, or until <see cref="MaxWait"/> after the first, and then handed on together, in the order
/// they came.
/// </summary>
internal sealed class InvocationBatcher : IDisposable
{
    /// <summary>How long no request may come before a batch is handed on, by default.</summary>
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long the first request of a batch waits at most, by default: File Explorer starts about ten processes a second, and
    /// a selection of a hundred files should still be one batch.
    /// </summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(12);

    private readonly TimeProvider _clock;
    private readonly Action<IReadOnlyList<string>> _flush;
    private readonly object _gate = new();
    private readonly List<string> _paths = [];
    private ITimer? _timer;
    private long _firstAt;
    private bool _disposed;

    /// <summary>Creates a batcher that hands each batch to <paramref name="flush"/>, on a thread-pool thread.</summary>
    public InvocationBatcher(
        Action<IReadOnlyList<string>> flush, TimeProvider? clock = null, TimeSpan? quiet = null, TimeSpan? maxWait = null)
    {
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _clock = clock ?? TimeProvider.System;
        Quiet = quiet ?? DefaultQuiet;
        MaxWait = maxWait ?? DefaultMaxWait;
    }

    /// <summary>How long no request may come before the batch is handed on.</summary>
    public TimeSpan Quiet { get; }

    /// <summary>How long the first request of a batch waits at most.</summary>
    public TimeSpan MaxWait { get; }

    /// <summary>Adds the files of one request to the batch being gathered, or starts one.</summary>
    public void Add(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_paths.Count == 0)
            {
                _firstAt = _clock.GetTimestamp();
            }

            _paths.AddRange(paths);
            var left = MaxWait - _clock.GetElapsedTime(_firstAt);
            var due = left < Quiet ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : Quiet;
            if (_timer is null)
            {
                _timer = _clock.CreateTimer(_ => Flush(), null, due, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timer.Change(due, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _paths.Clear();
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Flush()
    {
        string[] batch;
        lock (_gate)
        {
            if (_disposed || _paths.Count == 0)
            {
                return;
            }

            batch = [.. _paths];
            _paths.Clear();
            _timer?.Dispose();
            _timer = null;
        }

        _flush(batch);
    }
}
