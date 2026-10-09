using Assistant.Core.Agent;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>
/// The <c>agent_tasks</c> and <c>task_audit_records</c> tables: the activity log of what the Assistant did (PROJECT_SPEC §3.5, §5.9, step 117).
/// </summary>
/// <remarks>
/// Each method works on the connection it is given, so it takes part in that connection's transaction. What is written is what <see cref="AuditEntry"/> can
/// hold, which is no argument, no result and no message of any tool; every value travels as a parameter and nothing here logs. A row whose enum name this build
/// does not know was written by a newer one and is left out when reading, not failed on.
/// </remarks>
public interface IAuditRepository
{
    /// <summary>Keeps the run and each of its steps, replacing what was kept of them.</summary>
    void SaveTask(SqliteConnection connection, AgentTaskSnapshot task);

    /// <summary>Keeps one action that is not a step of a run, replacing what was kept of it.</summary>
    void SaveAction(SqliteConnection connection, AuditEntry action);

    /// <summary>The runs (with their steps) and the actions, newest first, at most <paramref name="limit"/> of them.</summary>
    IReadOnlyList<ActivityItem> List(SqliteConnection connection, int limit);

    /// <summary>Deletes every run, step and action.</summary>
    void Clear(SqliteConnection connection);

    /// <summary>
    /// Marks what was going on when the app last closed as interrupted: nothing is going on in a database nobody has open. Returns how many runs and actions it
    /// changed.
    /// </summary>
    int MarkInterrupted(SqliteConnection connection);

    /// <summary>Deletes what is older than <paramref name="cutoff"/>, and the oldest of what is more than the limits allow.</summary>
    void Prune(SqliteConnection connection, DateTimeOffset cutoff, int maxTasks, int maxActions);
}

/// <inheritdoc/>
public sealed class AuditRepository : IAuditRepository
{
    private const string EntryColumns =
        "id, conversation_id, task_id, kind, step_number, tool_name, risk_level, outcome, summary, confirmation, error_code, started_at, completed_at";

    /// <inheritdoc/>
    public void SaveTask(SqliteConnection connection, AgentTaskSnapshot task)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(task);
        using var transaction = connection.BeginTransaction(deferred: false);
        connection.Execute(
            "INSERT INTO agent_tasks (id, conversation_id, status, failure_point, started_at, completed_at) " +
            "VALUES ($id, (SELECT id FROM conversations WHERE id = $conversation), $status, $failure, $started, $completed) " +
            "ON CONFLICT (id) DO UPDATE SET conversation_id = excluded.conversation_id, status = excluded.status, " +
            "failure_point = excluded.failure_point, completed_at = excluded.completed_at",
            ("$id", DatabaseIds.ToText(task.Id)),
            ("$conversation", task.ConversationId == Guid.Empty ? null : DatabaseIds.ToText(task.ConversationId)),
            ("$status", task.Status.ToString()),
            ("$failure", task.FailurePoint),
            ("$started", DatabaseTimestamps.ToText(task.StartedAt)),
            ("$completed", task.EndedAt is { } ended ? DatabaseTimestamps.ToText(ended) : null));

        foreach (var step in task.Steps)
        {
            Upsert(connection, step with { TaskId = task.Id });
        }

        transaction.Commit();
    }

    /// <inheritdoc/>
    public void SaveAction(SqliteConnection connection, AuditEntry action)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(action);
        Upsert(connection, action with { TaskId = null, Sequence = 0 });
    }

    /// <inheritdoc/>
    public IReadOnlyList<ActivityItem> List(SqliteConnection connection, int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var items = new List<ActivityItem>();

        var tasks = connection.Query(
            "SELECT id, conversation_id, status, failure_point, started_at, completed_at FROM agent_tasks ORDER BY started_at DESC, id DESC LIMIT $limit",
            ReadTask,
            ("$limit", limit))
            .OfType<AgentTaskSnapshot>()
            .ToList();
        if (tasks.Count > 0)
        {
            var steps = connection.Query(
                $"SELECT {EntryColumns} FROM task_audit_records WHERE task_id IN (SELECT value FROM json_each($ids)) ORDER BY task_id, step_number, started_at",
                ReadEntry,
                ("$ids", DatabaseIds.ToJsonArray(tasks.Select(task => task.Id))))
                .OfType<AuditEntry>()
                .GroupBy(step => step.TaskId!.Value)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<AuditEntry>)[.. group]);
            foreach (var task in tasks)
            {
                var own = steps.GetValueOrDefault(task.Id) ?? [];
                items.Add(ActivityItem.Of(task with
                {
                    Steps = own,

                    // What was going on when the app closed is told where it stopped, from the steps it did.
                    FailurePoint = task.FailurePoint ?? (task.Status == AgentTaskStatus.Interrupted
                        ? AgentTaskRules.FailurePoint(task.Status, AgentStopReason.Answered, own)
                        : null),
                }));
            }
        }

        items.AddRange(
            connection.Query(
                $"SELECT {EntryColumns} FROM task_audit_records WHERE task_id IS NULL ORDER BY started_at DESC, id DESC LIMIT $limit",
                ReadEntry,
                ("$limit", limit))
                .OfType<AuditEntry>()
                .Select(ActivityItem.Of));

        return [.. items.OrderByDescending(item => item.StartedAt).ThenByDescending(item => item.Id).Take(limit)];
    }

    /// <inheritdoc/>
    public void Clear(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        connection.Execute("DELETE FROM task_audit_records");
        connection.Execute("DELETE FROM agent_tasks");
        transaction.Commit();
    }

    /// <inheritdoc/>
    public int MarkInterrupted(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var changed = connection.Execute(
            "UPDATE task_audit_records SET outcome = $interrupted WHERE outcome IN ($running, $waiting)",
            ("$interrupted", nameof(AuditStatus.Interrupted)),
            ("$running", nameof(AuditStatus.Running)),
            ("$waiting", nameof(AuditStatus.WaitingForYou)));
        changed += connection.Execute(
            "UPDATE agent_tasks SET status = $interrupted WHERE status = $running",
            ("$interrupted", nameof(AgentTaskStatus.Interrupted)),
            ("$running", nameof(AgentTaskStatus.Running)));
        transaction.Commit();
        return changed;
    }

    /// <inheritdoc/>
    public void Prune(SqliteConnection connection, DateTimeOffset cutoff, int maxTasks, int maxActions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTasks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActions);
        using var transaction = connection.BeginTransaction(deferred: false);
        var before = ("$cutoff", DatabaseTimestamps.ToText(cutoff));

        // A run's steps go with it (the table cascades).
        connection.Execute("DELETE FROM agent_tasks WHERE started_at < $cutoff", before);
        connection.Execute(
            "DELETE FROM agent_tasks WHERE id NOT IN (SELECT id FROM agent_tasks ORDER BY started_at DESC, id DESC LIMIT $max)", ("$max", maxTasks));
        connection.Execute("DELETE FROM task_audit_records WHERE task_id IS NULL AND started_at < $cutoff", before);
        connection.Execute(
            "DELETE FROM task_audit_records WHERE task_id IS NULL AND id NOT IN " +
            "(SELECT id FROM task_audit_records WHERE task_id IS NULL ORDER BY started_at DESC, id DESC LIMIT $max)",
            ("$max", maxActions));
        transaction.Commit();
    }

    private static void Upsert(SqliteConnection connection, AuditEntry entry) =>
        connection.Execute(
            "INSERT INTO task_audit_records (id, conversation_id, task_id, kind, step_number, tool_name, risk_level, outcome, summary, confirmation, error_code, " +
            "started_at, completed_at) " +
            "VALUES ($id, (SELECT id FROM conversations WHERE id = $conversation), $task, $kind, $step, $name, $risk, $outcome, $summary, $confirmation, $error, " +
            "$started, $completed) " +
            "ON CONFLICT (id) DO UPDATE SET conversation_id = excluded.conversation_id, outcome = excluded.outcome, summary = excluded.summary, " +
            "confirmation = excluded.confirmation, error_code = excluded.error_code, completed_at = excluded.completed_at",
            ("$id", DatabaseIds.ToText(entry.Id)),
            ("$conversation", entry.ConversationId is { } conversation ? DatabaseIds.ToText(conversation) : null),
            ("$task", entry.TaskId is { } task ? DatabaseIds.ToText(task) : null),
            ("$kind", entry.Kind.ToString()),
            ("$step", entry.Sequence),
            ("$name", entry.Name),
            ("$risk", entry.Risk?.ToString() ?? "Unknown"),
            ("$outcome", entry.Status.ToString()),
            ("$summary", entry.Summary),
            ("$confirmation", entry.Confirmation?.ToString()),
            ("$error", entry.ErrorCode),
            ("$started", DatabaseTimestamps.ToText(entry.StartedAt)),
            ("$completed", entry.EndedAt is { } ended ? DatabaseTimestamps.ToText(ended) : null));

    // A link to another row: its id, or nothing when there is none or it is not one the app wrote.
    private static Guid? OptionalId(string? text) => Guid.TryParse(text, out var id) ? id : null;

    // A run, or null for one whose status this build does not know or whose id is not one the app wrote.
    private static AgentTaskSnapshot? ReadTask(SqliteDataReader reader)
    {
        if (!reader.TryGetEnum(2, out AgentTaskStatus status) || !Guid.TryParse(reader.GetString(0), out var id))
        {
            return null;
        }

        return new AgentTaskSnapshot
        {
            Id = id,
            ConversationId = OptionalId(reader.GetStringOrNull(1)) ?? Guid.Empty,
            Status = status,
            FailurePoint = reader.GetStringOrNull(3),
            StartedAt = reader.GetTimestamp(4),
            EndedAt = reader.GetStringOrNull(5) is { } ended ? DatabaseTimestamps.FromText(ended) : null,
        };
    }

    // A step or an action, or null for one whose kind or status this build does not know or whose id is not one the app wrote.
    private static AuditEntry? ReadEntry(SqliteDataReader reader)
    {
        if (!reader.TryGetEnum(3, out AuditKind kind) || !reader.TryGetEnum(7, out AuditStatus status) || !Guid.TryParse(reader.GetString(0), out var id))
        {
            return null;
        }

        RiskLevel? risk = reader.TryGetEnum(6, out RiskLevel read) ? read : null;
        ConfirmationDecision? confirmation = !reader.IsDBNull(9) && reader.TryGetEnum(9, out ConfirmationDecision decision) ? decision : null;
        return new AuditEntry
        {
            Id = id,
            ConversationId = OptionalId(reader.GetStringOrNull(1)),
            TaskId = OptionalId(reader.GetStringOrNull(2)),
            Kind = kind,
            Sequence = reader.GetInt32(4),
            Name = reader.GetString(5),
            Risk = risk,
            Status = status,
            Summary = reader.GetString(8),
            Confirmation = confirmation,
            ErrorCode = reader.GetStringOrNull(10),
            StartedAt = reader.GetTimestamp(11),
            EndedAt = reader.GetStringOrNull(12) is { } ended ? DatabaseTimestamps.FromText(ended) : null,
        };
    }
}
