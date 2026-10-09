using Assistant.Core.Domain;

namespace Assistant.Core.Permissions;

/// <summary>
/// The uses of a capability the user has just said yes to, for the one piece of work that asked (PROJECT_SPEC §4.9, step 119). A capability set to ask every time
/// is not allowed until the user is asked about a use; the code that asked opens an approval for the work that follows (<see cref="Approve"/>), and the services
/// that check the permission (<c>IPermissionPolicy</c>) then see that use as allowed, so that a capture or a read made several calls down is allowed by the one
/// answer and not asked about again. It lasts exactly as long as the scope it returns, flows only into the work that was started inside it (it is held in the
/// flow of the asynchronous call, so another request in the same app never sees it), and is never saved. It can only raise a capability that is set to ask: one that is
/// off, or that this build cannot do, stays refused.
/// </summary>
public static class PermissionApprovals
{
    private static readonly AsyncLocal<Approval?> Current = new();

    /// <summary>Whether the user has said yes to a use of <paramref name="capability"/> in the work that is running.</summary>
    public static bool IsApproved(PermissionCapability capability)
    {
        for (var approval = Current.Value; approval is not null; approval = approval.Outer)
        {
            if (approval.Capability == capability)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens an approval of <paramref name="capability"/> for the work that follows until the returned scope is disposed. Call it only after the user said yes, in
    /// the method that goes on to do the work, since a scope opened inside a method that has returned is gone.
    /// </summary>
    public static IDisposable Approve(PermissionCapability capability)
    {
        var outer = Current.Value;
        Current.Value = new Approval(capability, outer);
        return new Scope(outer);
    }

    private sealed record Approval(PermissionCapability Capability, Approval? Outer);

    private sealed class Scope(Approval? outer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Current.Value = outer;
            }
        }
    }
}
