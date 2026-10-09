using System.Globalization;
using System.Text.Json;
using Assistant.Core.Clock;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Displays;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.Tools;

namespace Assistant.Tools.Clock;

/// <summary>
/// <c>set_clock_display</c>: where the Windows Clock app's window is put when the Assistant sets an alarm or a timer: which display (monitor) it is on
/// ("always move the alarm window to the left monitor"), and where on that display ("move it down about 30%", "30% to the left", "put it in the top
/// right corner"). The display is worked out from how the user said it (left, right, the main one, its number), and a move from where the window is
/// now, by a share of the display. The user is then shown what will change (the display is marked on the screen itself) and asked whether it is right.
/// Only on a yes is the choice remembered (Settings, under Memory) and a Clock window that is open moved there; from then on every alarm and timer
/// opens there. Words that fit no display, or more than one, are told to the model with the displays there are, and nobody is asked about a guess.
/// </summary>
public sealed class SetClockDisplayTool(IClockApp? clock, IDisplays? displays, IMemoryStore? memory, IDisplayPointer? pointer = null, TimeProvider? time = null) : ITool
{
    /// <summary>The tool's name.</summary>
    public const string Name = "set_clock_display";

    /// <summary>How far a move goes when the user did not say: a tenth of the display.</summary>
    public const int DefaultPercent = 10;

    private static readonly string[] Moves = ["up", "down", "left", "right"];

    private static readonly Dictionary<string, (double? X, double? Y)> Places = new(StringComparer.Ordinal)
    {
        ["top left"] = (0, 0), ["top"] = (null, 0), ["top right"] = (1, 0),
        ["left"] = (0, null), ["center"] = (0.5, 0.5), ["right"] = (1, null),
        ["bottom left"] = (0, 1), ["bottom"] = (null, 1), ["bottom right"] = (1, 1),
    };

    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        Name,
        "Choose where the Clock app's window (the alarm or timer popup) opens, and remember it: display for another monitor, move and percent to move it (down " +
        "30%), or place for a corner, an edge or the center. The user is shown it and asked, so do not ask in words.",
        [
            new ToolParameter("display", ToolParameterType.String, "Which monitor, as the user said it: left, right, main, or its number.", Required: false, MaxLength: 40),
            new ToolParameter("move", ToolParameterType.String, "Which way to move it from where it is.", Required: false, Choices: Moves),
            new ToolParameter("percent", ToolParameterType.Integer, "How far, as a percentage of the monitor, such as 30.", Required: false, Minimum: 1, Maximum: 100),
            new ToolParameter(
                "place", ToolParameterType.String, "Or a place: top left, top, top right, left, center, right, bottom left, bottom or bottom right.",
                Required: false, MaxLength: 20),
        ],
        RiskLevel.SideEffect,
        timeout: TimeSpan.FromSeconds(30));

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return clock is { IsInstalled: true } && displays is not null && memory is not null && ClockRequests.IsAbout(context);
    }

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => IsOffered(context);

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (chosen, refusal) = await ReadAsync(call, arguments, cancellationToken).ConfigureAwait(false);
        return refusal ?? await UseAsync(call, chosen!, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>What will change is worked out before the user is asked, and the display marked on the screen, so that the question is about something they can see.</remarks>
    public async Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (chosen, refusal) = await ReadAsync(call, arguments, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return ToolPlan.Refuse(refusal);
        }

        var planned = chosen!;
        var details = new List<ConfirmationDetail>();
        if (planned.Display is { } display)
        {
            details.Add(new ConfirmationDetail("Display", DisplayChoice.Describe(planned.All, display)));
            details.Add(new ConfirmationDetail("Size", string.Create(CultureInfo.CurrentCulture, $"{display.Width} × {display.Height}")));
            try
            {
                pointer?.PointOut(display, "The Clock window will open here");
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // The mark is a help; the question says which display in words too.
            }

            details.Add(new ConfirmationDetail("Marked", pointer is null ? "Not shown on the display" : "It is marked on that display for a few seconds."));
        }

        if (planned.Spot is { } spot)
        {
            details.Add(new ConfirmationDetail("Move", planned.MoveSaid));
            details.Add(new ConfirmationDetail("Place on the display", spot.Describe()));
        }

        var question = planned.Spot is null
            ? "Is this the right display for the Clock window?"
            : planned.Display is null ? $"Move the Clock window {planned.MoveSaid}?" : "Is this the right place for the Clock window?";
        return ToolPlan.Do(
            token => UseAsync(call, planned, token),
            new ToolConfirmation(
                ConfirmationKind.Other,
                question,
                details,
                planned.Spot is null ? "Yes, use it" : "Yes, move it",
                "Alarms and timers will open there from now on. You can change this in Settings, under Memory."));
    }

    private async Task<(Chosen? Chosen, ToolResult? Refusal)> ReadAsync(ToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (clock is not { IsInstalled: true } || displays is null || memory is null)
        {
            return (null, Failed(call, "The Windows Clock app is not installed on this PC, so there is no window to place. Tell the user."));
        }

        var said = Text(arguments, "display");
        var move = Text(arguments, "move").ToLowerInvariant();
        var place = Text(arguments, "place").ToLowerInvariant().Replace('-', ' ').Replace("centre", "center", StringComparison.Ordinal).Replace("middle", "center", StringComparison.Ordinal);
        if (said.Length == 0 && move.Length == 0 && place.Length == 0)
        {
            return (null, Invalid(call, "Say what to change: display for another monitor, move with percent to move the window, or place for a corner, an edge or the center."));
        }

        var all = DisplayChoice.InOrder(displays.List());
        if (all.Count == 0)
        {
            return (null, Failed(call, "Windows did not say which displays this PC has, so nothing was changed. Tell the user."));
        }

        DisplayInfo? display = null;
        if (said.Length > 0)
        {
            if (all.Count == 1 && move.Length == 0 && place.Length == 0)
            {
                return (null, Failed(call, "This PC has one display, so the Clock window already opens on it. Nothing was changed. Tell the user."));
            }

            // With one display, "this monitor" is the one there is; with more, the words must fit exactly one.
            display = all.Count == 1 ? null : DisplayChoice.Choose(all, said);
            if (display is null && all.Count > 1)
            {
                var listed = string.Join("; ", all.Select(each => DisplayChoice.Describe(all, each)));
                return (null, ToolErrors.Result(
                    call, ToolResultStatus.Failed, ToolErrors.InvalidArguments,
                    $"It is not clear which display \"{said}\" is. This PC has: {listed}. Ask the user which one they mean, then call again with its number. Nothing was changed."));
            }
        }

        if (move.Length == 0 && place.Length == 0)
        {
            return (new Chosen(all, display, said, null, string.Empty), null);
        }

        // Where the window is now: where it really is when it is open, else where it was last put. A named place needs neither.
        var remembered = ClockPlace.ReadSpot(memory);
        var now = await clock.ReadSpotAsync(cancellationToken).ConfigureAwait(false) ?? remembered;
        ClockSpot spot;
        string moveSaid;
        if (place.Length > 0)
        {
            if (!Places.TryGetValue(place, out var named))
            {
                return (null, Invalid(call, "That is not a place. Give place as top left, top, top right, left, center, right, bottom left, bottom or bottom right."));
            }

            var start = now ?? new ClockSpot(0.5, 0.5);
            spot = start with { X = named.X ?? start.X, Y = named.Y ?? start.Y };
            moveSaid = place == "center" ? "to the center of the display" : "to the " + place + " of the display";
        }
        else
        {
            if (Array.IndexOf(Moves, move) < 0)
            {
                return (null, Invalid(call, "Say which way to move the window: up, down, left or right."));
            }

            if (now is null)
            {
                return (null, Failed(
                    call,
                    "The Clock window is not open, so there is no place to move it from, and nothing was changed. Tell the user to ask again while a timer or an alarm " +
                    "is showing, or to name a place such as top right."));
            }

            var percent = Math.Clamp(Number(arguments, "percent") ?? DefaultPercent, 1, 100);
            var share = percent / 100.0;
            spot = move switch
            {
                "up" => now.Moved(0, -share),
                "down" => now.Moved(0, share),
                "left" => now.Moved(-share, 0),
                _ => now.Moved(share, 0),
            };
            moveSaid = string.Create(CultureInfo.InvariantCulture, $"{move} by {percent}% of the display");
            if (Math.Abs(spot.X - now.X) < 0.005 && Math.Abs(spot.Y - now.Y) < 0.005)
            {
                return (null, Failed(call, $"The Clock window is already as far {move} as it goes on its display, so nothing was changed. Tell the user."));
            }
        }

        return (new Chosen(all, display, said, spot with { Width = 0, Height = 0 }, moveSaid), null);
    }

    private async Task<ToolResult> UseAsync(ToolCall call, Chosen chosen, CancellationToken cancellationToken)
    {
        try
        {
            pointer?.Clear();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // It goes by itself.
        }

        // A move keeps the display that was chosen before, and a new display keeps the place on it.
        var display = chosen.Display ?? ClockPlace.Read(memory, chosen.All);
        var said = chosen.Display is null && display is not null ? DisplayChoice.Describe(chosen.All, display) : chosen.Said;
        var spot = chosen.Spot ?? ClockPlace.ReadSpot(memory);
        var kept = await ClockPlace.SaveAsync(memory!, chosen.All, display, said, spot, (time ?? TimeProvider.System).GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (kept is null)
        {
            return Failed(call, "The choice could not be saved right now, so nothing was changed. Tell the user.");
        }

        // A Clock window that is open now goes there at once; the next alarm or timer opens there by itself.
        var moved = await clock!.ApplyPlaceAsync(cancellationToken).ConfigureAwait(false);
        var where = chosen.Spot is null
            ? "on " + DisplayChoice.Describe(chosen.All, chosen.Display!)
            : (chosen.Display is null ? string.Empty : "on " + DisplayChoice.Describe(chosen.All, chosen.Display) + ", ") + chosen.Spot.Describe();
        return new ToolResult(
            call.Id, call.ToolName, ToolResultStatus.Succeeded,
            SystemToolResults.Done($"Remembered: the Clock window opens {where} from now on." + (moved.Message.Length > 0 ? " " + moved.Message : string.Empty)));
    }

    private static string Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? string.Join(' ', (value.GetString() ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            : string.Empty;

    // A percentage the model wrote as a number or as text ("30", "30%").
    private static int? Number(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number is >= 0 and <= 1000)
        {
            return (int)Math.Round(number);
        }

        return value.ValueKind == JsonValueKind.String
            && double.TryParse((value.GetString() ?? string.Empty).Trim().TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var written)
            && written is >= 0 and <= 1000
            ? (int)Math.Round(written)
            : null;
    }

    private static ToolResult Failed(ToolCall call, string message) => ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);

    private ToolResult Invalid(ToolCall call, string message) =>
        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, message, ToolUsage.Describe(Definition));

    private sealed record Chosen(IReadOnlyList<DisplayInfo> All, DisplayInfo? Display, string Said, ClockSpot? Spot, string MoveSaid);
}