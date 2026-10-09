using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Domain;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Controls;

/// <summary>Draws each message of a conversation by its author: the user's in bubbles, the assistant's as text.</summary>
public sealed class MessageTemplateSelector : DataTemplateSelector
{
    /// <summary>Template for <see cref="MessageRole.User"/> messages.</summary>
    public DataTemplate? User { get; set; }

    /// <summary>Template for <see cref="MessageRole.Assistant"/> messages.</summary>
    public DataTemplate? Assistant { get; set; }

    /// <summary>Template for <see cref="MessageRole.Tool"/> messages, which by default are not shown.</summary>
    public DataTemplate? Tool { get; set; }

    /// <inheritdoc/>
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        MessageViewModel { Role: MessageRole.User } => User,
        MessageViewModel { Role: MessageRole.Assistant } => Assistant,
        MessageViewModel { Role: MessageRole.Tool } => Tool,
        _ => base.SelectTemplate(item, container),
    };
}
