using Assistant.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Events;

/// <summary>
/// In-process, typed publish/subscribe. Subscribers are held through weak references, and dead subscriptions are
/// pruned whenever an event type is subscribed to or published.
/// </summary>
public sealed partial class AppEventBus(ILogger<AppEventBus> logger) : IAppEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = [];

    /// <inheritdoc/>
    public async Task PublishAsync<TEvent>(TEvent appEvent, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(appEvent);

        foreach (var subscription in GetLiveSubscriptions(typeof(TEvent)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // No ConfigureAwait(false): every handler runs on the publisher's context, not only the first.
                await subscription.DeliverAsync(appEvent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogHandlerFailed(logger, typeof(TEvent).Name, exception);
            }
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe<TSubscriber, TEvent>(
        TSubscriber subscriber,
        Func<TSubscriber, TEvent, CancellationToken, Task> handler)
        where TSubscriber : class
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(handler);
        if (ReferenceEquals(handler.Target, subscriber))
        {
            throw new ArgumentException(
                "The handler is bound to the subscriber, so the bus would keep it alive. Use a static lambda.",
                nameof(handler));
        }

        // The adapter captures only the handler, never the subscriber.
        var subscription = new Subscription(
            this,
            typeof(TEvent),
            subscriber,
            (target, appEvent, cancellationToken) => handler((TSubscriber)target, (TEvent)appEvent, cancellationToken));
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var subscriptions))
            {
                subscriptions = [];
                _subscriptions.Add(typeof(TEvent), subscriptions);
            }

            subscriptions.RemoveAll(static s => !s.IsLive);
            subscriptions.Add(subscription);
        }

        return subscription;
    }

    // Returns a snapshot, so handlers can subscribe and unsubscribe while an event is being delivered.
    private Subscription[] GetLiveSubscriptions(Type eventType)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(eventType, out var subscriptions))
            {
                return [];
            }

            subscriptions.RemoveAll(static s => !s.IsLive);
            if (subscriptions.Count == 0)
            {
                _subscriptions.Remove(eventType);
                return [];
            }

            return [.. subscriptions];
        }
    }

    private void Remove(Type eventType, Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(eventType, out var subscriptions)
                && subscriptions.Remove(subscription)
                && subscriptions.Count == 0)
            {
                _subscriptions.Remove(eventType);
            }
        }
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Error, Message = "A handler for {EventName} failed")]
    private static partial void LogHandlerFailed(ILogger logger, string eventName, Exception exception);

    private sealed class Subscription : IDisposable
    {
        private readonly AppEventBus _bus;
        private readonly Type _eventType;

        // The only reference to the subscriber, and a weak one.
        private readonly WeakReference<object> _subscriber;
        private readonly Func<object, object, CancellationToken, Task> _handler;
        private int _disposed;

        public Subscription(
            AppEventBus bus,
            Type eventType,
            object subscriber,
            Func<object, object, CancellationToken, Task> handler)
        {
            _bus = bus;
            _eventType = eventType;
            _subscriber = new WeakReference<object>(subscriber);
            _handler = handler;
        }

        public bool IsLive => Volatile.Read(ref _disposed) == 0 && _subscriber.TryGetTarget(out _);

        // Checked again at delivery, so a handler that unsubscribes another one mid-delivery takes effect at once.
        public Task DeliverAsync(object appEvent, CancellationToken cancellationToken) =>
            Volatile.Read(ref _disposed) == 0 && _subscriber.TryGetTarget(out var subscriber)
                ? _handler(subscriber, appEvent, cancellationToken)
                : Task.CompletedTask;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _bus.Remove(_eventType, this);
            }
        }
    }
}
