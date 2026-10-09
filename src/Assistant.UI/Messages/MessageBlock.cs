namespace Assistant.UI.Messages;

/// <summary>
/// One block of an assistant message's text, such as a paragraph, a heading or a list, as
/// <see cref="MessageTextParser"/> reads it. Each kind of block has its own template (Themes/Controls/Messages.xaml).
/// Blocks compare by value, so a streaming message redraws only the blocks that changed.
/// </summary>
public abstract record MessageBlock
{
    // Blocks hold chat content (PROJECT_SPEC §3.2), so their text never reaches ToString, and so never a log.
    /// <inheritdoc/>
    public sealed override string ToString() => GetType().Name;
}
