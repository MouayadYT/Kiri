namespace Assistant.UI.Windowing;

/// <summary>What the Assistant's one window is showing (PROJECT_SPEC §4.1, §4.2).</summary>
public enum AssistantWindowState
{
    /// <summary>The Search or Ask bar: a compact pill with a single line to type in.</summary>
    Compact,

    /// <summary>The floating conversation: the tall panel that holds a conversation. The pill grows into it.</summary>
    FloatingConversation,
}
