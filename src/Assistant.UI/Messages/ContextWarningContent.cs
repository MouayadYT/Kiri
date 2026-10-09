namespace Assistant.UI.Messages;

/// <summary>
/// A warning in an answer that some context, or some of the conversation, did not fit what the model can read at once
/// and was left out or cut short, so the answer does not take all of it into account. It is drawn as a small warning
/// glyph and the words, in the text column, apart from the model's prose; it is not a card, and it is not part of what
/// is copied as the answer's text. The words name an item only by its label, never by its content.
/// </summary>
public sealed class ContextWarningContent : MessageContent
{
    /// <summary>Creates a warning that says <paramref name="lines"/>, one line for each thing that was left out or cut short.</summary>
    public ContextWarningContent(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Lines = [.. lines.Where(line => !string.IsNullOrWhiteSpace(line))];
    }

    /// <summary>What was left out or cut short, in words, a line for each.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The whole warning as one text, for assistive technology.</summary>
    public string Text => "Warning: " + string.Join(" ", Lines);
}
