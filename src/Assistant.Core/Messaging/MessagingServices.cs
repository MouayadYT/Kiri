namespace Assistant.Core.Messaging;

/// <summary>
/// The messaging services a chat may be on, by the names people know them by. A messaging app that bridges several (Beeper) names the service of
/// a chat as it likes: as its name ("iMessage"), or inside an account's id ("local-imessage_ba_…"). This reads either, so that a message is shown as
/// going through the service the person is really reached on, and so that "through iMessage" chooses that chat.
/// </summary>
public static class MessagingServices
{
    // What an app's own word for a service holds, and what the service is called. The more particular come first ("googlemessages" before "sms").
    private static readonly (string Holds, string Name)[] Known =
    [
        ("imessage", "iMessage"),
        ("whatsapp", "WhatsApp"),
        ("signal", "Signal"),
        ("telegram", "Telegram"),
        ("instagram", "Instagram"),
        ("messenger", "Messenger"),
        ("facebook", "Messenger"),
        ("discord", "Discord"),
        ("slack", "Slack"),
        ("linkedin", "LinkedIn"),
        ("googlemessages", "Google Messages"),
        ("gmessages", "Google Messages"),
        ("googlechat", "Google Chat"),
        ("twitter", "X"),
        ("beeper", "Beeper"),
        ("hungryserv", "Beeper"),
        ("matrix", "Matrix"),
        ("sms", "SMS"),
    ];

    /// <summary>Each known service by the one word it is written as, lower case ("imessage" for iMessage): what a request is read for.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Words { get; } = [.. Known.Select(entry => new KeyValuePair<string, string>(entry.Holds, entry.Name))];

    /// <summary>The known service named somewhere in <paramref name="text"/> ("iMessage chat with Sami in Beeper" is iMessage), or empty when it names none.</summary>
    public static string Find(string? text)
    {
        var squashed = new string((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        foreach (var (holds, name) in Known)
        {
            if (squashed.Contains(holds, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// What the service <paramref name="network"/> names is called: "iMessage" for "local-imessage_ba_1", and the text itself, tidied, for one that is
    /// not known. Empty when there is none.
    /// </summary>
    public static string Name(string? network)
    {
        var text = (network ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var squashed = new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        foreach (var (holds, name) in Known)
        {
            if (squashed.Contains(holds, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return text.Length > 40 ? text[..40] : text;
    }
}
