namespace Assistant.UI.Messages;

/// <summary>
/// Rich content that presents a result, such as a calculation, drawn in a black card among a message's other content.
/// Each kind of card derives from this class and supplies a <c>DataTemplate</c> for its type, drawn inside the shared
/// card frame (the <c>MessageCard</c> style in Themes/Controls/Messages.xaml). Only cards are framed; prose never is,
/// and neither are other results, such as a gallery or a list of files, which have presentations of their own.
/// </summary>
public abstract class MessageCard : MessageContent
{
    /// <summary>A card is always as wide as the card frame.</summary>
    public sealed override bool IsWide => true;
}
