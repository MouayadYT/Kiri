using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Home;
using Assistant.Core.Memory;
using Assistant.Core.Tools;

namespace Assistant.Tools.Home;

/// <summary>
/// Which requests are about the user's home: one that uses a word for a thing in a home (a light, a fan, the thermostat), or a word of the name of
/// one of the user's own devices ("the window fan"). The home tools are offered only for these, so that they cost a small model nothing in every
/// other request, and for the rest of a conversation that began with one ("and the lamp too").
/// </summary>
internal static class HomeRequests
{
    private const int MaxRemembered = 64;

    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "light", "lamp", "bulb", "switch", "plug", "outlet", "socket", "fan", "thermostat", "heating", "heater", "radiator", "cooling", "conditioner",
        "conditioning", "ac", "blind", "curtain", "shutter", "shade", "garage", "lock", "unlock", "scene", "vacuum", "dim", "brighten", "brightness",
        "humidifier", "dehumidifier", "homeassistant", "hass", "sprinkler",
    };

    // The kinds of thing whose names count: what is switched and set, and not the hundreds of sensors, updates and automations beside them.
    private static readonly HashSet<string> NamedKinds = new(StringComparer.Ordinal)
    {
        "light", "switch", "fan", "cover", "climate", "lock", "media_player", "vacuum", "humidifier", "scene", "input_boolean", "water_heater", "valve", "siren",
    };

    private static readonly ConcurrentDictionary<Guid, byte> Active = new();

    /// <summary>Whether the request in <paramref name="context"/> is about the home, or an earlier one in its conversation was.</summary>
    public static bool IsAbout(ToolContext context, IReadOnlyList<HomeDevice> known)
    {
        if (context.Request is null)
        {
            return true;
        }

        if (Mentions(context.Request, known))
        {
            if (Active.Count >= MaxRemembered)
            {
                Active.Clear();
            }

            Active[context.ConversationId] = 0;
            return true;
        }

        return Active.ContainsKey(context.ConversationId);
    }

    private static bool Mentions(string request, IReadOnlyList<HomeDevice> known)
    {
        var words = HomeMatch.Words(request);
        if (words.Count == 0)
        {
            return false;
        }

        if (words.Any(Words.Contains) || HomeMatch.Squash(request).Contains("homeassistant", StringComparison.Ordinal))
        {
            return true;
        }

        var said = words.Where(word => word.Length >= 3).ToHashSet(StringComparer.Ordinal);
        return said.Count > 0 && known.Any(device => NamedKinds.Contains(device.Kind) && HomeMatch.Words(device.Name).Any(said.Contains));
    }
}

/// <summary>Finds the device the user meant by the name they said, among the ones an action can be done to.</summary>
internal static class HomeMatch
{
    /// <summary>The fewest points a device needs to be the one that was meant.</summary>
    public const int Enough = 50;

    // How far ahead of the next the best has to be, to be taken without asking.
    private const int Lead = 10;

    // Words that say nothing about which device.
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "the", "my", "a", "an", "our", "please", "to", "of", "in", "at", "for", "and", "on", "off", "up", "down", "turn", "switch", "set", "is", "it", "that", "this",
    };

    // What people call a kind of device when they do not say its name: "the AC" is a thermostat, "the lamp" a light.
    private static readonly Dictionary<string, string[]> KindWords = new(StringComparer.Ordinal)
    {
        ["ac"] = ["climate"], ["conditioner"] = ["climate"], ["conditioning"] = ["climate"], ["aircon"] = ["climate"], ["cooling"] = ["climate"],
        ["heating"] = ["climate"], ["heat"] = ["climate"], ["thermostat"] = ["climate"], ["heater"] = ["climate", "water_heater"], ["hvac"] = ["climate"],
        ["light"] = ["light"], ["lamp"] = ["light"], ["bulb"] = ["light"], ["fan"] = ["fan"], ["blind"] = ["cover"], ["curtain"] = ["cover"],
        ["shutter"] = ["cover"], ["shade"] = ["cover"], ["garage"] = ["cover"], ["lock"] = ["lock"], ["vacuum"] = ["vacuum"], ["humidifier"] = ["humidifier"],
        ["tv"] = ["media_player"], ["television"] = ["media_player"], ["speaker"] = ["media_player"],
    };

    /// <summary>The device meant, when one stands out, and the ones that come close, best first.</summary>
    public sealed record Found(HomeDevice? Device, IReadOnlyList<HomeDevice> Close);

    /// <summary>The words said, in the form a name is remembered under: <c>ac</c> for "the AC".</summary>
    public static string Key(string? spoken) => string.Join(' ', Words(spoken));

    /// <summary>The kinds of device the words said stand for ("the AC" is a thermostat), or none when they name no kind.</summary>
    public static IReadOnlyList<string> KindsSaid(string? spoken) =>
        [.. Words(spoken).SelectMany(word => KindWords.TryGetValue(word, out var kinds) ? kinds : []).Distinct(StringComparer.Ordinal)];

    /// <summary>The lower-case words of <paramref name="text"/> that say which device: no "the" or "my", and a plural as its singular.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var character in (text ?? string.Empty) + " ")
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (word.Length > 0)
            {
                var found = word.ToString();
                if (found.Length > 3 && found[^1] == 's' && found[^2] != 's')
                {
                    found = found[..^1];
                }

                if (!Noise.Contains(found) && !words.Contains(found, StringComparer.Ordinal))
                {
                    words.Add(found);
                }

                word.Clear();
            }
        }

        return words;
    }

    /// <summary>The letters and digits of <paramref name="text"/>, lower case and run together.</summary>
    public static string Squash(string? text) =>
        new((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// The device among <paramref name="devices"/> that <paramref name="spoken"/> names. A name said exactly wins; one that only holds the words
    /// said ("fan" for Window Fan) wins when no other does as well; otherwise nobody is guessed, and the close ones are given to ask about.
    /// </summary>
    public static Found Find(IEnumerable<HomeDevice> all, string? spoken)
    {
        var devices = all as IReadOnlyList<HomeDevice> ?? [.. all];
        var said = Words(spoken);
        if (said.Count == 0)
        {
            return new Found(null, []);
        }

        var scored = devices
            .Select(device => (Device: device, Score: Score(device, said)))
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var close = scored.Where(pair => pair.Score >= 30).Take(6).Select(pair => pair.Device).ToList();
        if (scored.Count > 0 && scored[0].Score >= Enough && (scored.Count == 1 || scored[0].Score - scored[1].Score >= Lead))
        {
            return new Found(scored[0].Device, close);
        }

        // No name fits, and what was said is a kind of thing ("the AC"): the one device of that kind is the one, and several are the ones to ask about,
        // by their names, so that the user is asked a question they can answer.
        if (close.Count == 0 && KindsSaid(spoken) is { Count: > 0 } kinds)
        {
            var ofKind = devices.Where(device => kinds.Contains(device.Kind, StringComparer.Ordinal) && !device.IsUnavailable)
                .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return ofKind.Count == 1 ? new Found(ofKind[0], ofKind) : new Found(null, [.. ofKind.Take(6)]);
        }

        return new Found(null, close);
    }

    private static int Score(HomeDevice device, IReadOnlyList<string> said)
    {
        var name = Words(device.Name);
        var shared = said.Count(word => name.Contains(word, StringComparer.Ordinal));
        int score;
        if (name.Count > 0 && shared == said.Count && shared == name.Count)
        {
            score = 100;
        }
        else if (shared == said.Count)
        {
            // Every word said is in the name; each word of the name that was not said makes it a little less likely the one.
            score = Math.Max(40, 70 - (6 * (name.Count - shared)));
        }
        else if (name.Count > 0 && shared == name.Count)
        {
            score = 60;
        }
        else if (Words(device.Id[(device.Id.IndexOf('.', StringComparison.Ordinal) + 1)..].Replace('_', ' ')).SequenceEqual(said, StringComparer.Ordinal))
        {
            score = 65;
        }
        else
        {
            score = said.Count >= 2 ? 30 * shared / said.Count : 0;
        }

        if (score == 0)
        {
            return 0;
        }

        // "The fan" is a fan before it is a switch called something fan; a device Home Assistant cannot reach is the last to be meant.
        if (said.Contains(device.Kind, StringComparer.Ordinal) || (device.Kind == "light" && (said.Contains("lamp", StringComparer.Ordinal) || said.Contains("bulb", StringComparer.Ordinal))))
        {
            score += 5;
        }

        return device.IsUnavailable ? Math.Max(1, score - 25) : score;
    }
}

/// <summary>What the home tools tell the model when Home Assistant could not be used.</summary>
internal static class HomeTexts
{
    public static string For(HomeFailure failure) => failure switch
    {
        HomeFailure.NotConnected => "Home Assistant is not connected. Tell the user they can connect it in Settings, under Integrations.",
        HomeFailure.LocalOnly => "The user's Home Assistant is reached over the internet and Local Only mode is on, so it was not used. Tell the user; they can turn Local Only off in Settings, under Privacy.",
        HomeFailure.Unauthorized => "Home Assistant did not accept the access token. Tell the user they can paste a new one in Settings, under Integrations.",
        HomeFailure.Refused => "Home Assistant could not do that: the device does not support it, or the value is out of its range. Tell the user.",
        HomeFailure.NotKept => "The access token for Home Assistant could not be read on this PC. Tell the user they can connect it again in Settings, under Integrations.",
        _ => "Home Assistant did not answer. It may be off, or this PC may be on another network. Tell the user.",
    };

    /// <summary>A kind as a person says it: Media player for <c>media_player</c>.</summary>
    public static string Kind(string kind) => kind switch
    {
        "climate" => "Thermostat",
        "cover" => "Cover",
        "input_boolean" => "Switch",
        _ => kind.Length == 0 ? kind : char.ToUpperInvariant(kind[0]) + kind[1..].Replace('_', ' '),
    };

    /// <summary>A state with its unit, as it is read: <c>23.5 °C</c>.</summary>
    public static string State(HomeDevice device) => device.Unit is { } unit ? $"{device.State} {unit}" : device.State;
}

/// <summary>
/// <c>control_home_device</c>: does one thing to one device of the user's Home Assistant (PROJECT_SPEC §4.8). The model gives the device's name as
/// the user said it; the tool finds which device that is among the ones the action can be done to, and when it cannot tell, says which ones come
/// close, and nobody is asked to confirm a guess. It changes something in the user's home, so the user confirms each call, with the device's own
/// name shown. What it is afterwards is read back from Home Assistant and said as it is.
/// </summary>
public sealed class ControlHomeDeviceTool(IHomeAssistant? home, IMemoryStore? memory = null) : ITool
{
    private const int MaxRemembered = 64;

    // How long after a name fitted no one device the device then used is taken to be the one meant by it.
    private static readonly TimeSpan AnswerTime = TimeSpan.FromMinutes(10);

    // The most words a message has that is taken for the answer to "which one?": an answer names a device, and a longer message is a request of its own.
    private const int MaxAnswerWords = 6;

    // The name each conversation last said that fitted no one device, with the devices it could have been: when a device is then controlled in the same
    // conversation, that is the user's answer, and it is remembered (Settings, under Memory), so they are asked once.
    private readonly ConcurrentDictionary<Guid, Unsettled> _unsettled = new();

    /// <summary>The tool's name.</summary>
    public const string Name = "control_home_device";

    private static readonly string[] OnOffKinds =
        ["light", "switch", "fan", "climate", "media_player", "input_boolean", "humidifier", "cover", "siren", "water_heater", "remote", "automation", "group", "vacuum"];

    private static readonly Dictionary<string, HomeAction> Actions = new(StringComparer.Ordinal)
    {
        ["turn_on"] = new("Turn on {0}?", "Turn on", "{0} was turned on.", [.. OnOffKinds, "scene", "script"], device => device.Kind switch
        {
            "vacuum" => ("vacuum", "start"),
            "scene" => ("scene", "turn_on"),
            _ => ("homeassistant", "turn_on"),
        }, Expect: "on"),
        ["turn_off"] = new("Turn off {0}?", "Turn off", "{0} was turned off.", OnOffKinds, device =>
            device.Kind == "vacuum" ? ("vacuum", "return_to_base") : ("homeassistant", "turn_off"), Expect: "off"),
        ["toggle"] = new("Switch {0} the other way?", "Toggle", "{0} was toggled.",
            ["light", "switch", "fan", "climate", "media_player", "input_boolean", "humidifier", "cover", "siren", "remote", "automation", "group"],
            _ => ("homeassistant", "toggle")),
        ["set_brightness"] = new("Set {0} to {1}% brightness?", "Set brightness", "{0} was set to {1}% brightness.", ["light"],
            _ => ("light", "turn_on"), "brightness_pct", 0, 100),
        ["set_temperature"] = new("Set {0} to {1}°?", "Set temperature", "{0} was set to {1}°.", ["climate", "water_heater"],
            device => (device.Kind, "set_temperature"), "temperature", 0, 120),
        ["set_speed"] = new("Set {0} to {1}% speed?", "Set speed", "{0} was set to {1}% speed.", ["fan"], _ => ("fan", "set_percentage"), "percentage", 0, 100),
        ["open"] = new("Open {0}?", "Open", "{0} is opening.", ["cover", "valve"], device => (device.Kind, device.Kind == "valve" ? "open_valve" : "open_cover")),
        ["close"] = new("Close {0}?", "Close", "{0} is closing.", ["cover", "valve"], device => (device.Kind, device.Kind == "valve" ? "close_valve" : "close_cover")),
        ["stop"] = new("Stop {0}?", "Stop", "{0} was stopped.", ["cover", "valve"], device => (device.Kind, device.Kind == "valve" ? "stop_valve" : "stop_cover")),
        ["lock"] = new("Lock {0}?", "Lock", "{0} was locked.", ["lock"], _ => ("lock", "lock")),
        ["unlock"] = new("Unlock {0}?", "Unlock", "{0} was unlocked.", ["lock"], _ => ("lock", "unlock")),
        ["run"] = new("Run {0}?", "Run", "{0} was run.", ["scene", "script", "button", "input_button", "automation"], device => device.Kind switch
        {
            "scene" => ("scene", "turn_on"),
            "script" => ("script", "turn_on"),
            "automation" => ("automation", "trigger"),
            _ => (device.Kind, "press"),
        }),
    };

    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        Name,
        "Control a device in the user's home through their Home Assistant: turn a light, switch, plug or fan on or off, dim a light, set a thermostat, open or " +
        "close a cover, lock a door, or run a scene or script. Use it whenever the user asks to turn something in their home on or off or to change it. Give the " +
        "device's name as the user said it; the tool finds the device, so do not open an app or search files for it.",
        [
            new ToolParameter("device", ToolParameterType.String, "The device's name as the user said it, such as window fan or bedroom lamp.", MaxLength: 80),
            new ToolParameter(
                "action", ToolParameterType.String,
                "What to do: turn_on, turn_off, toggle, set_brightness (a light), set_temperature (a thermostat), set_speed (a fan), open, close or stop (a cover or blind), " +
                "lock, unlock, or run (a scene, script or button).",
                Choices: [.. Actions.Keys]),
            new ToolParameter(
                "value", ToolParameterType.Number,
                "Only for set_brightness and set_speed (a percentage from 0 to 100) and set_temperature (the degrees). Leave it out otherwise.", Required: false, Minimum: 0, Maximum: 120),
        ],
        RiskLevel.SideEffect,
        timeout: TimeSpan.FromSeconds(30));

    /// <inheritdoc/>
    public Task PrepareAsync(ToolContext context, CancellationToken cancellationToken) => home?.LoadAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return home is { IsConnected: true } && HomeRequests.IsAbout(context, home.Known);
    }

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => IsOffered(context);

    /// <inheritdoc/>
    /// <remarks>
    /// After the user was asked which of several devices they meant, a message of a few words that names one of them is their answer, whatever came
    /// between (an answer they stopped, a slip of the keyboard): it is this tool's, and is not looked up in files or on the web.
    /// </remarks>
    public bool Claims(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return home is { IsConnected: true } && Answered(context) is not null;
    }

    // The device the user's message names among the ones they were just asked about, or null when it is not an answer to that.
    private HomeDevice? Answered(ToolContext context)
    {
        if (home is null || context.Request is not { Length: > 0 } request
            || !_unsettled.TryGetValue(context.ConversationId, out var asked) || DateTimeOffset.UtcNow - asked.At > AnswerTime
            || HomeMatch.Words(request).Count is 0 or > MaxAnswerWords)
        {
            return null;
        }

        var among = home.Known
            .Where(device => asked.Close.Count > 0 ? asked.Close.Contains(device.Id, StringComparer.Ordinal) : asked.Kinds.Contains(device.Kind, StringComparer.Ordinal))
            .ToList();
        return HomeMatch.Find(among, request).Device;
    }

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (planned, refusal) = await ReadAsync(call, arguments, context, cancellationToken).ConfigureAwait(false);
        return refusal ?? await DoAsync(call, planned!, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>The device is found before the user is asked, so the question names the device itself, and a name that fits none or several is told to the model.</remarks>
    public async Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (planned, refusal) = await ReadAsync(call, arguments, context, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return ToolPlan.Refuse(refusal);
        }

        var step = planned!;
        var details = new List<ConfirmationDetail>
        {
            new("Device", step.Device.Name),
            new("Kind", HomeTexts.Kind(step.Device.Kind)),
            new("Now", HomeTexts.State(step.Device)),
        };
        return ToolPlan.Do(
            token => DoAsync(call, step, token),
            new ToolConfirmation(ConfirmationKind.ConnectedApp, step.Words(step.Action.Question), details, step.Action.Button, "It is done in your Home Assistant."));
    }

    private async Task<(Planned? Planned, ToolResult? Refusal)> ReadAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var conversation = context.ConversationId;
        if (home is not { IsConnected: true })
        {
            return (null, Failed(call, HomeTexts.For(HomeFailure.NotConnected)));
        }

        var named = Text(arguments, "action").ToLowerInvariant().Replace(' ', '_');
        if (!Actions.TryGetValue(named, out var action))
        {
            return (null, Invalid(call, "Say what to do with the device: " + string.Join(", ", Actions.Keys) + "."));
        }

        var spoken = Text(arguments, "device");
        if (spoken.Length == 0)
        {
            return (null, Invalid(call, "Give the device's name as the user said it."));
        }

        double? value = null;
        if (action.DataKey is not null)
        {
            if (Number(arguments, "value") is not { } given || given < action.Minimum || given > action.Maximum)
            {
                return (null, Invalid(call, string.Create(CultureInfo.InvariantCulture, $"Give value as a number from {action.Minimum:0} to {action.Maximum:0} for {named}.")));
            }

            value = Math.Round(given, 1);
        }

        var listed = await home.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (listed.Failure != HomeFailure.None)
        {
            return (null, Failed(call, HomeTexts.For(listed.Failure)));
        }

        // What the user called a device before, and then said which one they meant, is that device from then on.
        var able = listed.Devices.Where(device => action.Kinds.Contains(device.Kind, StringComparer.Ordinal)).ToList();
        var key = HomeMatch.Key(spoken);
        var remembered = memory?.Find(MemoryKind.HomeDevice, key) is { } entry ? able.FirstOrDefault(device => string.Equals(device.Id, entry.Value, StringComparison.Ordinal)) : null;
        var found = remembered is not null ? new HomeMatch.Found(remembered, [remembered]) : HomeMatch.Find(able, spoken);

        // The name that was asked about, given again, with the user's own answer beside it: the device they named is the one.
        if (found.Device is null && Answered(context) is { } answer && able.FirstOrDefault(each => string.Equals(each.Id, answer.Id, StringComparison.Ordinal)) is { } meant)
        {
            found = new HomeMatch.Found(meant, [meant]);
        }

        if (found.Device is not { } device)
        {
            if (key.Length > 0)
            {
                if (_unsettled.Count >= MaxRemembered)
                {
                    _unsettled.Clear();
                }

                _unsettled[conversation] = new Unsettled(spoken, key, [.. found.Close.Select(close => close.Id)], HomeMatch.KindsSaid(spoken), DateTimeOffset.UtcNow);
            }

            return (null, Failed(call, found.Close.Count == 0
                ? $"No device called \"{spoken}\" that this can be done to was found in Home Assistant. Tell the user, or call get_home_devices to see what there is."
                : $"\"{spoken}\" could be more than one device: {string.Join(", ", found.Close.Select(close => $"{close.Name} ({HomeTexts.Kind(close.Kind).ToLowerInvariant()})"))}. "
                  + "Ask the user which one they mean, then call again with that name exactly; if they say any of them will do, use the first. Nothing was changed. "
                  + "Which one they choose is remembered, so they are asked once."));
        }

        if (device.IsUnavailable)
        {
            return (null, Failed(call, $"Home Assistant cannot reach {device.Name} right now (it shows as {device.State}), so nothing was changed. Tell the user."));
        }

        return (new Planned(device, action, value, conversation), null);
    }

    // The device that was controlled answers the name that fitted no one device a moment ago, when it is one that name could have meant: one of the devices
    // the user was asked about, or, when none was close, a device of the kind the name stands for ("the AC" is a thermostat, never a lamp).
    private async Task LearnAsync(Planned step, CancellationToken cancellationToken)
    {
        if (memory is null || !_unsettled.TryRemove(step.Conversation, out var asked) || DateTimeOffset.UtcNow - asked.At > AnswerTime)
        {
            return;
        }

        var fits = asked.Close.Count > 0
            ? asked.Close.Contains(step.Device.Id, StringComparer.Ordinal)
            : asked.Kinds.Contains(step.Device.Kind, StringComparer.Ordinal);
        if (!fits || string.Equals(asked.Key, HomeMatch.Key(step.Device.Name), StringComparison.Ordinal))
        {
            return;
        }

        await memory.SaveAsync(
            new MemoryEntry(Guid.NewGuid(), MemoryKind.HomeDevice, $"When you say \"{asked.Spoken}\" at home, you mean {step.Device.Name}.", DateTimeOffset.UtcNow)
            {
                Key = asked.Key,
                Value = step.Device.Id,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> DoAsync(ToolCall call, Planned step, CancellationToken cancellationToken)
    {
        var (domain, service) = step.Action.Service(step.Device);
        var data = step.Action.DataKey is { } key && step.Value is { } value ? new Dictionary<string, double> { [key] = value } : null;
        var result = await home!.CallAsync(domain, service, step.Device.Id, data, step.Action.Expect, cancellationToken).ConfigureAwait(false);
        if (!result.Done)
        {
            return Failed(call, HomeTexts.For(result.Failure) + " Do not say it was done.");
        }

        await LearnAsync(step, cancellationToken).ConfigureAwait(false);
        var done = step.Words(step.Action.Done);
        if (result.State is not { } state)
        {
            return Succeeded(call, done, step.Device, step.Device.State);
        }

        // What Home Assistant shows afterwards is said as it is: a device that did not follow is not reported as done.
        return step.Action.Expect is { } expected && state is "on" or "off" && state != expected
            ? Failed(call, $"Home Assistant took the request, but still shows {step.Device.Name} as {state}. Tell the user it may not have worked.")
            : Succeeded(call, $"{done} Home Assistant now shows it as {state}.", step.Device, state);
    }

    // What was done, in words for the model, with the device as it is now, which the conversation draws its card from.
    private static ToolResult Succeeded(ToolCall call, string message, HomeDevice device, string state) =>
        new(call.Id, call.ToolName, ToolResultStatus.Succeeded, HomeToolResults.Controlled(message, new HomeControlled(device.Id, device.Name, device.Kind, state)));

    private static ToolResult Failed(ToolCall call, string message) => ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);

    private ToolResult Invalid(ToolCall call, string message) =>
        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, message, ToolUsage.Describe(Definition));

    internal static string Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? string.Join(' ', (value.GetString() ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            : string.Empty;

    private static double? Number(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    // One thing that can be done: how the user is asked, what is said when it is done, the kinds of device it can be done to, and Home Assistant's service for it.
    private sealed record HomeAction(
        string Question, string Button, string Done, string[] Kinds, Func<HomeDevice, (string Domain, string Service)> Service,
        string? DataKey = null, double Minimum = 0, double Maximum = 0, string? Expect = null);

    private sealed record Unsettled(string Spoken, string Key, IReadOnlyList<string> Close, IReadOnlyList<string> Kinds, DateTimeOffset At);

    private sealed record Planned(HomeDevice Device, HomeAction Action, double? Value, Guid Conversation)
    {
        public string Words(string pattern) => string.Format(CultureInfo.CurrentCulture, pattern, Device.Name, Value?.ToString("0.#", CultureInfo.CurrentCulture));
    }
}

/// <summary>
/// <c>get_home_devices</c>: what the user's Home Assistant lists and what each thing is now: whether a light is on, what a sensor reads. It only
/// reads. With a name, a kind or a room it gives what fits; without, the things that can be switched and set, since a home has hundreds of sensors.
/// </summary>
public sealed class GetHomeDevicesTool(IHomeAssistant? home) : ITool
{
    /// <summary>The tool's name.</summary>
    public const string Name = "get_home_devices";

    /// <summary>The most devices one answer lists.</summary>
    public const int MaxListed = 30;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> Controlled = new(StringComparer.Ordinal)
    {
        "light", "switch", "fan", "cover", "climate", "lock", "media_player", "vacuum", "humidifier", "scene", "input_boolean", "water_heater", "valve",
    };

    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        Name,
        "Look at the devices in the user's home through their Home Assistant and what each is now: whether a light or a fan is on, what a temperature or humidity " +
        "sensor reads, whether a door is locked. Use it for questions about the home, and to find a device's exact name.",
        [
            new ToolParameter(
                "device", ToolParameterType.String,
                "A name, a kind or a room to look for, such as fan, bedroom or temperature. Leave it out to list the lights, switches and other things that can be controlled.",
                Required: false, MaxLength: 80),
        ],
        RiskLevel.ReadOnly,
        timeout: TimeSpan.FromSeconds(20));

    /// <inheritdoc/>
    public Task PrepareAsync(ToolContext context, CancellationToken cancellationToken) => home?.LoadAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return home is { IsConnected: true } && HomeRequests.IsAbout(context, home.Known);
    }

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => IsOffered(context);

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (home is not { IsConnected: true })
        {
            return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, HomeTexts.For(HomeFailure.NotConnected));
        }

        var listed = await home.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (listed.Failure != HomeFailure.None)
        {
            return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, HomeTexts.For(listed.Failure));
        }

        var said = HomeMatch.Words(ControlHomeDeviceTool.Text(arguments, "device"));
        var fitting = (said.Count == 0
                ? listed.Devices.Where(device => Controlled.Contains(device.Kind))
                : listed.Devices
                    .Select(device => (Device: device, Shared: Shared(device, said)))
                    .Where(pair => pair.Shared > 0)
                    .OrderByDescending(pair => pair.Shared)
                    .ThenBy(pair => HomeMatch.Words(pair.Device.Name).Count)
                    .Select(pair => pair.Device))
            .ToList();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("devices");
            foreach (var device in fitting.Take(MaxListed))
            {
                writer.WriteStartObject();
                writer.WriteString("name", device.Name);
                writer.WriteString("kind", device.Kind);
                writer.WriteString("state", HomeTexts.State(device));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (fitting.Count > MaxListed)
            {
                writer.WriteNumber("not_listed", fitting.Count - MaxListed);
                writer.WriteString("note", "There are more. Ask for a name, a kind or a room to narrow it.");
            }
            else if (fitting.Count == 0)
            {
                writer.WriteString("note", "Nothing in Home Assistant fits that. Tell the user, or look again without a name.");
            }

            writer.WriteEndObject();
        }

        return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <inheritdoc/>
    public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ToolPlan.Do(token => RunAsync(call, arguments, context, token)));

    // How many of the words said are in the device's name or are its kind.
    private static int Shared(HomeDevice device, IReadOnlyList<string> said)
    {
        var name = HomeMatch.Words(device.Name);
        var kind = HomeMatch.Words(device.Kind.Replace('_', ' '));
        return said.Count(word => name.Contains(word, StringComparer.Ordinal) || kind.Contains(word, StringComparer.Ordinal));
    }
}
