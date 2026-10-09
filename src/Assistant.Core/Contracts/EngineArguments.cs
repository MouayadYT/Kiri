using System.Globalization;

namespace Assistant.Core.Contracts;

/// <summary>
/// The command-line options of the model engine that a model's profile or a hardware preset may set (PROJECT_SPEC §5.6),
/// and the one place that decides which they are. The model host starts the engine hidden, offline and on a UNIX socket,
/// and no setting may undo that, so an argument is accepted only when it is on this list, spelled in its long form, with
/// a value in range. Options the host chooses itself (the model, the socket, the context, the devices, the projector)
/// are never on it.
/// </summary>
/// <remarks>
/// Arguments are separate tokens, as they are on the engine's command line: <c>["--threads", "6", "--no-kv-offload"]</c>.
/// The same check runs where a profile is defined, where the app builds a load request, and again in the host, which
/// does not trust its pipe.
/// </remarks>
public static class EngineArguments
{
    private static readonly string[] CacheTypes = ["f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1"];
    private static readonly string[] FlashAttentionModes = ["on", "off", "auto"];

    // Flags of one option share a key, so "--jinja" and "--no-jinja" count as the same option.
    private static readonly Dictionary<string, Option> Options = new(StringComparer.Ordinal)
    {
        ["--threads"] = Option.Number("threads", 1, 1024),
        ["--threads-batch"] = Option.Number("threads-batch", 1, 1024),
        ["--batch-size"] = Option.Number("batch-size", 1, 65536),
        ["--ubatch-size"] = Option.Number("ubatch-size", 1, 65536),
        ["--flash-attn"] = Option.Choice("flash-attn", FlashAttentionModes),
        ["--cache-type-k"] = Option.Choice("cache-type-k", CacheTypes),
        ["--cache-type-v"] = Option.Choice("cache-type-v", CacheTypes),
        ["--no-kv-offload"] = Option.Switch("kv-offload"),
        ["--jinja"] = Option.Switch("jinja"),
        ["--no-jinja"] = Option.Switch("jinja"),
        ["--reasoning-budget"] = Option.Number("reasoning-budget", -1, 1_000_000),
    };

    /// <summary>The flags that may be used, in their long form.</summary>
    public static IReadOnlyCollection<string> AllowedFlags => Options.Keys;

    /// <summary>Whether <paramref name="arguments"/> are all allowed. Null or empty is allowed.</summary>
    public static bool IsValid(IReadOnlyList<string>? arguments) => Validate(arguments) is null;

    /// <summary>
    /// Checks <paramref name="arguments"/> and describes the first problem, or returns <see langword="null"/> when they
    /// are all allowed. The description never repeats an argument, which could be a path.
    /// </summary>
    public static string? Validate(IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        while (index < arguments.Count)
        {
            var flag = arguments[index];
            if (flag is null || !Options.TryGetValue(flag, out var option))
            {
                return $"Argument {index + 1} is not an engine option that may be set.";
            }

            if (!seen.Add(option.Key))
            {
                return $"Argument {index + 1} sets an option that was already set.";
            }

            index++;
            if (option.Kind == OptionKind.Switch)
            {
                continue;
            }

            if (index >= arguments.Count || !option.Accepts(arguments[index]))
            {
                return $"Argument {index + 1} is missing or is not a value the option accepts.";
            }

            index++;
        }

        return null;
    }

    /// <summary>
    /// Combines argument lists, later lists after earlier ones: an option a later list sets replaces the same option
    /// from an earlier list, keeping the earlier one's position.
    /// </summary>
    /// <exception cref="ArgumentException">A list is not valid (<see cref="Validate"/>).</exception>
    public static IReadOnlyList<string> Merge(params IReadOnlyList<string>?[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var order = new List<string>();
        var entries = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            if (layer is null)
            {
                continue;
            }

            if (Validate(layer) is { } problem)
            {
                throw new ArgumentException(problem, nameof(layers));
            }

            for (var index = 0; index < layer.Count;)
            {
                var option = Options[layer[index]];
                var length = option.Kind == OptionKind.Switch ? 1 : 2;
                if (!entries.ContainsKey(option.Key))
                {
                    order.Add(option.Key);
                }

                entries[option.Key] = layer.Skip(index).Take(length).ToArray();
                index += length;
            }
        }

        return order.SelectMany(key => entries[key]).ToArray();
    }

    private enum OptionKind
    {
        Switch,
        Number,
        Choice,
    }

    private sealed record Option(string Key, OptionKind Kind, int Minimum = 0, int Maximum = 0, string[]? Choices = null)
    {
        public static Option Switch(string key) => new(key, OptionKind.Switch);

        public static Option Number(string key, int minimum, int maximum) => new(key, OptionKind.Number, minimum, maximum);

        public static Option Choice(string key, string[] choices) => new(key, OptionKind.Choice, Choices: choices);

        public bool Accepts(string? value) => Kind switch
        {
            OptionKind.Number => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                && number >= Minimum && number <= Maximum,
            OptionKind.Choice => Choices!.Contains(value, StringComparer.Ordinal),
            _ => false,
        };
    }
}
