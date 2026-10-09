using System.Text.RegularExpressions;

namespace Assistant.Core.Budgeting;

/// <summary>
/// The words that tell the user what fitting their request into the model's memory left out or cut short, and which
/// part was used (PROJECT_SPEC §4.4: no silent truncation). Only an item's label ever appears in them, never its content.
/// </summary>
public static partial class ContextBudgetNotices
{
    /// <summary>Tells the user that the oldest messages of a long conversation were not sent.</summary>
    public const string EarlierMessagesLeftOut =
        "This conversation is longer than the model can read at once, so its earliest messages were left out.";

    /// <summary>Tells the user that the middle of their own message was not sent.</summary>
    public const string MessageShortened =
        "Your message is longer than the model can read at once, so the middle of it was left out.";

    private const int MaxLabelLength = 60;

    /// <summary>Tells the user which context was cut short: the first part of each item was used.</summary>
    /// <param name="labels">The labels of the items, in the order they were given.</param>
    public static string ContextShortened(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return labels.Count == 1
            ? $"{Name(labels[0], "A piece of context")} was too long for the model, so only the first part of it was used."
            : $"{labels.Count} pieces of context were too long for the model, so only the first part of each was used.";
    }

    /// <summary>Tells the user which context did not fit at all.</summary>
    /// <param name="labels">The labels of the items, in the order they were given.</param>
    public static string ContextLeftOut(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return labels.Count == 1
            ? $"{Name(labels[0], "A piece of context")} didn't fit in what the model can read at once, so it was left out."
            : $"{labels.Count} pieces of context didn't fit in what the model can read at once, so they were left out.";
    }

    // The label on one line and not long, in quotes; or the fallback when it has none.
    internal static string Name(string label, string fallback)
    {
        var clean = WhiteSpace().Replace(label, " ").Trim();
        if (clean.Length == 0)
        {
            return fallback;
        }

        if (clean.Length <= MaxLabelLength)
        {
            return $"“{clean}”";
        }

        // Not through the middle of a surrogate pair.
        var cut = char.IsHighSurrogate(clean[MaxLabelLength - 1]) ? MaxLabelLength - 1 : MaxLabelLength;
        return $"“{clean[..cut].TrimEnd()}…”";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
