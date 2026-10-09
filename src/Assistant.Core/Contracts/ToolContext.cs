using System.Text;

namespace Assistant.Core.Contracts;

/// <summary>What a tool call runs for: the conversation it was made in, and the request being answered.</summary>
/// <param name="ConversationId">
/// The conversation whose turn the model made the call in. A tool that works on the conversation's files (<see cref="IConversationFiles"/>)
/// reaches only that conversation's, and a question to the user about a call (<see cref="IPermissionService"/>) is shown in it.
/// </param>
/// <param name="Request">
/// The user's message the turn answers, or <see langword="null"/> when there is none (a call made outside a model's turn). A registry that
/// loads tools by what is asked (the connected apps' tools, PROJECT_SPEC §4.8) reads it to decide which to offer. It is private content
/// (PROJECT_SPEC §3.2): it is never logged, and it is left out of <see cref="object.ToString"/>.
/// </param>
/// <param name="RunPause">
/// What stops the clock of the run the call is made in while the user is asked something (step 115), or <see langword="null"/> when the call is made
/// outside a run that has one. The time a person takes to read a question is theirs, not the run's.
/// </param>
/// <param name="Confirmations">
/// Told when the user is asked whether the call may be done and what they answered (step 117), or <see langword="null"/> when nothing follows the call. It is how
/// the activity log learns that a step waited for the user and what they said, without the question's words.
/// </param>
public sealed record ToolContext(Guid ConversationId, string? Request = null, IRunPause? RunPause = null, IConfirmationObserver? Confirmations = null)
{
    /// <summary>
    /// Whether the user's message came with a picture: one they attached or pasted, or a part of the screen they marked. What they ask ("what does this
    /// say?") is then about that picture, and a tool that would go and get another one (a picture of the whole screen) is not offered.
    /// </summary>
    public bool HasPicture { get; init; }

    // Keeps the request (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ConversationId = {ConversationId}");
        return true;
    }
}

/// <summary>
/// Follows the question the Assistant asks before it changes anything (PROJECT_SPEC §4.8, steps 115 and 117): that it is being asked, and what the user answered.
/// What is asked is not passed on, only that it was and how it ended. A call that is refused before anyone is asked is neither.
/// </summary>
public interface IConfirmationObserver
{
    /// <summary>The user is about to be asked, and the call waits for them.</summary>
    void Asking();

    /// <summary>The question ended, with this answer; only <see cref="Confirmation.ConfirmationDecision.Approved"/> lets the call run.</summary>
    void Answered(Confirmation.ConfirmationDecision decision);
}

/// <summary>The clock of an agent run, which can be held while the run waits for the user (PROJECT_SPEC §4.8, step 115).</summary>
public interface IRunPause
{
    /// <summary>Holds the run's time limit until the returned scope is disposed. Scopes may nest; the clock runs again when the last one ends.</summary>
    IDisposable Pause();
}
