using System.Windows;
using System.Windows.Controls;
using Assistant.UI.Messages;

namespace Assistant.UI.Controls;

/// <summary>
/// Draws each part of a message according to what it is: prose as open text, a card inside the card frame, and any
/// other kind by the <c>DataTemplate</c> for its own type, so nothing is framed unless it is a card.
/// </summary>
public sealed class MessageContentTemplateSelector : DataTemplateSelector
{
    /// <summary>Template for <see cref="TextContent"/>: the prose's blocks, unboxed.</summary>
    public DataTemplate? Text { get; set; }

    /// <summary>Template for any <see cref="MessageCard"/>: the card frame around the card's own template.</summary>
    public DataTemplate? Card { get; set; }

    /// <inheritdoc/>
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        TextContent => Text,
        MessageCard => Card,

        // No template here: the content presenter then finds the DataTemplate for the item's own type.
        _ => base.SelectTemplate(item, container),
    };
}
