using System.Text;
using Assistant.Core.Confirmation;
using Assistant.Core.Domain;

namespace Assistant.Core.Audit;

/// <summary>
/// One thing the Assistant did, as the activity log keeps it (PROJECT_SPEC §3.5, §4.8, step 117): which tool or action, how risky it was, how it ended,
/// when, and what the user said when asked. It holds no private content: no argument, no result, no prompt and no answer, only fixed words
/// (<see cref="AuditText"/>) and names that were tidied before they were kept (<see cref="AuditText.Label"/>), so that it can be shown and kept without more
/// care than a count.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>An id for the entry.</summary>
    public required Guid Id { get; init; }

    /// <summary>The run it is a step of, or <see langword="null"/> for an action of its own (an integration that was installed).</summary>
    public Guid? TaskId { get; init; }

    /// <summary>The conversation it happened in, when it happened in one.</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>Its place among the steps of its run, from 1; 0 for an action of its own.</summary>
    public int Sequence { get; init; }

    /// <summary>What kind of thing it is.</summary>
    public required AuditKind Kind { get; init; }

    /// <summary>The tool's name, or what the action was done to (an app's name), tidied.</summary>
    public required string Name { get; init; }

    /// <summary>How much it can change; <see langword="null"/> when that is not known (a tool the Assistant has no definition of).</summary>
    public RiskLevel? Risk { get; init; }

    /// <summary>Where it stands, or how it ended.</summary>
    public required AuditStatus Status { get; init; }

    /// <summary>What it was, in fixed words with at most a tidied name in them: "Send a message", "Install Todoist 2.1.0".</summary>
    public required string Summary { get; init; }

    /// <summary>What the user answered when asked whether it may be done; <see langword="null"/> when they were not asked.</summary>
    public ConfirmationDecision? Confirmation { get; init; }

    /// <summary>For a step that did not work, a code that says why (<see cref="AuditText.Reason"/> turns it into words); otherwise <see langword="null"/>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>When it began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When it ended, or <see langword="null"/> while it goes on.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>How long it took, or <see langword="null"/> while it goes on.</summary>
    public TimeSpan? Duration => EndedAt is { } ended ? ended - StartedAt : null;

    // Keeps the name and the summary out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, Status = {Status}");
        return true;
    }
}
