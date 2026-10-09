using System.Text;

namespace Assistant.Core.History;

/// <summary>
/// Titles for conversations. A conversation starts with a provisional title made from the first thing the user asked; a
/// better one, such as a title the local model writes, can replace it later.
/// </summary>
public static class ConversationTitle
{
    /// <summary>The most characters a provisional title has, the ellipsis that marks a cut included.</summary>
    public const int MaxLength = 80;

    private const string Ellipsis = "…";

    /// <summary>
    /// Makes a provisional title from <paramref name="firstRequest"/>, what the user asked first: its words on one line,
    /// cut short at a word with an ellipsis when it is longer than <see cref="MaxLength"/>. A request with no words gives
    /// an empty title.
    /// </summary>
    public static string Provisional(string? firstRequest)
    {
        if (string.IsNullOrWhiteSpace(firstRequest))
        {
            return string.Empty;
        }

        var line = OneLine(firstRequest);
        if (line.Length <= MaxLength)
        {
            return line;
        }

        var room = MaxLength - Ellipsis.Length;
        var cut = room;

        // Cut between words when there is a space in the second half of the room; otherwise a long word is cut where it is.
        var space = line.LastIndexOf(' ', room);
        if (space > room / 2)
        {
            cut = space;
        }
        else if (char.IsHighSurrogate(line[cut - 1]))
        {
            cut--;
        }

        return line[..cut].TrimEnd(' ', ',', ';', ':', '-', '–', '—') + Ellipsis;
    }

    /// <summary>The words of <paramref name="text"/> on one line, single-spaced.</summary>
    public static string OneLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = line.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                line.Append(' ');
                pendingSpace = false;
            }

            line.Append(character);
        }

        return line.ToString();
    }
}
