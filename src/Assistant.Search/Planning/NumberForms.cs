using System.Globalization;

namespace Assistant.Search.Planning;

/// <summary>
/// The ways one small number is written in a name or a request: "3", "three", "3rd", "third". People type one and files hold
/// another ("Milestone Three" for "milestone 3"), so a word that is a number is held to be the same as any other form of it.
/// Only the numbers 1 to 20 and the tens up to 100 are read; anything else is not a number here.
/// </summary>
internal static class NumberForms
{
    private static readonly string[] Cardinals =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
        "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    ];

    private static readonly string[] Ordinals =
    [
        "zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth", "eleventh",
        "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth", "eighteenth", "nineteenth", "twentieth",
    ];

    private static readonly Dictionary<string, int> Values = Build();

    /// <summary>The value of <paramref name="word"/> when it is a number in one of its forms (lower case): "3", "three", "3rd", "third".</summary>
    public static bool TryValue(string word, out int value)
    {
        value = 0;
        return !string.IsNullOrEmpty(word) && Values.TryGetValue(word, out value);
    }

    /// <summary>Whether the two words are the same number written differently, or the same way.</summary>
    public static bool Same(string first, string second) =>
        TryValue(first, out var one) && TryValue(second, out var other) && one == other;

    /// <summary>
    /// The other forms of <paramref name="word"/> when it is a number: "three" is "3", "3rd" and "third"; "3" is "three". Empty when
    /// the word is not a number. The word itself is not among them.
    /// </summary>
    public static IReadOnlyList<string> OtherForms(string word)
    {
        if (!TryValue(word, out var value))
        {
            return [];
        }

        var forms = new List<string> { value.ToString(CultureInfo.InvariantCulture) };
        if (value < Cardinals.Length)
        {
            forms.Add(Cardinals[value]);
            forms.Add(Ordinals[value]);
        }

        forms.Add(value.ToString(CultureInfo.InvariantCulture) + OrdinalSuffix(value));
        return [.. forms.Where(form => !string.Equals(form, word, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)];
    }

    private static Dictionary<string, int> Build()
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var number = 0; number < Cardinals.Length; number++)
        {
            values[number.ToString(CultureInfo.InvariantCulture)] = number;
            values[Cardinals[number]] = number;
            values[Ordinals[number]] = number;
            values[number.ToString(CultureInfo.InvariantCulture) + OrdinalSuffix(number)] = number;
        }

        return values;
    }

    private static string OrdinalSuffix(int number) => (number % 100) switch
    {
        11 or 12 or 13 => "th",
        _ => (number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" },
    };
}
