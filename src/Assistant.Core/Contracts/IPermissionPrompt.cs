using Assistant.Core.Confirmation;
using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>What the user is asked when a capability set to ask every time is about to be used (PROJECT_SPEC §4.9, step 119).</summary>
/// <param name="Capability">The capability that would be used.</param>
/// <param name="Question">The question, such as "Let the Assistant capture part of your screen?".</param>
/// <param name="Detail">One line on what this use is and what it is for, or <see langword="null"/>; it never holds private content.</param>
public sealed record PermissionPromptRequest(PermissionCapability Capability, string Question, string? Detail = null);

/// <summary>
/// Puts the question of a capability that is set to ask every time in front of the user, outside a conversation (PROJECT_SPEC §4.9, step 119): the answer to the
/// shortcut for Visual Intelligence or for selected text, which has no conversation to ask in yet. In a conversation a tool's use is asked as a question in the
/// conversation (<see cref="IPermissionService"/>) and this is not used. Only <see cref="ConfirmationDecision.Approved"/> lets the use go ahead; an answer that does not
/// come, and a question that nothing could show, are a no.
/// </summary>
public interface IPermissionPrompt
{
    /// <summary>Asks the user and waits for the answer: one use only, which cannot be kept for a later one.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the question is taken back.</exception>
    Task<ConfirmationDecision> AskAsync(PermissionPromptRequest request, CancellationToken cancellationToken = default);
}
