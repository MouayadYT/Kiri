namespace Assistant.UI.Messages;

/// <summary>
/// One typed part of an assistant message, drawn by the template for its kind: the model's prose
/// (<see cref="TextContent"/>), unboxed, is the default; a <see cref="MessageCard"/> is drawn in a card frame; any
/// other kind, such as code, an image gallery or a list of files, is drawn only by the <c>DataTemplate</c> for its own
/// type. Nothing is framed because of the message it is in, only because of what it is. Whoever produces a part
/// chooses its kind, so the conversation never guesses a presentation from text.
/// </summary>
public abstract class MessageContent
{
    /// <summary>
    /// Whether the part is as wide as a card, reaching past the text column on both sides, rather than lying in it.
    /// Wide parts sit further from the text around them than parts of the text do.
    /// </summary>
    public virtual bool IsWide => false;

    /// <summary>
    /// Whether the part is put away behind the button with three dots under the answer: the answer's workings (the steps of what the Assistant did, a
    /// question once the user has answered it), which the user opens when they want to see them. A part that says so and can change its mind reports
    /// the change as a property change of this name.
    /// </summary>
    public virtual bool IsTucked => false;

    // Content holds chat content (PROJECT_SPEC §3.2), so none of it reaches ToString, and so never a log.
    /// <inheritdoc/>
    public sealed override string ToString() => GetType().Name;
}
