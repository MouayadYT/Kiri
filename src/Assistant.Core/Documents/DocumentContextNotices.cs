using System.Globalization;
using Assistant.Core.Budgeting;

namespace Assistant.Core.Documents;

/// <summary>
/// The words that tell the user how much of the file they attached was read for their question (PROJECT_SPEC §4.4: no silent
/// truncation). Only the file's name ever appears in them, never any of its text, and a file that was read whole needs none.
/// </summary>
public static class DocumentContextNotices
{
    /// <summary>
    /// What to tell the user about how <paramref name="fileName"/> was used, in the order to say it: nothing when all of the
    /// file's text went to the model.
    /// </summary>
    /// <param name="fileName">The file's name, shown as its label.</param>
    /// <param name="result">What the question about the file came to.</param>
    public static IReadOnlyList<string> For(string fileName, DocumentContextResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var notices = new List<string>(2);
        var name = ContextBudgetNotices.Name(fileName, "The attached file");
        if (result.Truncated)
        {
            notices.Add(Truncated(name));
        }

        var selection = result.Selection;
        if (!selection.IsComplete)
        {
            notices.Add(Partial(name, selection.Reason, selection.Passages.Count, selection.TotalPassages));
        }

        return notices;
    }

    /// <summary>Tells the user that the file named <paramref name="name"/> (in quotes) has text that was never read.</summary>
    internal static string Truncated(string name) =>
        $"{name} is very long, or some of it couldn't be read, so the answer may leave something out.";

    /// <summary>
    /// Tells the user that only <paramref name="used"/> of the <paramref name="total"/> parts of the file named <paramref name="name"/>
    /// (in quotes) were read, and which: those that match the question, the first ones, or ones spread across it.
    /// </summary>
    internal static string Partial(string name, PassageSelectionReason reason, int used, int total)
    {
        var all = string.Create(CultureInfo.InvariantCulture, $"{total}");
        var parts = string.Create(CultureInfo.InvariantCulture, $"{used} parts");
        return (reason, used == 1) switch
        {
            (PassageSelectionReason.Matched, true) =>
                $"Only the part of {name} that best matches your question was read, out of {all}.",
            (PassageSelectionReason.Matched, false) =>
                $"Only the {parts} of {name} that best match your question were read, out of {all}.",
            (PassageSelectionReason.NoMatch, true) =>
                $"Nothing in {name} matched the words of your question, so only its first part was read, out of {all}.",
            (PassageSelectionReason.NoMatch, false) =>
                $"Nothing in {name} matched the words of your question, so only its first {parts} were read, out of {all}.",
            (_, true) =>
                $"{name} is long, so only its first part was read, out of {all}.",
            _ =>
                $"{name} is long, so only {parts} spread across it were read, out of {all}.",
        };
    }
}
