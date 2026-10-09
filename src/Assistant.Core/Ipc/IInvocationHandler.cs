namespace Assistant.Core.Ipc;

/// <summary>What the app does with an invocation request that arrived on its pipe (<see cref="InvocationServer"/>).</summary>
public interface IInvocationHandler
{
    /// <summary>
    /// Takes <paramref name="request"/>, whose shape and limits have been checked but whose paths have not, and answers at once:
    /// the work itself happens later, on the app's own time. Called on a thread-pool thread.
    /// </summary>
    InvocationReply Handle(InvocationRequest request);
}
