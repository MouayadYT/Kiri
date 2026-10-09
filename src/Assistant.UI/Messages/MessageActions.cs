using System.Windows.Input;

namespace Assistant.UI.Messages;

/// <summary>
/// What the buttons under an answer do. The view that draws the conversation carries them out, as it does the copy button of a block of code.
/// </summary>
public static class MessageActions
{
    /// <summary>Copies the answer; its parameter is the answer's <c>MessageViewModel</c>.</summary>
    public static RoutedUICommand Copy { get; } = new("Copy", nameof(Copy), typeof(MessageActions));
}
