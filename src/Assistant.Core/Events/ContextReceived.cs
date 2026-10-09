using Assistant.Core.Domain;

namespace Assistant.Core.Events;

/// <summary>
/// Context arrived for the assistant to act on, such as files from File Explorer or a selection captured with a
/// hotkey or from the browser.
/// </summary>
/// <param name="Items">The context, including its in-memory content.</param>
/// <param name="Source">Where the context came from.</param>
public sealed record ContextReceived(IReadOnlyList<ContextItem> Items, InvocationSource Source)
{
    /// <summary>
    /// Identifier of the action the user picked with the context, or <see langword="null"/> to open it for a
    /// free-form ask.
    /// </summary>
    public string? ActionId { get; init; }
}
