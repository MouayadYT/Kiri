namespace Assistant.Core.Contracts;

/// <summary>Publishes in-process notifications between loosely coupled components.</summary>
/// <remarks>
/// Events stay in memory inside the process; the bus never persists them or logs their contents. Subscribers are held
/// weakly, so a subscription never keeps a view model alive.
/// </remarks>
public interface IAppEventBus
{
    /// <summary>
    /// Delivers <paramref name="appEvent"/> to each live subscriber of <typeparamref name="TEvent"/>, one at a time in
    /// subscription order and on the caller's context, and completes when every handler has run. A handler that
    /// throws does not stop delivery to the others.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled; the remaining handlers are skipped.
    /// </exception>
    Task PublishAsync<TEvent>(TEvent appEvent, CancellationToken cancellationToken = default)
        where TEvent : class;

    /// <summary>
    /// Subscribes <paramref name="subscriber"/> to events of type <typeparamref name="TEvent"/>. The bus holds the
    /// subscriber weakly: once nothing else references it, the subscription ends by itself.
    /// </summary>
    /// <param name="subscriber">The object that receives the events, typically a view model.</param>
    /// <param name="handler">
    /// Called with the subscriber and the event. It must not capture the subscriber, or the bus would keep it alive;
    /// use a static lambda such as <c>static (vm, e, ct) =&gt; vm.OnEventAsync(e, ct)</c>.
    /// </param>
    /// <returns>A subscription that unsubscribes when disposed.</returns>
    IDisposable Subscribe<TSubscriber, TEvent>(
        TSubscriber subscriber,
        Func<TSubscriber, TEvent, CancellationToken, Task> handler)
        where TSubscriber : class
        where TEvent : class;
}
