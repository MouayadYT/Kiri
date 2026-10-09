namespace Assistant.Core.Audit;

/// <summary>
/// A multi-step run of the agent as it stands at one moment (PROJECT_SPEC §4.8, step 117): when it began, the steps so far, how it stands or ended and, when
/// it could not go on, where. It is a value: the next moment is another one. It holds no private content, like the entries it is made of.
/// </summary>
public sealed record AgentTaskSnapshot
{
    /// <summary>An id for the run.</summary>
    public required Guid Id { get; init; }

    /// <summary>The conversation it answers in, or <see cref="Guid.Empty"/> when it is not known.</summary>
    public Guid ConversationId { get; init; }

    /// <summary>When it began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When it ended, or <see langword="null"/> while it goes on.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>How it stands, or how it ended.</summary>
    public AgentTaskStatus Status { get; init; }

    /// <summary>Every tool call of the run, in order, the ones that were not made included.</summary>
    public IReadOnlyList<AuditEntry> Steps { get; init; } = [];

    /// <summary>
    /// Where the run could not go on, in one short line of fixed words ("Step 3 (Open an application) didn't work: it took too long"); <see langword="null"/> when it
    /// went on to its end.
    /// </summary>
    public string? FailurePoint { get; init; }

    /// <summary>Whether the user has asked to stop it and it has not stopped yet.</summary>
    public bool CancelRequested { get; init; }

    /// <summary>Whether it goes on.</summary>
    public bool IsRunning => Status == AgentTaskStatus.Running;

    /// <summary>How many tools were called: the steps that were not skipped.</summary>
    public int StepCount => Steps.Count(step => step.Status != AuditStatus.Skipped);

    /// <summary>How long it took, or how long it has been going on at <paramref name="now"/>.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) => (EndedAt ?? now) - StartedAt;

    /// <summary>
    /// Whether it is worth putting in front of the user in the conversation: it has more than one step, or something went wrong in it. A run with a single
    /// step that went well is told in the answer and is in the activity log; it needs no panel.
    /// </summary>
    public bool IsWorthShowing =>
        StepCount >= 2
        || Steps.Any(step => step.Status.IsProblem())
        || Status is AgentTaskStatus.Incomplete or AgentTaskStatus.Cancelled or AgentTaskStatus.Failed or AgentTaskStatus.Interrupted;
}

/// <summary>One line of the activity log: a run of the agent with its steps, or an action of its own such as an integration that was installed.</summary>
/// <param name="Task">The run, or <see langword="null"/> for an action.</param>
/// <param name="Action">The action, or <see langword="null"/> for a run.</param>
public sealed record ActivityItem(AgentTaskSnapshot? Task, AuditEntry? Action)
{
    /// <summary>When it began.</summary>
    public DateTimeOffset StartedAt => Task?.StartedAt ?? Action?.StartedAt ?? DateTimeOffset.MinValue;

    /// <summary>The id of the run or the action.</summary>
    public Guid Id => Task?.Id ?? Action?.Id ?? Guid.Empty;

    /// <summary>A line for a run.</summary>
    public static ActivityItem Of(AgentTaskSnapshot task) => new(task, null);

    /// <summary>A line for an action.</summary>
    public static ActivityItem Of(AuditEntry action) => new(null, action);
}
