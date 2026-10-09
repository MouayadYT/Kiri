using System.Globalization;
using Assistant.Core.Displays;
using Assistant.Core.Memory;

namespace Assistant.Core.Clock;

/// <summary>
/// Where on a display a window sits, as shares of the room it has there: 0 is the left (or top) edge, 1 the right (or bottom) edge with the window
/// still wholly on the display, and a half is the middle. <see cref="Width"/> and <see cref="Height"/> say how much of the display the window itself
/// takes, when that was measured, so that a move "by 30% of the display" can be worked out.
/// </summary>
/// <param name="X">How far across, from 0 to 1.</param>
/// <param name="Y">How far down, from 0 to 1.</param>
/// <param name="Width">The window's width as a share of the display's, or 0 when not known.</param>
/// <param name="Height">The window's height as a share of the display's, or 0 when not known.</param>
public sealed record ClockSpot(double X, double Y, double Width = 0, double Height = 0)
{
    /// <summary>
    /// The spot after a move of <paramref name="across"/> and <paramref name="down"/>, each a share of the whole display (0.3 is "30% of the monitor",
    /// negative for left and up), kept on the display.
    /// </summary>
    public ClockSpot Moved(double across, double down)
    {
        static double Step(double share, double size) => share / Math.Max(1 - Math.Clamp(size, 0, 0.95), 0.05);
        return this with { X = Math.Clamp(X + Step(across, Width), 0, 1), Y = Math.Clamp(Y + Step(down, Height), 0, 1) };
    }

    /// <summary>The spot in words: "30% across and 70% down".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(X * 100):0}% across and {Math.Round(Y * 100):0}% down");
}

/// <summary>Where the user wants the Clock app's window: on which display (none for wherever it is), and where on it (none for wherever it lands).</summary>
/// <param name="Display">The display, or <see langword="null"/> when the user chose none.</param>
/// <param name="Spot">The place on it, or <see langword="null"/> when the user chose none.</param>
public sealed record ClockPlacement(DisplayInfo? Display, ClockSpot? Spot);

/// <summary>
/// Where the user wants the Clock app's window ("always put the alarm window on the left monitor", "move it down 30%"). It is one thing the Assistant
/// remembers (Settings, under Memory, where the user can take it back): the display's own name and the words the user chose it by, so that after the
/// displays were plugged in differently the choice is still the one they meant, and the place on the display as shares of it.
/// </summary>
public static partial class ClockPlace
{
    /// <summary>What the choice is kept under.</summary>
    public const string Key = "clock.display";

    /// <summary>Keeps <paramref name="display"/>, which the user chose by saying <paramref name="said"/>, as where the Clock window goes.</summary>
    public static Task<MemoryEntry?> SaveAsync(
        IMemoryStore memory, IReadOnlyList<DisplayInfo> displays, DisplayInfo display, string said, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        SaveAsync(memory, displays, display, said, ReadSpot(memory), now, cancellationToken);

    /// <summary>
    /// Keeps where the Clock window goes: <paramref name="display"/> (chosen by saying <paramref name="said"/>), or wherever it is when there is none,
    /// and <paramref name="spot"/> on it, or wherever it lands when there is none.
    /// </summary>
    public static Task<MemoryEntry?> SaveAsync(
        IMemoryStore memory, IReadOnlyList<DisplayInfo> displays, DisplayInfo? display, string said, ClockSpot? spot, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(displays);
        var where = display is null ? "where it is" : "on " + DisplayChoice.Describe(displays, display);
        var at = spot is null ? string.Empty : ", " + spot.Describe();
        var place = spot is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"|{spot.X:0.###}|{spot.Y:0.###}");
        return memory.SaveAsync(
            new MemoryEntry(Guid.NewGuid(), MemoryKind.Preference, $"The Clock window (alarms and timers) opens {where}{at}.", now)
            {
                Key = Key,
                Value = (display?.Id ?? string.Empty) + "|" + MemoryRules.Fold(said).Replace('|', ' ') + place,
            },
            cancellationToken);
    }

    /// <summary>
    /// The display the Clock window goes on among <paramref name="displays"/>, or <see langword="null"/> when the user chose none, or the one they chose is
    /// not there now and their words fit no other.
    /// </summary>
    public static DisplayInfo? Read(IMemoryStore? memory, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (Fields(memory) is not { } fields)
        {
            return null;
        }

        var (id, said) = (fields[0], fields.Length > 1 ? fields[1] : string.Empty);
        if (id.Length == 0 && said.Length == 0)
        {
            return null;
        }

        return displays.FirstOrDefault(display => string.Equals(display.Id, id, StringComparison.OrdinalIgnoreCase)) ?? DisplayChoice.Choose(displays, said);
    }

    /// <summary>Where on its display the Clock window goes, or <see langword="null"/> when the user chose no place.</summary>
    public static ClockSpot? ReadSpot(IMemoryStore? memory)
    {
        if (Fields(memory) is not { Length: >= 4 } fields
            || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return null;
        }

        return new ClockSpot(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    /// <summary>Everything the user chose about where the Clock window goes, or <see langword="null"/> when they chose nothing.</summary>
    public static ClockPlacement? ReadPlacement(IMemoryStore? memory, IReadOnlyList<DisplayInfo> displays)
    {
        var (display, spot) = (Read(memory, displays), ReadSpot(memory));
        return display is null && spot is null ? null : new ClockPlacement(display, spot);
    }

    /// <summary>
    /// What <paramref name="entry"/> (the place as it is kept) becomes when the user rewrites it by hand in Settings, under Memory, as
    /// <paramref name="typed"/>: the two percentages in what they typed ("94% across and 36% down", or just "100 30") become the place on the display,
    /// and the display stays the one that was chosen. <see langword="null"/> when what they typed holds no two numbers from 0 to 100.
    /// </summary>
    public static MemoryEntry? Rewritten(MemoryEntry entry, string? typed)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (TryReadSpot(typed, out var spot) is false)
        {
            return null;
        }

        var fields = entry.Value.Split('|');
        var (id, said) = (fields[0], fields.Length > 1 ? fields[1] : string.Empty);

        // The words about the display are kept as they stand: only where on it changes.
        var words = entry.Text.TrimEnd('.');
        var cut = SpotInWords().Match(words);
        var display = (cut.Success ? words[..cut.Index] : words).TrimEnd(',', ' ');
        return entry with
        {
            Text = $"{display}, {spot.Describe()}.",
            Value = string.Create(CultureInfo.InvariantCulture, $"{id}|{said}|{spot.X:0.###}|{spot.Y:0.###}"),
        };
    }

    /// <summary>Reads a place from words: "94% across and 36% down" in either order, or two numbers, the first across and the second down.</summary>
    public static bool TryReadSpot(string? text, out ClockSpot spot)
    {
        spot = new ClockSpot(0, 0);
        var words = text ?? string.Empty;
        var across = Percent().Match(words) is { Success: true } named ? Number(named.Groups["across"].Value) : null;
        var down = PercentDown().Match(words) is { Success: true } other ? Number(other.Groups["down"].Value) : null;
        if (across is null && down is null)
        {
            // Just two numbers: the last two in what was typed, so that "Display 1, 100 30" reads as 100 and 30.
            var numbers = Numbers().Matches(words).Select(match => Number(match.Value)).ToList();
            if (numbers.Count >= 2)
            {
                (across, down) = (numbers[^2], numbers[^1]);
            }
        }

        if (across is not { } x || down is not { } y)
        {
            return false;
        }

        spot = new ClockSpot(x / 100.0, y / 100.0);
        return true;
    }

    private static double? Number(string digits) =>
        double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100 ? value : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<across>\d{1,3}(?:\.\d+)?)\s*%?\s*across", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Percent();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<down>\d{1,3}(?:\.\d+)?)\s*%?\s*down", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PercentDown();

    [System.Text.RegularExpressions.GeneratedRegex(@"\d{1,3}(?:\.\d+)?\s*%?\s*(?:across|down)|\d{1,3}(?:\.\d+)?\s*%", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SpotInWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"\d{1,3}(?:\.\d+)?", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Numbers();

    private static string[]? Fields(IMemoryStore? memory) => memory?.Find(MemoryKind.Preference, Key)?.Value.Split('|');
}