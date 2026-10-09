using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>
/// Knows which long-running operations are under way, so the Assistant can show a transient activity state (the
/// Searching chip) for as long as one runs, and lets the user cancel it. Anything that waits on a search, the model or
/// a tool reports itself here; nothing in it is private content.
/// </summary>
public interface IActivityTracker
{
    /// <summary>
    /// Raised when <see cref="Current"/> may have changed. It can be raised from any thread, so a subscriber that
    /// touches the UI hops to it.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// The operation to show: the one that began most recently of those still running and not cancelled, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    ActivityStatus? Current { get; }

    /// <summary>
    /// Reports that an operation of <paramref name="kind"/> has begun. Dispose the scope when it ends, however it ends.
    /// </summary>
    /// <param name="kind">What the operation is doing.</param>
    /// <param name="statusText">
    /// The status to show, or <see langword="null"/> for the kind's own. Never private content; see
    /// <see cref="ActivityStatus.Text"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// The operation's own token, if it has one: cancelling it ends the activity, and cancelling the activity cancels
    /// the scope's <see cref="IActivityScope.CancellationToken"/>, which is linked to it.
    /// </param>
    IActivityScope Begin(ActivityKind kind, string? statusText = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels <see cref="Current"/>: its scope's token is cancelled, and it stops showing at once, without waiting for
    /// the operation to wind down.
    /// </summary>
    /// <returns><see langword="true"/> when there was an activity to cancel.</returns>
    bool CancelCurrent();
}
