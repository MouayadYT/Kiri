namespace Assistant.UI.ViewModels;

/// <summary>
/// Saves the messages of a conversation as they are asked and answered (PROJECT_SPEC §3.5, §4.2, §4.3), one at a time
/// and without holding up the conversation: the floating conversation and the History window both tell it what was said,
/// and it writes to the local history, if history is on.
/// </summary>
public interface IConversationRecorder
{
    /// <summary>
    /// Saves <paramref name="message"/> as part of the conversation <paramref name="conversationId"/>, as it is now. A
    /// message that was recorded before is changed where it stands: the user's question is recorded when it is asked,
    /// and again if something is attached to it, and the answer when it ends, however it ended. Messages are saved in the
    /// order they are recorded, and a failure to save never reaches the conversation.
    /// </summary>
    void Record(Guid conversationId, MessageViewModel message);
}
