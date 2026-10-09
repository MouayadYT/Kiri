using System.Text;

namespace Assistant.Core.Confirmation;

/// <summary>One line of what the user is asked to approve: what it is (<see cref="Label"/>) and exactly what it is for this call (<see cref="Value"/>).</summary>
/// <param name="Label">Short name of the line: "To", "Message", "File", "Application".</param>
/// <param name="Value">What it is: a name, a path, the whole text of a message. May be several lines.</param>
public sealed record ConfirmationDetail(string Label, string Value);

/// <summary>
/// A line of a question that the user may rewrite before they say yes: the words of a message that is about to be sent. The tool that asks makes one,
/// names the line it stands for, and reads <see cref="Value"/> when its plan is run; the panel that asks sets it from what the user left in the field.
/// It holds the user's words, so nothing of it reaches <see cref="object.ToString"/>.
/// </summary>
/// <param name="label">The label of the line it stands for ("Message").</param>
/// <param name="maxLength">The longest the text may be.</param>
public sealed class ConfirmationEdit(string label, int maxLength)
{
    private string? _value;

    /// <summary>The label of the line that may be rewritten.</summary>
    public string Label { get; } = label;

    /// <summary>The longest the text may be.</summary>
    public int MaxLength { get; } = maxLength;

    /// <summary>What the user left in the field, or <see langword="null"/> when they changed nothing.</summary>
    public string? Value => Volatile.Read(ref _value);

    /// <summary>Keeps what the user left in the field, cut to <see cref="MaxLength"/>.</summary>
    public void Set(string? text)
    {
        var value = text ?? string.Empty;
        Volatile.Write(ref _value, value.Length > MaxLength ? value[..MaxLength] : value);
    }

    /// <inheritdoc/>
    public override string ToString() => nameof(ConfirmationEdit);
}

/// <summary>
/// Everything the user is shown when the Assistant asks "may I do this?" (PROJECT_SPEC §4.8, step 115): what the call would do, to what exactly, and
/// the button that says yes. It is made by the code of the tool that would run (which resolves the target: the person a name stands for, the file an
/// id stands for) or, for a tool that does not say, from the call's own arguments, every one of them, as they were given; never from what the model
/// said it would do. The text is made safe to show (<see cref="ConfirmationText"/>) and bounded, so a question is always one the user can read in full.
/// It holds what the user's content is (the words of a message, a path), so none of it reaches <see cref="object.ToString"/>, and so never a log.
/// </summary>
public sealed record ToolConfirmation
{
    /// <summary>The most lines a question may have.</summary>
    public const int MaxDetails = 24;

    /// <summary>The longest a line's label may be.</summary>
    public const int MaxLabelLength = 48;

    /// <summary>The longest one line's value may be; a message may be 2,000 characters, and an argument 4,000.</summary>
    public const int MaxValueLength = 4_000;

    /// <summary>The most characters all the lines of a question may have together: what is asked is always something a person can read.</summary>
    public const int MaxTotalLength = 12_000;

    /// <summary>The longest the question's title may be.</summary>
    public const int MaxTitleLength = 160;

    /// <summary>The longest the warning under the lines may be.</summary>
    public const int MaxWarningLength = 240;

    /// <summary>The longest the label of the button that says yes may be.</summary>
    public const int MaxButtonLength = 24;

    /// <summary>Makes the question; its text is made safe to show.</summary>
    /// <param name="kind">What kind of thing it would do.</param>
    /// <param name="title">The question, such as "Send this message to Omar?".</param>
    /// <param name="details">The lines: what it is done to and with, exactly. At least one.</param>
    /// <param name="approveLabel">What the button that says yes says: "Send", "Open", "Allow".</param>
    /// <param name="warning">What the user should know before they say yes, such as that it cannot be taken back; or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">There is no title or no line, or the question is larger than a person can be asked to read.</exception>
    public ToolConfirmation(
        ConfirmationKind kind, string title, IEnumerable<ConfirmationDetail> details, string approveLabel = "Allow", string? warning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(details);
        Kind = kind;
        Title = Bounded(ConfirmationText.Visible(title, multiline: false).Trim(), MaxTitleLength, nameof(title));
        ApproveLabel = Bounded(
            ConfirmationText.Visible(string.IsNullOrWhiteSpace(approveLabel) ? "Allow" : approveLabel, multiline: false).Trim(), MaxButtonLength, nameof(approveLabel));
        Warning = string.IsNullOrWhiteSpace(warning) ? null : Bounded(ConfirmationText.Visible(warning, multiline: false).Trim(), MaxWarningLength, nameof(warning));

        var lines = new List<ConfirmationDetail>();
        var total = 0;
        foreach (var detail in details)
        {
            ArgumentNullException.ThrowIfNull(detail);
            var label = Bounded(ConfirmationText.Visible(detail.Label, multiline: false).Trim(), MaxLabelLength, nameof(details));
            var value = Bounded(ConfirmationText.Visible(detail.Value, multiline: true), MaxValueLength, nameof(details));
            total += label.Length + value.Length;
            lines.Add(new ConfirmationDetail(label, value));
        }

        if (lines.Count == 0)
        {
            throw new ArgumentException("A question has to show what is to be done.", nameof(details));
        }

        if (lines.Count > MaxDetails || total > MaxTotalLength)
        {
            throw new ArgumentException("There is more in the call than a person can be asked to read.", nameof(details));
        }

        Details = lines;
    }

    /// <summary>What kind of thing it would do.</summary>
    public ConfirmationKind Kind { get; }

    /// <summary>The question.</summary>
    public string Title { get; }

    /// <summary>What it is done to and with, exactly, one line after another.</summary>
    public IReadOnlyList<ConfirmationDetail> Details { get; }

    /// <summary>What the button that says yes says.</summary>
    public string ApproveLabel { get; }

    /// <summary>What the user should know before they say yes, or <see langword="null"/>.</summary>
    public string? Warning { get; }

    /// <summary>
    /// The one line of the question the user may rewrite before they say yes (the words of a message), or <see langword="null"/> when nothing can be
    /// changed. The tool that asks makes it and reads it when it is run: what is done is then what the user left there.
    /// </summary>
    public ConfirmationEdit? Edit { get; init; }

    /// <summary>What the button that says no says, when it is not the usual "Don't allow"; <see langword="null"/> for the usual.</summary>
    public string? DeclineLabel { get; init; }

    /// <summary>
    /// What the question says once the user said yes, when "Allowed." is not it: a question that is a choice and not a permission ("is this the one
    /// you prefer?") says what the yes did. <see langword="null"/> for the usual. The Assistant's own words, never the call's.
    /// </summary>
    public string? ApprovedResult { get; init; }

    /// <summary>What it says once the user said no, when "Not allowed. Nothing was done." is not it; <see langword="null"/> for the usual.</summary>
    public string? DeclinedResult { get; init; }

    /// <summary>Everything the question says as one text, for assistive technology.</summary>
    public string AsText() =>
        string.Join(' ', new[] { Title }.Concat(Details.Select(detail => $"{detail.Label}: {detail.Value}")).Concat(Warning is null ? [] : [Warning]));

    // Keeps the lines (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}");
        return true;
    }

    private static string Bounded(string text, int limit, string parameter) =>
        text.Length <= limit ? text : throw new ArgumentException($"A part of the question is longer than {limit} characters.", parameter);
}
