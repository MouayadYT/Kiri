using Assistant.Core.Domain;

namespace Assistant.Core.Budgeting;

/// <summary>
/// The fixed rules that rank context (PROJECT_SPEC §5.5), the same for every request: what the user explicitly selected
/// first, then what is on the screen now (the selection, a screen region, a page), then what a search or a tool
/// returned, and context attached to earlier messages last, with what was retrieved for them after what the user
/// attached. Nothing else decides: not the size of an item, not its name, not when within the turn it arrived.
/// </summary>
public static class ContextPriorityRules
{
    /// <summary>
    /// How <paramref name="item"/> came to be context: what it says, or, when it says nothing, what its kind implies: a
    /// file or a picture the user attached was selected by them, text, a screenshot or a page came from the screen, and
    /// search results were retrieved.
    /// </summary>
    public static ContextSource SourceOf(ContextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Source != ContextSource.Unspecified ? item.Source : item.Type switch
        {
            ContextItemType.File or ContextItemType.Image or ContextItemType.FileNotes => ContextSource.UserSelected,
            ContextItemType.SearchResults => ContextSource.Retrieval,
            _ => ContextSource.CurrentScreen,
        };
    }

    /// <summary>The rank of <paramref name="item"/> in a prompt whose newest message is, or is not, the one it was attached to.</summary>
    /// <param name="item">The context item.</param>
    /// <param name="isCurrent"><see langword="true"/> for an item of the message being answered; <see langword="false"/> for one of an earlier message.</param>
    public static ContextPriority Of(ContextItem item, bool isCurrent) => Of(SourceOf(item), isCurrent);

    /// <summary>The rank of context that came from <paramref name="source"/>.</summary>
    /// <param name="source">How the context came in; <see cref="ContextSource.Unspecified"/> ranks with the screen's context.</param>
    /// <param name="isCurrent"><see langword="true"/> for the message being answered; <see langword="false"/> for an earlier one.</param>
    public static ContextPriority Of(ContextSource source, bool isCurrent)
    {
        var retrieved = source is ContextSource.Retrieval or ContextSource.Tool;
        if (!isCurrent)
        {
            return retrieved ? ContextPriority.EarlierRetrieved : ContextPriority.EarlierAttached;
        }

        return source switch
        {
            ContextSource.UserSelected => ContextPriority.Selected,
            ContextSource.Retrieval or ContextSource.Tool => ContextPriority.Retrieved,
            _ => ContextPriority.OnScreen,
        };
    }
}
