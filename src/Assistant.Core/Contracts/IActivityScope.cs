using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>
/// One running operation that is reported to the <see cref="IActivityTracker"/>. It ends when it is disposed, or when
/// the token it was begun with is cancelled.
/// </summary>
public interface IActivityScope : IDisposable
{
    /// <summary>What the operation reports now.</summary>
    ActivityStatus Status { get; }

    /// <summary>
    /// Cancelled when the user cancels the activity, or when the token it was begun with is cancelled. The operation
    /// passes it on to whatever it is waiting on.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Changes the status text, for an operation that moves between stages. See <see cref="ActivityStatus.Text"/>.</summary>
    void Update(string statusText);
}
