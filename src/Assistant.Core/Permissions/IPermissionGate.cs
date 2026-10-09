using Assistant.Core.Domain;

namespace Assistant.Core.Permissions;

/// <summary>
/// The answer of <see cref="IPermissionGate"/>: whether a use of a capability may go ahead, and, when the user was asked and said yes, the means to carry that yes into the
/// work that follows (<see cref="Enter"/>), so that every service that checks the permission further down sees that one use as allowed.
/// </summary>
public sealed class PermissionGrant
{
    internal PermissionGrant(PermissionDecision decision, bool approvedByUser)
    {
        Decision = decision;
        ApprovedByUser = approvedByUser;
    }

    /// <summary>Why the capability may or may not be used.</summary>
    public PermissionDecision Decision { get; }

    /// <summary>Whether the use may go ahead.</summary>
    public bool IsGranted => Decision.IsAllowed;

    /// <summary>Whether the user was asked about this use and said yes (and not that the switch simply allowed it).</summary>
    public bool ApprovedByUser { get; }

    /// <summary>
    /// Opens the approval for the work that follows until the scope is disposed. Call it, in the method that does the work and only when <see cref="IsGranted"/>, so the
    /// services it calls see the use as allowed; for a use the switch allowed anyway it changes nothing.
    /// </summary>
    public IDisposable Enter() => ApprovedByUser && IsGranted ? PermissionApprovals.Approve(Decision.Capability) : NoScope.Instance;

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Asks whether one use of a capability may go ahead, and asks the user when they chose to be asked each time (PROJECT_SPEC §4.9, step 119). The code that starts a use
/// the user began (a shortcut pressed, a request answered) calls it first; it allows the use when the switch does, asks when the capability is set to ask every time, and
/// refuses when the capability is off or this build cannot do it. It is the one way a question about a capability reaches the user, so a capability set to ask never
/// lets a use go ahead that was not asked about. A check made further down (<see cref="Contracts.IPermissionPolicy"/>) is not a question: it sees only what was allowed
/// or approved here.
/// </summary>
public interface IPermissionGate
{
    /// <summary>Decides whether a use of <paramref name="capability"/> may go ahead, asking the user when the capability is set to ask each time.</summary>
    /// <param name="capability">What would be used.</param>
    /// <param name="detail">One line on what this use is, shown in the question; never private content.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<PermissionGrant> RequestAsync(PermissionCapability capability, string? detail = null, CancellationToken cancellationToken = default);
}
