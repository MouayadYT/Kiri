using Assistant.Core.Agent;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;

namespace Assistant.Core.Audit;

/// <summary>A multi-step run of the agent, followed live: what the panel in the conversation shows, and what stops it (PROJECT_SPEC §4.8, step 117).</summary>
public interface IAgentTaskView
{
    /// <summary>The run as it stands now. Each moment is a new value; this property gives the latest.</summary>
    AgentTaskSnapshot Snapshot { get; }

    /// <summary>Raised, from whichever thread did it, when the run changes: a step began, waited, ended, or the run ended.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Asks the run to stop, as the Stop button of the conversation does: the work that is going on is told to stop, nothing more is started, and what was done
    /// stays done. Returns <see langword="false"/> when the run has ended already.
    /// </summary>
    bool Cancel();
}

/// <summary>One tool call of a run, which its runner tells how it goes (PROJECT_SPEC §4.8, step 117). It follows the question the user is asked, when there is one.</summary>
public interface IAgentStepScope : IConfirmationObserver
{
    /// <summary>The step's place in its run, from 1.</summary>
    int Sequence { get; }

    /// <summary>The step ended this way; a code says why when it did not work. Telling it twice changes nothing.</summary>
    void End(AuditStatus status, string? errorCode = null);
}

/// <summary>
/// One run of the agent, which its runner tells how it goes. The run is not recorded, shown or saved until its first tool is called: a run that only answered
/// in words is not a task.
/// </summary>
public interface IAgentTaskScope : IAgentTaskView, IDisposable
{
    /// <summary>Cancelled when the user asks to stop the run from the panel or the activity log; the runner stops as if its caller had cancelled.</summary>
    CancellationToken CancelRequested { get; }

    /// <summary>A tool is about to be run. The name is kept only if it is one a tool can have, and what the tool is called with is not given.</summary>
    IAgentStepScope BeginStep(string? toolName, RiskLevel? risk);

    /// <summary>A call the model made was not run (it could not be read, was a repeat, was not offered, or the run had no room), for the reason the code gives.</summary>
    void RecordSkipped(string? toolName, RiskLevel? risk, string? errorCode);

    /// <summary>The run ended; what is still going on in it ends with it. Telling it twice changes nothing.</summary>
    void End(AgentOutcome outcome, AgentStopReason reason);
}

/// <summary>Raised when a run makes its first tool call, so a surface that shows the conversation it answers in can follow it.</summary>
public sealed class AgentTaskStartedEventArgs(IAgentTaskView task, Guid conversationId) : EventArgs
{
    /// <summary>The run.</summary>
    public IAgentTaskView Task { get; } = task;

    /// <summary>The conversation the run answers in.</summary>
    public Guid ConversationId { get; } = conversationId;
}

/// <summary>Where the runs of the agent report themselves (PROJECT_SPEC §4.8, step 117).</summary>
public interface IAgentTaskLog
{
    /// <summary>Raised when a run makes its first tool call.</summary>
    event EventHandler<AgentTaskStartedEventArgs>? TaskStarted;

    /// <summary>Starts following a run in the conversation <paramref name="conversationId"/>. Nothing is recorded until it calls a tool.</summary>
    IAgentTaskScope BeginTask(Guid conversationId);
}

/// <summary>An action that is not a step of a run (an integration that was installed), which the code that does it tells how it ends.</summary>
public interface IAuditAction : IDisposable
{
    /// <summary>
    /// The action ended this way. <paramref name="errorCode"/> says why it did not work; <paramref name="summary"/> replaces what it was said to be when
    /// the end says more (a count of what was found). Telling it twice changes nothing; being disposed without being told is a stop.
    /// </summary>
    void End(AuditStatus status, string? errorCode = null, string? summary = null);
}

/// <summary>Where actions of the Assistant that are not tool calls are recorded: looking for, offering, installing, updating and removing integrations (PROJECT_SPEC §4.8, step 117).</summary>
public interface IAuditTrail
{
    /// <summary>
    /// Records that an action began. <paramref name="name"/> and <paramref name="summary"/> are tidied before they are kept and must hold nothing private; the
    /// user's answer, when the action waited for one, is <paramref name="confirmation"/>.
    /// </summary>
    IAuditAction Begin(
        AuditKind kind, string name, string summary, RiskLevel? risk, Guid? conversationId = null, ConfirmationDecision? confirmation = null);
}

/// <summary>What the activity log shows, and what the user can do with it (PROJECT_SPEC §3.5, §4.9, step 117).</summary>
public interface IAuditHistory
{
    /// <summary>Raised when something in the log changed.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// The runs and actions, newest first, at most <paramref name="limit"/>: what is kept on this PC and what is going on now. It never throws for a database
    /// that cannot be read: the runs of this session are listed.
    /// </summary>
    Task<IReadOnlyList<ActivityItem>> ListAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Deletes what the log kept and forgets what it held in memory, except what is going on. It is the user's own action.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the run <paramref name="taskId"/> to stop. Returns <see langword="false"/> when there is no such run going on.</summary>
    bool CancelTask(Guid taskId);
}

/// <summary>Where the activity log is kept (PROJECT_SPEC §3.5, step 117): the database of the app. Every call is its own business and never throws to the run it comes from.</summary>
public interface IAuditStore
{
    /// <summary>Keeps the run and all its steps as they are now, replacing what was kept of it.</summary>
    Task SaveTaskAsync(AgentTaskSnapshot task, CancellationToken cancellationToken = default);

    /// <summary>Keeps one action that is not a step of a run, replacing what was kept of it.</summary>
    Task SaveActionAsync(AuditEntry action, CancellationToken cancellationToken = default);

    /// <summary>The runs and actions kept, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ActivityItem>> ListAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Deletes everything kept.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes the runs and actions that ended more than <paramref name="maxAge"/> ago, whatever else is kept. A store that keeps nothing does nothing.</summary>
    Task PruneAsync(TimeSpan maxAge, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>How much of the activity log is kept (PROJECT_SPEC §3.5, step 117).</summary>
public static class AuditRetention
{
    /// <summary>The longest a run or action is kept.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    /// <summary>
    /// How long a run or action is kept when the user keeps conversations for <paramref name="retention"/>: as long as the conversations, when that is shorter than
    /// <see cref="MaxAge"/>, since a log that outlived what the user chose to keep would be a way around it.
    /// </summary>
    public static TimeSpan MaxAgeFor(HistoryRetention retention) =>
        retention == HistoryRetention.UntilDeleted ? MaxAge : TimeSpan.FromDays(Math.Min((int)retention, (int)MaxAge.TotalDays));

    /// <summary>The most runs kept; the oldest go first.</summary>
    public const int MaxTasks = 500;

    /// <summary>The most actions that are not steps of a run kept; the oldest go first.</summary>
    public const int MaxActions = 1000;

    /// <summary>The most runs and actions the activity log lists at once.</summary>
    public const int ListLimit = 200;
}
