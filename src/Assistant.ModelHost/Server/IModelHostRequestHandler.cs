using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Server;

/// <summary>
/// Does the work one request asks for. The <see cref="ModelHostSession"/> does the rest: it reads the pipe, answers
/// frames it cannot read, runs requests concurrently, routes <see cref="CancelGenerationRequest"/> and handles
/// <see cref="ShutdownRequest"/>, neither of which reaches a handler.
/// </summary>
internal interface IModelHostRequestHandler
{
    /// <summary>
    /// Handles <paramref name="request"/>, sending its replies with the final one last. Runs concurrently with other
    /// requests.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="replies">Where the replies go.</param>
    /// <param name="cancellationToken">
    /// Cancelled when the owner cancels the generation or the host is shutting down. The handler may then throw
    /// <see cref="OperationCanceledException"/> without a final reply, and the session sends it.
    /// </param>
    Task HandleAsync(ModelHostRequest request, IModelHostReplies replies, CancellationToken cancellationToken);
}
