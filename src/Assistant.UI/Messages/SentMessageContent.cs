using System.Windows.Media;
using Assistant.Core.Tools;

namespace Assistant.UI.Messages;

/// <summary>
/// The mark of the messaging service a message goes through, drawn small at the corner of the person's picture (the message reference): the service's
/// own colours and a speech bubble. iMessage is its green, Beeper its blue to violet, and so on; a service that is not known is a plain grey bubble.
/// The mark follows the service the person is reached on, so a message that Beeper carries over iMessage shows iMessage's.
/// </summary>
public static class MessageChannelMark
{
    private static readonly Geometry Bubble = Frozen(
        "M 8,2.5 C 11.6,2.5 14,4.6 14,7.4 C 14,10.2 11.6,12.3 8,12.3 C 7.3,12.3 6.7,12.2 6.1,12.1 C 5.2,12.9 4,13.5 2.8,13.6 "
        + "C 3.5,12.9 3.9,12 4,11.1 C 2.7,10.2 2,8.9 2,7.4 C 2,4.6 4.4,2.5 8,2.5 Z");

    // Beeper's own: a rounded bubble with its tail at the lower left and the smile cut into it.
    private static readonly Geometry BeeperBubble = Frozen(
        "M 4.6,2.6 L 11.4,2.6 A 2.6,2.6 0 0 1 14,5.2 L 14,9.2 A 2.6,2.6 0 0 1 11.4,11.8 L 6.4,11.8 L 3.2,14 L 3.6,11.6 A 2.6,2.6 0 0 1 2,9.2 L 2,5.2 "
        + "A 2.6,2.6 0 0 1 4.6,2.6 Z M 5.2,7.4 C 6.6,9.4 9.4,9.4 10.8,7.4 L 10,6.9 C 9,8.3 7,8.3 6,6.9 Z");

    /// <summary>The colours behind the mark of <paramref name="service"/>.</summary>
    public static Brush Background(string? service) => (service ?? string.Empty) switch
    {
        "iMessage" or "SMS" or "Google Messages" => Gradient("#5FF777", "#0BBA29"),
        "Beeper" or "Matrix" => Gradient("#4A7DFF", "#8B3DFF"),
        "WhatsApp" => Gradient("#5EF27A", "#1FAF38"),
        "Signal" => Gradient("#5B8DFF", "#2C5BE8"),
        "Telegram" => Gradient("#41BCF5", "#1D93D2"),
        "Messenger" => Gradient("#00B2FF", "#A033FF"),
        "Instagram" => Gradient("#FEDA75", "#D62976"),
        "Discord" => Gradient("#7289DA", "#5865F2"),
        "Slack" => Gradient("#611F69", "#4A154B"),
        "LinkedIn" => Gradient("#2D8FD8", "#0A66C2"),
        _ => Gradient("#8E8E93", "#636366"),
    };

    /// <summary>The mark's shape, in a 16 by 16 box, drawn in white.</summary>
    public static Geometry Glyph(string? service) => service is "Beeper" or "Matrix" ? BeeperBubble : Bubble;

    private static Brush Gradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), new System.Windows.Point(0.5, 0), new System.Windows.Point(0.5, 1));
        brush.Freeze();
        return brush;
    }

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The first letters of the first two words of a name, for the disc drawn where a picture of the person would be; a question mark for no name.</summary>
    public static string Initials(string? name)
    {
        var words = (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(word => char.IsLetterOrDigit(word[0])).Take(2).ToArray();
        return words.Length == 0 ? "?" : string.Concat(words.Select(word => char.ToUpperInvariant(word[0])));
    }
}

/// <summary>
/// A message the Assistant has just sent, as the conversation shows it (the message reference): who it went to, with the mark of the service it went
/// through, and what it said, as the bubble it is in their chat. The copy button under the answer it is in copies what was sent. It comes into the
/// conversation after the Assistant's words ("It's sent."), out of a blur, as the reference's does.
/// </summary>
public sealed class SentMessageContent : MessageContent
{
    /// <summary>Creates the card for <paramref name="sent"/>.</summary>
    /// <param name="sent">The message as the tool that sent it described it.</param>
    public SentMessageContent(SentMessage sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        To = sent.To;
        Service = sent.Service;
        Channel = sent.Service.Length > 0 ? sent.Service : sent.App.Length > 0 ? sent.App : "Message";
        Message = sent.Text;
        IsPending = sent.IsPending;
        IsSample = sent.IsSample;
    }

    /// <summary>The card is as wide as a card.</summary>
    public override bool IsWide => true;

    /// <summary>Who it went to.</summary>
    public string To { get; }

    /// <summary>The first letters of their name, for the disc where their picture would be.</summary>
    public string Initials => MessageChannelMark.Initials(To);

    /// <summary>The service it went through, when known: which mark is drawn.</summary>
    public string Service { get; }

    /// <summary>What is written under the name: the service, or else the messaging app.</summary>
    public string Channel { get; }

    /// <summary>The colours of the service's mark.</summary>
    public Brush MarkBackground => MessageChannelMark.Background(Service);

    /// <summary>The service's mark.</summary>
    public Geometry MarkGlyph => MessageChannelMark.Glyph(Service);

    /// <summary>What the message said.</summary>
    public string Message { get; }

    /// <summary>Whether there are words to show in the bubble.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>Whether the messaging app is still sending it.</summary>
    public bool IsPending { get; }

    /// <summary>Whether the messaging app is made up, so that nothing reached anyone.</summary>
    public bool IsSample { get; }

    /// <summary>What is said under the bubble when it is not simply sent; empty otherwise.</summary>
    public string Note => IsSample ? "Sample: nothing was really sent" : IsPending ? "Sending…" : string.Empty;

    /// <summary>Whether there is a <see cref="Note"/>.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>The card as one text, for assistive technology.</summary>
    public string Text => $"Message to {To} through {Channel}: {Message}" + (HasNote ? ". " + Note : string.Empty);
}
