using Assistant.Core.Agent;
using Assistant.Core.Confirmation;

namespace Assistant.Core.Audit;

/// <summary>
/// How a run of the agent is described once it has ended (PROJECT_SPEC §4.8, step 117): how it stands, and, when it could not go on, the one short line that says
/// where. Both come from what the run did and why it stopped, never from what anything said, so the line is made of fixed words.
/// </summary>
public static class AgentTaskRules
{
    /// <summary>How a run that ended the way <paramref name="outcome"/> and <paramref name="reason"/> say stands.</summary>
    public static AgentTaskStatus StatusOf(AgentOutcome outcome, AgentStopReason reason) => outcome switch
    {
        AgentOutcome.Failed => AgentTaskStatus.Failed,
        AgentOutcome.Stopped or AgentOutcome.Abandoned => AgentTaskStatus.Cancelled,
        _ => reason is AgentStopReason.RoundLimit or AgentStopReason.ToolCallLimit or AgentStopReason.TimeLimit or AgentStopReason.Looping
            ? AgentTaskStatus.Incomplete
            : AgentTaskStatus.Completed,
    };

    /// <summary>
    /// Where the run could not go on, in one line, or <see langword="null"/> when it went on to its end: the user stopped it, the model could not answer, a
    /// bound was reached, or the last thing it tried did not work. A step the user did not allow is their choice, not a failure; one that could not be asked
    /// about, or was not answered, is told, since nothing was done.
    /// </summary>
    /// <param name="status">How the run ended.</param>
    /// <param name="reason">Why its final answer came when it did.</param>
    /// <param name="steps">Its steps, in order.</param>
    public static string? FailurePoint(AgentTaskStatus status, AgentStopReason reason, IReadOnlyList<AuditEntry> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var made = steps.Where(step => step.Status != AuditStatus.Skipped).ToList();
        var last = made.Count > 0 ? made[^1] : null;

        switch (status)
        {
            case AgentTaskStatus.Cancelled:
                return last is { Status: AuditStatus.Running or AuditStatus.WaitingForYou or AuditStatus.Cancelled }
                    ? $"Stopped during {Label(last)}"
                    : last is null ? "Stopped before the first step" : $"Stopped after {Label(last)}";
            case AgentTaskStatus.Failed:
                return last is null ? "The model couldn't start" : $"The model couldn't go on after {Label(last)}";
            case AgentTaskStatus.Interrupted:
                return last is null ? "Interrupted when the app closed" : $"Interrupted during {Label(last)}";
            case AgentTaskStatus.Incomplete:
                var where = last is null ? string.Empty : $" after {Label(last)}";
                return reason switch
                {
                    AgentStopReason.TimeLimit => "Ran out of time" + where,
                    AgentStopReason.Looping => "Kept repeating itself" + where,
                    _ => "Used all the steps it is allowed" + where,
                };
        }

        // It went on to its answer, but the last thing it tried may not have worked.
        return last switch
        {
            { Status: AuditStatus.Failed or AuditStatus.TimedOut } =>
                $"{Capital(Label(last))} {(last.Status == AuditStatus.TimedOut ? "took too long" : "didn't work" + (last.ErrorCode is null ? string.Empty : ": " + AuditText.Reason(last.ErrorCode)))}",
            { Status: AuditStatus.Declined, Confirmation: ConfirmationDecision.NoAnswer or ConfirmationDecision.CouldNotAsk } =>
                $"{Capital(Label(last))} wasn't done: {(last.Confirmation == ConfirmationDecision.NoAnswer ? "there was no answer in time" : "you couldn't be asked")}",
            _ => null,
        };
    }

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    // "step 3 (Open an application)": where in the run, and what it was.
    private static string Label(AuditEntry step) => $"step {step.Sequence} ({step.Summary})";
}
