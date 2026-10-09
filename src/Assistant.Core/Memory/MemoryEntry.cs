using System.Text;

namespace Assistant.Core.Memory;

/// <summary>What a thing the Assistant remembers is.</summary>
public enum MemoryKind
{
    /// <summary>Something the user told the Assistant about themselves or how they want things done. The model is told these, and the user can rewrite them.</summary>
    Note = 0,

    /// <summary>What the user calls a device in their home ("the AC"), and the device that is.</summary>
    HomeDevice = 1,

    /// <summary>The chat a person's messages go to, once the user has chosen it.</summary>
    MessageRoute = 2,

    /// <summary>A way the user wants something done from now on, which the Assistant itself follows: which display the Clock window opens on.</summary>
    Preference = 3,

    /// <summary>
    /// Which chat is a person's on one messaging service (their iMessage chat, their WhatsApp chat), learned when a message was sent there: asking for that
    /// service again goes straight to it. It is not what they prefer (<see cref="MessageRoute"/>), only where each service's messages go.
    /// </summary>
    MessageChat = 4,
}

/// <summary>
/// One thing the Assistant remembers for the user (Settings, under Memory). It is kept on this PC only. Everything in it is the user's own
/// (private content, PROJECT_SPEC §3.2), so none of it is ever logged.
/// </summary>
/// <param name="Id">The entry's id.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Text">What it says, as the user reads it in Settings: one short sentence.</param>
/// <param name="CreatedAt">When it was first remembered.</param>
public sealed record MemoryEntry(Guid Id, MemoryKind Kind, string Text, DateTimeOffset CreatedAt)
{
    /// <summary>What an entry that stands for something is looked up by: the words the user says for a device, or a person's id. Empty for a note.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>What an entry that stands for something stands for: a device's id, or where a person's messages go. Empty for a note.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>A new note that says <paramref name="text"/>.</summary>
    public static MemoryEntry Note(string text, DateTimeOffset now) => new(Guid.NewGuid(), MemoryKind.Note, text, now);

    // Keeps what is remembered out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}, Kind = {Kind}");
        return true;
    }
}

/// <summary>What keeps memory tidy.</summary>
public static class MemoryRules
{
    /// <summary>The most entries kept; the oldest notes make room for new ones.</summary>
    public const int MaxEntries = 200;

    /// <summary>The longest an entry's text may be.</summary>
    public const int MaxTextLength = 300;

    /// <summary>The longest a key or a value may be.</summary>
    public const int MaxValueLength = 1000;

    /// <summary>The most notes the model is told, newest first, and the most characters they may take.</summary>
    public const int MaxNotesTold = 40;

    /// <summary>The most characters of notes the model is told.</summary>
    public const int MaxCharactersTold = 2400;

    /// <summary><paramref name="text"/> as one line with single spaces, cut to <see cref="MaxTextLength"/>.</summary>
    public static string Clean(string? text)
    {
        var builder = new StringBuilder();
        var lastWasSpace = true;
        foreach (var character in text ?? string.Empty)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var line = builder.ToString().Trim();
        return line.Length > MaxTextLength ? line[..MaxTextLength].TrimEnd() : line;
    }

    /// <summary>The letters and digits of <paramref name="text"/> in lower case with single spaces: two texts that fold alike say the same.</summary>
    public static string Fold(string? text)
    {
        var builder = new StringBuilder();
        var lastWasSpace = true;
        foreach (var character in text ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
