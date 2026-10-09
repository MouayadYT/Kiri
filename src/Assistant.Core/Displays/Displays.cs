using System.Globalization;

namespace Assistant.Core.Displays;

/// <summary>One of this PC's displays (monitors), where Windows has it on the desktop.</summary>
/// <param name="Id">Windows' own name for it (<c>\\.\DISPLAY2</c>), which stays the same while it is plugged in.</param>
/// <param name="Number">Its number as Windows shows it in Settings: 2 for Display 2.</param>
/// <param name="Left">The left edge of the display on the desktop, in pixels.</param>
/// <param name="Top">Its top edge.</param>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="IsMain">Whether it is the main display.</param>
public sealed record DisplayInfo(string Id, int Number, int Left, int Top, int Width, int Height, bool IsMain)
{
    /// <summary>What it is called: "Display 2".</summary>
    public string Name => "Display " + Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The displays of this PC.</summary>
public interface IDisplays
{
    /// <summary>The displays that are in use, in no particular order; none when Windows does not say.</summary>
    IReadOnlyList<DisplayInfo> List();
}

/// <summary>Shows the user which display is meant, on the display itself, the way Windows' own "Identify" does.</summary>
public interface IDisplayPointer
{
    /// <summary>Marks <paramref name="display"/> with <paramref name="words"/> for a few seconds. It takes no click and no key, and goes by itself.</summary>
    void PointOut(DisplayInfo display, string words);

    /// <summary>Takes the mark away now.</summary>
    void Clear();
}

/// <summary>
/// Works out which display the user means by how people say it: left, right, the middle one, the main one, the other one, or its number. A display is
/// only ever chosen when the words can mean one alone; otherwise nobody guesses and the user is asked.
/// </summary>
public static class DisplayChoice
{
    /// <summary>The displays from left to right, and from the top down where two are above each other.</summary>
    public static IReadOnlyList<DisplayInfo> InOrder(IEnumerable<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        return [.. displays.OrderBy(display => display.Left).ThenBy(display => display.Top).ThenBy(display => display.Number)];
    }

    /// <summary>Where <paramref name="display"/> is among <paramref name="displays"/>, in words: "on the left", "in the middle", "above the others".</summary>
    public static string Place(IReadOnlyList<DisplayInfo> displays, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(display);
        if (displays.Count < 2)
        {
            return "the only display";
        }

        // The displays that stand side by side with the main one make the row that "left", "middle" and "right" are said of. One that is mostly above
        // or below that row (a display mounted over another) is said to be above or below the display it stands over.
        var main = displays.FirstOrDefault(other => other.IsMain) ?? displays[0];
        var row = displays.Where(other => InRow(other, main)).OrderBy(Center).ThenBy(other => other.Number).ToList();
        if (!row.Contains(display))
        {
            var under = row.Where(other => Overlap(display.Left, display.Width, other.Left, other.Width) > 0)
                .OrderByDescending(other => Overlap(display.Left, display.Width, other.Left, other.Width)).FirstOrDefault();
            var above = Middle(display) < Middle(under ?? main);
            return (above ? "above " : "below ") + (under is null || row.Count == 1 ? "the others" : under.Name);
        }

        if (row.Count == 1)
        {
            // It is the row alone: every other display is over or under it.
            return displays.All(other => other == display || Middle(other) < Middle(display)) ? "below the others"
                : displays.All(other => other == display || Middle(other) > Middle(display)) ? "above the others"
                : "between the others";
        }

        var index = row.IndexOf(display);
        return index == 0 ? "on the left" : index == row.Count - 1 ? "on the right" : row.Count == 3 ? "in the middle" : "between the others";
    }

    private static double Center(DisplayInfo display) => display.Left + (display.Width / 2.0);

    private static double Middle(DisplayInfo display) => display.Top + (display.Height / 2.0);

    private static double Overlap(double start, double length, double otherStart, double otherLength) =>
        Math.Max(0, Math.Min(start + length, otherStart + otherLength) - Math.Max(start, otherStart));

    // Whether the display stands beside the main one: at least half of the shorter of the two is level with the other.
    private static bool InRow(DisplayInfo display, DisplayInfo main) =>
        display == main || Overlap(display.Top, display.Height, main.Top, main.Height) >= Math.Min(display.Height, main.Height) / 2.0;
    /// <summary><paramref name="display"/> as the user is told of it: "Display 2, on the left (main display)".</summary>
    public static string Describe(IReadOnlyList<DisplayInfo> displays, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return display.Name + (displays.Count > 1 ? ", " + Place(displays, display) : string.Empty) + (display.IsMain && displays.Count > 1 ? " (main display)" : string.Empty);
    }

    /// <summary>
    /// The display <paramref name="said"/> means among <paramref name="displays"/>, or <see langword="null"/> when the words fit none or more than one.
    /// </summary>
    public static DisplayInfo? Choose(IReadOnlyList<DisplayInfo> displays, string? said)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0)
        {
            return null;
        }

        var words = Words(said);
        if (words.Count == 0)
        {
            return null;
        }

        // A number is the display's own: "2", "monitor 2", "display two".
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];

            // "One" is a number only where it follows what it counts ("monitor one"), or stands alone: "the big one" names no display.
            if (word == "one" && words.Count > 1 && (index == 0 || words[index - 1] is not ("display" or "monitor" or "screen" or "number")))
            {
                continue;
            }

            var number = word switch
            {
                "one" or "first" or "1st" => 1,
                "two" or "second" or "2nd" => 2,
                "three" or "third" or "3rd" => 3,
                "four" or "fourth" or "4th" => 4,
                _ => int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            };

            // "The second monitor" of two is the one that is not the main one, as Windows itself calls it.
            if (word == "second" && displays.Count == 2 && displays.Count(display => !display.IsMain) == 1 && displays.All(display => display.Number != 2 || display.IsMain))
            {
                return displays.Single(display => !display.IsMain);
            }

            if (number > 0 && displays.Where(display => display.Number == number).ToList() is { Count: 1 } numbered)
            {
                return numbered[0];
            }
        }

        bool Has(params string[] any) => words.Any(word => any.Contains(word, StringComparer.Ordinal));
        if (Has("main", "primary"))
        {
            return displays.Where(display => display.IsMain).ToList() is { Count: 1 } main ? main[0] : null;
        }

        if (Has("other", "secondary") && displays.Count == 2)
        {
            return displays.Where(display => !display.IsMain).ToList() is { Count: 1 } other ? other[0] : null;
        }

        if (displays.Count < 2)
        {
            return null;
        }

        if (Has("left", "leftmost", "lefthand"))
        {
            return displays.Where(display => Place(displays, display) == "on the left").ToList() is { Count: 1 } left ? left[0] : null;
        }

        if (Has("right", "rightmost", "righthand"))
        {
            return displays.Where(display => Place(displays, display) == "on the right").ToList() is { Count: 1 } right ? right[0] : null;
        }

        if (Has("middle", "center", "centre", "central"))
        {
            return displays.Where(display => Place(displays, display) == "in the middle").ToList() is { Count: 1 } middle ? middle[0] : null;
        }

        if (Has("top", "upper", "above"))
        {
            return displays.Where(display => Place(displays, display).StartsWith("above", StringComparison.Ordinal)).ToList() is { Count: 1 } top ? top[0] : null;
        }

        if (Has("bottom", "lower", "below"))
        {
            return displays.Where(display => Place(displays, display).StartsWith("below", StringComparison.Ordinal)).ToList() is { Count: 1 } bottom ? bottom[0] : null;
        }

        return null;
    }

    private static List<string> Words(string? text)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        foreach (var character in (text ?? string.Empty) + " ")
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        return words;
    }
}
