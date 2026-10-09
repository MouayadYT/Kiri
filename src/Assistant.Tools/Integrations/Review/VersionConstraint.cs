using System.Globalization;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Whether a version of a runtime satisfies what a package says it needs: an npm <c>engines</c> range (<c>&gt;=18 || ^20</c>) or a Python
/// <c>requires_python</c> specifier (<c>&gt;=3.10,&lt;4</c>). The review (PROJECT_SPEC §4.8, step 107) uses it to refuse a package that needs a newer runtime than
/// the one the Assistant sets up. It reads the forms packages actually use; anything it cannot read gives <see langword="null"/>, which the review
/// reports as "could not check", never as a pass.
/// </summary>
internal static class VersionConstraint
{
    private const int Unbounded = int.MaxValue;

    private readonly record struct V(int Major, int Minor, int Patch) : IComparable<V>
    {
        public int CompareTo(V other) =>
            Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
    }

    private readonly record struct Comparator(string Op, V Version)
    {
        public bool Holds(V actual)
        {
            var compared = actual.CompareTo(Version);
            return Op switch
            {
                ">=" => compared >= 0,
                ">" => compared > 0,
                "<=" => compared <= 0,
                "<" => compared < 0,
                _ => compared == 0,
            };
        }
    }

    /// <summary>The runtime's version as numbers: <c>24.21.0</c> or <c>3.12.15</c>.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        if (Parse(text) is { } parsed)
        {
            version = new Version(parsed.Major, parsed.Minor, parsed.Patch);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Whether <paramref name="version"/> satisfies the npm range <paramref name="range"/>; <see langword="null"/> when the range cannot be read.</summary>
    public static bool? SatisfiesNpm(string? range, Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        var actual = new V(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
        var any = false;
        foreach (var set in range.Split("||", StringSplitOptions.TrimEntries))
        {
            if (ReadNpmSet(set) is not { } comparators)
            {
                return null;
            }

            any = true;
            if (comparators.All(comparator => comparator.Holds(actual)))
            {
                return true;
            }
        }

        return any ? false : null;
    }

    /// <summary>Whether <paramref name="version"/> satisfies the Python specifier <paramref name="specifier"/>; <see langword="null"/> when it cannot be read.</summary>
    public static bool? SatisfiesPython(string? specifier, Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(specifier))
        {
            return true;
        }

        var actual = new V(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
        foreach (var raw in specifier.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var holds = PythonClause(raw, actual);
            if (holds is null)
            {
                return null;
            }

            if (!holds.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool? PythonClause(string clause, V actual)
    {
        var op = new string([.. clause.TakeWhile(character => character is '<' or '>' or '=' or '!' or '~')]);
        var rest = clause[op.Length..].Trim();
        if (rest.Length == 0)
        {
            return null;
        }

        var wildcard = rest.EndsWith(".*", StringComparison.Ordinal);
        var parts = Parts(wildcard ? rest[..^2] : rest);
        if (parts is null)
        {
            return null;
        }

        var low = new V(parts[0], parts.Length > 1 ? parts[1] : 0, parts.Length > 2 ? parts[2] : 0);
        switch (op)
        {
            case ">=":
                return actual.CompareTo(low) >= 0;
            case ">":
                return actual.CompareTo(low) > 0;
            case "<=":
                return actual.CompareTo(low) <= 0;
            case "<":
                return actual.CompareTo(low) < 0;
            case "==" or "===" or "!=":
                var equal = wildcard ? Prefix(parts, actual) : actual.CompareTo(low) == 0;
                return op == "!=" ? !equal : equal;
            case "~=":
                // ~=3.10 means >=3.10 and ==3.*; ~=3.10.2 means >=3.10.2 and ==3.10.*
                var keep = parts.Length >= 3 ? 2 : 1;
                return actual.CompareTo(low) >= 0 && Prefix(parts.Take(keep).ToArray(), actual);
            default:
                return null;
        }
    }

    // Whether the actual version starts with the given numbers.
    private static bool Prefix(int[] parts, V actual)
    {
        int[] numbers = [actual.Major, actual.Minor, actual.Patch];
        for (var index = 0; index < parts.Length && index < 3; index++)
        {
            if (parts[index] != numbers[index])
            {
                return false;
            }
        }

        return true;
    }

    // One set of an npm range (what is between two ||): comparators that must all hold.
    private static List<Comparator>? ReadNpmSet(string set)
    {
        var text = set.Trim();
        if (text.Length == 0 || text == "*" || text.Equals("x", StringComparison.OrdinalIgnoreCase) || text == "latest")
        {
            return [];
        }

        // "a - b" is a range of its own.
        var hyphen = text.IndexOf(" - ", StringComparison.Ordinal);
        if (hyphen > 0)
        {
            var from = Partial(text[..hyphen].Trim());
            var to = Partial(text[(hyphen + 3)..].Trim());
            if (from is null || to is null)
            {
                return null;
            }

            return [new Comparator(">=", Floor(from)), .. Upper(to, inclusive: true)];
        }

        // An operator is stuck to its version: ">= 1.2" is ">=1.2".
        var tokens = new List<string>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tokens.Count > 0 && tokens[^1] is ">=" or "<=" or ">" or "<" or "=" or "^" or "~" or "~>")
            {
                tokens[^1] += token;
            }
            else
            {
                tokens.Add(token);
            }
        }

        var comparators = new List<Comparator>();
        foreach (var token in tokens)
        {
            if (ReadNpmToken(token) is not { } read)
            {
                return null;
            }

            comparators.AddRange(read);
        }

        return comparators;
    }

    private static List<Comparator>? ReadNpmToken(string token)
    {
        if (token.StartsWith('^') || token.StartsWith('~'))
        {
            var caret = token[0] == '^';
            var rest = token[(token.StartsWith("~>", StringComparison.Ordinal) ? 2 : 1)..];
            var partial = rest.Trim().Length == 0 ? null : Partial(rest);
            if (partial is null)
            {
                return null;
            }

            var floor = Floor(partial);
            V ceiling;
            if (caret)
            {
                ceiling = partial[0] > 0 || partial.Length == 1 ? new V(partial[0] + 1, 0, 0)
                    : partial.Length < 3 || partial[1] > 0 ? new V(0, partial[1] + 1, 0)
                    : new V(0, 0, partial[2] + 1);
            }
            else
            {
                ceiling = partial.Length == 1 ? new V(partial[0] + 1, 0, 0) : new V(partial[0], partial[1] + 1, 0);
            }

            return [new Comparator(">=", floor), new Comparator("<", ceiling)];
        }

        var op = new string([.. token.TakeWhile(character => character is '<' or '>' or '=')]);
        var version = token.Length == op.Length ? null : Partial(token[op.Length..]);
        if (version is null)
        {
            return null;
        }

        if (op.Length == 0 || op == "=")
        {
            // A plain version, or one with wildcards, is a range of its own.
            return version.Length == 3 ? [new Comparator("=", Floor(version))] : [new Comparator(">=", Floor(version)), .. Upper(version, inclusive: false)];
        }

        return op switch
        {
            ">=" => [new Comparator(">=", Floor(version))],
            ">" => version.Length == 3 ? [new Comparator(">", Floor(version))] : [new Comparator(">=", Ceil(version))],
            "<=" => Upper(version, inclusive: true),
            "<" => [new Comparator("<", Floor(version))],
            _ => null,
        };
    }

    // The numbers of a possibly partial version ("1", "1.2", "1.2.x", "1.2.3"); wildcards end the list. Null when it is not one.
    private static int[]? Partial(string text)
    {
        var trimmed = text.Trim().TrimStart('v', '=').Trim();
        var cut = trimmed.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        if (trimmed.Length == 0 || trimmed is "*" or "x" or "X")
        {
            return [];
        }

        var numbers = new List<int>();
        foreach (var part in trimmed.Split('.'))
        {
            if (part is "x" or "X" or "*")
            {
                break;
            }

            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers.Count > 3 ? null : [.. numbers];
    }

    private static V Floor(int[] partial) => new(partial.Length > 0 ? partial[0] : 0, partial.Length > 1 ? partial[1] : 0, partial.Length > 2 ? partial[2] : 0);

    // The first version after a partial one: 1.2 -> 1.3.0.
    private static V Ceil(int[] partial) =>
        partial.Length == 0 ? new V(Unbounded, 0, 0) : partial.Length == 1 ? new V(partial[0] + 1, 0, 0) : partial.Length == 2 ? new V(partial[0], partial[1] + 1, 0) : new V(partial[0], partial[1], partial[2] + 1);

    // What a partial version allows at the top: 1.2 as an upper bound is below 1.3.0 (or at most 1.2.x when inclusive).
    private static List<Comparator> Upper(int[] partial, bool inclusive)
    {
        if (partial.Length == 0)
        {
            return [];
        }

        if (partial.Length == 3)
        {
            return [new Comparator("<=", Floor(partial))];
        }

        return [new Comparator("<", Ceil(partial))];
    }

    private static V? Parse(string? text)
    {
        var partial = text is null ? null : Partial(text);
        return partial is { Length: > 0 } ? Floor(partial) : null;
    }

    private static int[]? Parts(string text)
    {
        var numbers = new List<int>();
        foreach (var part in text.Split('.'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers.Count is >= 1 and <= 3 ? [.. numbers] : null;
    }
}
