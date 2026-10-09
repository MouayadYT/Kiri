using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Core.Activity;

/// <summary>
/// The in-memory <see cref="IActivityTracker"/>. It is safe to use from any thread. It keeps only each operation's kind
/// and status text, never what the operation is about.
/// </summary>
public sealed class ActivityTracker : IActivityTracker
{
    private readonly object _gate = new();
    private readonly List<Scope> _running = [];

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public ActivityStatus? Current
    {
        get
        {
            lock (_gate)
            {
                return LatestShown()?.Status;
            }
        }
    }

    /// <inheritdoc/>
    public IActivityScope Begin(ActivityKind kind, string? statusText = null, CancellationToken cancellationToken = default)
    {
        if (statusText is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        }

        var scope = new Scope(this, new ActivityStatus(Guid.NewGuid(), kind, statusText ?? ActivityStatus.DefaultText(kind)),
            cancellationToken);
        lock (_gate)
        {
            _running.Add(scope);
        }

        Raise();

        // A token that is already cancelled ends the activity as soon as it is listened to, so it is listened to last.
        scope.ListenTo(cancellationToken);
        return scope;
    }

    /// <inheritdoc/>
    public bool CancelCurrent()
    {
        Scope? target;
        lock (_gate)
        {
            target = LatestShown();
            target?.MarkCancelled();
        }

        if (target is null)
        {
            return false;
        }

        target.Cancel();
        Raise();
        return true;
    }

    // The most recent of the operations that are running and have not been cancelled. Call with the gate held.
    private Scope? LatestShown()
    {
        for (var i = _running.Count - 1; i >= 0; i--)
        {
            if (!_running[i].IsCancelled)
            {
                return _running[i];
            }
        }

        return null;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Scope : IActivityScope
    {
        private readonly ActivityTracker _owner;
        private readonly CancellationTokenSource _source;
        private ActivityStatus _status;
        private CancellationTokenRegistration _registration;
        private bool _ended;

        public Scope(ActivityTracker owner, ActivityStatus status, CancellationToken linkedTo)
        {
            _owner = owner;
            _status = status;
            _source = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);

            // The token is read once: a source that has been disposed refuses to give it out.
            CancellationToken = _source.Token;
        }

        public ActivityStatus Status
        {
            get
            {
                lock (_owner._gate)
                {
                    return _status;
                }
            }
        }

        public CancellationToken CancellationToken { get; }

        // Set with the tracker's gate held.
        public bool IsCancelled { get; private set; }

        public void ListenTo(CancellationToken token)
        {
            if (token.CanBeCanceled)
            {
                _registration = token.Register(static state => ((Scope)state!).EndByCancellation(), this);
            }
        }

        public void MarkCancelled() => IsCancelled = true;

        public void Cancel()
        {
            try
            {
                _source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation ended just as it was cancelled: nothing is left to cancel.
            }
        }

        // The operation's own token was cancelled: the scope's token, linked to it, must be cancelled before the scope ends,
        // or ending would unlink it first.
        private void EndByCancellation()
        {
            Cancel();
            Dispose();
        }

        public void Update(string statusText)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
            lock (_owner._gate)
            {
                if (_ended)
                {
                    return;
                }

                _status = _status with { Text = statusText };
            }

            _owner.Raise();
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_ended)
                {
                    return;
                }

                _ended = true;
                _owner._running.Remove(this);
            }

            _registration.Dispose();
            _source.Dispose();
            _owner.Raise();
        }
    }
}
