namespace Assistant.Core.Budgeting;

/// <summary>
/// Where a piece of context stands when the prompt cannot carry all of it (PROJECT_SPEC §5.5): the lower the value, the
/// sooner it is served from what is left of the window, and the later it is cut short or left out.
/// </summary>
public enum ContextPriority
{
    /// <summary>What the user picked on purpose for this question: files and pictures they attached.</summary>
    Selected = 0,

    /// <summary>What was captured from the screen for this question: the selected text, a screen region, a page.</summary>
    OnScreen = 1,

    /// <summary>What a search or a tool returned for this question.</summary>
    Retrieved = 2,

    /// <summary>Context the user attached to an earlier message of the conversation.</summary>
    EarlierAttached = 3,

    /// <summary>What a search or a tool returned for an earlier message of the conversation.</summary>
    EarlierRetrieved = 4,
}
