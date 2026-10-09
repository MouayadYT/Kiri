using Assistant.Core.Messaging;

namespace Assistant.Core.People;

/// <summary>
/// Reads a handle someone is reached by in a messaging app ("@marcus:beeper.com", "sami@example.com"): whether a piece of text is one, the name in it
/// ("marcus"), and the messaging service its server is, when that is one the Assistant knows ("Beeper"). A messaging app's search usually looks at
/// what chats are called, not at handles, so the name in a handle is what the person's chat is looked for by, and its service says which of their
/// chats is meant.
/// </summary>
public static class PersonHandles
{
    /// <summary>Whether <paramref name="text"/> reads as a handle and not as a name: one word with an at sign or a colon in it.</summary>
    public static bool IsHandle(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length >= 3 && value.Length <= 200 && !value.Any(char.IsWhiteSpace)
            && (value.Contains('@', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal))
            && value.Any(char.IsLetter);
    }

    /// <summary>The name in a handle: "marcus" for "@marcus:beeper.com" and for "marcus@example.com". Empty when <paramref name="handle"/> is not a handle.</summary>
    public static string LocalPart(string? handle)
    {
        if (!IsHandle(handle))
        {
            return string.Empty;
        }

        var value = handle!.Trim().TrimStart('@', '!', '#');
        var end = value.IndexOfAny([':', '@']);
        return (end < 0 ? value : value[..end]).Trim('.', '_', '-');
    }

    /// <summary>The messaging service a handle's server is ("Beeper" for "@marcus:beeper.com"), or empty when it is not one that is known.</summary>
    public static string Service(string? handle)
    {
        if (!IsHandle(handle))
        {
            return string.Empty;
        }

        var value = handle!.Trim().TrimStart('@', '!', '#');
        var start = value.IndexOfAny([':', '@']);
        return start < 0 ? string.Empty : MessagingServices.Find(value[(start + 1)..]);
    }

    /// <summary>The name in a handle as a person's name is written: "Marcus" for "@marcus:beeper.com". Empty when there is none.</summary>
    public static string NameOf(string? handle)
    {
        var local = LocalPart(handle).Replace('.', ' ').Replace('_', ' ').Trim();
        return local.Length == 0 ? string.Empty : string.Join(' ', local.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
