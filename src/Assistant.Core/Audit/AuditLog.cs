using Assistant.Core.Agent;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Audit;

/// <summary>
/// The activity log (PROJECT_SPEC §3.5, §4.8, §4.9, step 117): what the Assistant did on the user's behalf, kept so that it can be seen, stopped and looked back
/// on. The runs of the agent report each tool call as it goes (<see cref="IAgentTaskLog"/>), and the code that looks for, offers, installs, updates and removes
/// integrations reports those (<see cref="IAuditTrail"/>); the panel in the conversation follows a run live, and the activity page lists everything
/// (<see cref="IAuditHistory"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it holds.</b> A step is a tool's name, how risky it is, when it began and ended, how it ended, what the user answered when asked, a code that says why
/// it did not work, and fixed words about what it was (<see cref="AuditText"/>). Never an argument, a result, a message, a prompt or an answer, and never the words
/// a tool or a server wrote about its own failure: there is nowhere to put them. A run is recorded only from its first tool call, so a conversation that is only
/// talk leaves nothing here.
/// </para>
/// <para>
/// <b>Where it lives.</b> The most recent runs and actions are held in memory, which is what the panel and the page read for what is going on. They are also kept
/// in the app's database through the <see cref="IAuditStore"/>, away from the run (a queue writes them one after another and a newer state of the same run replaces
/// an older one that was not written yet), and only while the user keeps history on in Settings. A log that cannot be written never fails or slows what it records.
/// </para>
/// <para>Logs say kinds, outcomes and counts only (PROJECT_SPEC §3.3).</para>
/// </remarks>
public sealed partial class AuditLog : IAgentTaskLog, IAuditTrail, IAuditHistory
{
    /// <summary>The most runs held in memory; the oldest finished one goes first.</summary>
    public const int MemoryTasks = 64;

    /// <summary>The most actions held in memory; the oldest finished one goes first.</summary>
    public const int MemoryActions = 128;

    private readonly TimeProvider _clock;
    private readonly ILogger<AuditLog> _logger;
    private readonly IAuditStore? _store;
    private readonly ISettingsService? _settings;
    private readonly object _gate = new();
    private readonly List<TaskScope> _tasks = [];
    private readonly List<ActionScope> _actions = [];
    private readonly Dictionary<Guid, Func<CancellationToken, Task>> _pending = [];
    private Task _writer = Task.CompletedTask;
    private bool _writing;

    /// <summary>Creates the log.</summary>
    /// <param name="clock">The time.</param>
    /// <param name="logger">Where it says how things went.</param>
    /// <param name="store">Where the log is kept; without one the log lives in memory only.</param>
    /// <param name="settings">Says whether the user keeps history; without it, everything is kept.</param>
    public AuditLog(TimeProvider clock, ILogger<AuditLog> logger, IAuditStore? store = null, ISettingsService? settings = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _logger = logger;
        _store = store;
        _settings = settings;
    }

    /// <inheritdoc/>
    public event EventHandler<AgentTaskStartedEventArgs>? TaskStarted;

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IAgentTaskScope BeginTask(Guid conversationId) => new TaskScope(this, conversationId);

    /// <inheritdoc/>
    public IAuditAction Begin(AuditKind kind, string name, string summary, RiskLevel? risk, Guid? conversationId = null, ConfirmationDecision? confirmation = null)
    {
        var scope = new ActionScope(this, kind, name, summary, risk, conversationId, confirmation);
        scope.Register();
        return scope;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ActivityItem>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var items = new Dictionary<Guid, ActivityItem>();
        if (_store is not null)
        {
            try
            {
                // What is waiting to be written is written first, so that what the store lists is up to date.
                await FlushAsync().ConfigureAwait(false);
                foreach (var item in await _store.ListAsync(limit, cancellationToken).ConfigureAwait(false))
                {
                    items[item.Id] = item;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogReadFailed(_logger, exception.GetType().Name);
            }
        }

        // What is held in memory is newer than what was kept, for the runs and actions it holds.
        lock (_gate)
        {
            foreach (var task in _tasks)
            {
                items[task.Id] = ActivityItem.Of(task.Snapshot);
            }

            foreach (var action in _actions)
            {
                items[action.Id] = ActivityItem.Of(action.Entry);
            }
        }

        return [.. items.Values.OrderByDescending(item => item.StartedAt).ThenByDescending(item => item.Id).Take(limit)];
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync().ConfigureAwait(false);
        if (_store is not null)
        {
            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _tasks.RemoveAll(task => task.Snapshot.Status.IsFinished());
            _actions.RemoveAll(action => action.Entry.Status.IsFinished());
        }

        LogCleared(_logger);
        RaiseChanged();
    }

    /// <summary>
    /// Deletes the runs and actions that ended more than <paramref name="maxAge"/> ago, from the store and from memory: "delete all logs after 48 hours"
    /// (Settings > Activity). What is going on is never touched. It is the user's own choice, made once; it does not undo anything that was done.
    /// </summary>
    public async Task PruneAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        await FlushAsync().ConfigureAwait(false);
        var before = _clock.GetUtcNow() - maxAge;
        if (_store is not null)
        {
            await _store.PruneAsync(maxAge, cancellationToken).ConfigureAwait(false);
        }

        int removed;
        lock (_gate)
        {
            removed = _tasks.RemoveAll(task => task.Snapshot.Status.IsFinished() && (task.Snapshot.EndedAt ?? task.Snapshot.StartedAt) < before)
                + _actions.RemoveAll(action => action.Entry.Status.IsFinished() && (action.Entry.EndedAt ?? action.Entry.StartedAt) < before);
        }

        if (removed > 0)
        {
            RaiseChanged();
        }
    }

    /// <inheritdoc/>
    public bool CancelTask(Guid taskId)
    {
        TaskScope? scope;
        lock (_gate)
        {
            scope = _tasks.FirstOrDefault(task => task.Id == taskId);
        }

        return scope?.Cancel() ?? false;
    }

    /// <summary>Completes when everything waiting to be written has been written, or has failed. For shutdown and for tests.</summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            Task writer;
            lock (_gate)
            {
                if (!_writing && _pending.Count == 0)
                {
                    return;
                }

                writer = _writer;
            }

            await writer.ConfigureAwait(false);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whoever listens is not the run's concern.
        }
    }

    // Keeps the latest state of a run or an action, written in its turn. A state that is waiting when a newer one arrives is replaced.
    private void Enqueue(Guid key, Func<IAuditStore, CancellationToken, Task> save)
    {
        if (_store is null)
        {
            return;
        }

        lock (_gate)
        {
            _pending[key] = token => SaveIfKeptAsync(save, token);
            if (!_writing)
            {
                _writing = true;
                _writer = Task.Run(DrainAsync);
            }
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Func<CancellationToken, Task> next;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _writing = false;
                    return;
                }

                var key = _pending.Keys.First();
                next = _pending[key];
                _pending.Remove(key);
            }

            try
            {
                await next(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogSaveFailed(_logger, exception.GetType().Name);
            }
        }
    }

    // Nothing is written while the user has history off (Settings, Privacy): the log then lives in memory for the session.
    private async Task SaveIfKeptAsync(Func<IAuditStore, CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        if (_settings is not null)
        {
            var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.Privacy.HistoryEnabled)
            {
                return;
            }
        }

        await save(_store!, cancellationToken).ConfigureAwait(false);
    }

    private void RaiseTaskStarted(TaskScope scope)
    {
        try
        {
            TaskStarted?.Invoke(this, new AgentTaskStartedEventArgs(scope, scope.ConversationId));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whoever shows the run is not the run's concern.
        }
    }

    // Drops the oldest that have ended, once there are more than memory is meant to hold. Called with the gate held.
    private void Trim()
    {
        while (_tasks.Count > MemoryTasks && _tasks.FindIndex(task => task.Snapshot.Status.IsFinished()) is var index and >= 0)
        {
            _tasks.RemoveAt(index);
        }

        while (_actions.Count > MemoryActions && _actions.FindIndex(action => action.Entry.Status.IsFinished()) is var index and >= 0)
        {
            _actions.RemoveAt(index);
        }
    }

    [LoggerMessage(EventId = 2810, Level = LogLevel.Information, Message = "Agent task recorded: {Status}, {Steps} steps, {Problems} that did not work")]
    private static partial void LogTaskEnded(ILogger logger, AgentTaskStatus status, int steps, int problems);

    [LoggerMessage(EventId = 2811, Level = LogLevel.Warning, Message = "The activity log could not be saved: {ExceptionType}")]
    private static partial void LogSaveFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2812, Level = LogLevel.Warning, Message = "The activity log could not be read: {ExceptionType}")]
    private static partial void LogReadFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2813, Level = LogLevel.Information, Message = "The activity log was cleared by the user")]
    private static partial void LogCleared(ILogger logger);

    // One run, from its first tool call to its end.
    private sealed class TaskScope : IAgentTaskScope
    {
        private readonly AuditLog _log;
        private readonly CancellationTokenSource _cancel = new();
        private readonly List<StepScope> _steps = [];
        private AgentTaskSnapshot _snapshot;
        private AgentTaskStatus _status = AgentTaskStatus.Running;
        private DateTimeOffset? _endedAt;
        private string? _failurePoint;
        private bool _cancelRequested;
        private bool _registered;
        private bool _ended;
        private bool _disposed;

        public TaskScope(AuditLog log, Guid conversationId)
        {
            _log = log;
            Id = Guid.NewGuid();
            ConversationId = conversationId;
            StartedAt = log._clock.GetUtcNow();
            CancelRequested = _cancel.Token;
            _snapshot = Build();
        }

        public Guid Id { get; }

        public Guid ConversationId { get; }

        public DateTimeOffset StartedAt { get; }

        public CancellationToken CancelRequested { get; }

        public object Gate => _log._gate;

        public DateTimeOffset Now() => _log._clock.GetUtcNow();

        public AgentTaskSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public event EventHandler? Changed;

        public IAgentStepScope BeginStep(string? toolName, RiskLevel? risk) => Add(toolName, risk, AuditStatus.Running, null);

        public void RecordSkipped(string? toolName, RiskLevel? risk, string? errorCode) => Add(toolName, risk, AuditStatus.Skipped, errorCode);

        public bool Cancel()
        {
            lock (_log._gate)
            {
                if (_ended)
                {
                    return false;
                }

                _cancelRequested = true;
            }

            Publish(save: false);
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run ended just as it was stopped: nothing is left to stop.
            }

            return true;
        }

        public void End(AgentOutcome outcome, AgentStopReason reason)
        {
            bool recorded;
            lock (_log._gate)
            {
                if (_ended)
                {
                    return;
                }

                _ended = true;
                recorded = _registered;
                if (recorded)
                {
                    var now = _log._clock.GetUtcNow();

                    // What is still going on ends with the run: stopped when it was stopped, failed when it failed.
                    foreach (var step in _steps)
                    {
                        step.Close(
                            outcome == AgentOutcome.Failed ? AuditStatus.Failed : AuditStatus.Cancelled,
                            outcome == AgentOutcome.Failed ? ToolErrors.Failed : null,
                            now);
                    }

                    _status = AgentTaskRules.StatusOf(outcome, reason);
                    _endedAt = now;
                    _failurePoint = AgentTaskRules.FailurePoint(_status, reason, _steps.Select(step => step.Entry).ToList());
                }
            }

            if (recorded)
            {
                var snapshot = Publish(save: true);
                LogTaskEnded(_log._logger, snapshot.Status, snapshot.StepCount, snapshot.Steps.Count(step => step.Status.IsProblem()));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            End(AgentOutcome.Abandoned, AgentStopReason.Answered);
            _cancel.Dispose();
        }

        // Adds a step: the run is recorded and announced when it has its first.
        private StepScope Add(string? toolName, RiskLevel? risk, AuditStatus status, string? errorCode)
        {
            StepScope step;
            var first = false;
            var accepted = false;
            lock (_log._gate)
            {
                var now = _log._clock.GetUtcNow();

                // A run that has ended takes no more steps; the step is given back, apart from the run, so that the caller need not care.
                step = new StepScope(this, attached: !_ended, entry: new AuditEntry
                {
                    Id = Guid.NewGuid(),
                    TaskId = Id,
                    ConversationId = ConversationId == Guid.Empty ? null : ConversationId,
                    Sequence = _steps.Count + 1,
                    Kind = AuditKind.ToolCall,
                    Name = AuditText.ToolName(toolName),
                    Risk = risk,
                    Status = status,
                    Summary = AuditText.ToolPhrase(toolName),
                    ErrorCode = status == AuditStatus.Skipped ? AuditText.Code(errorCode) : null,
                    StartedAt = now,
                    EndedAt = status == AuditStatus.Skipped ? now : null,
                });

                if (!_ended)
                {
                    accepted = true;
                    _steps.Add(step);
                    if (!_registered)
                    {
                        _registered = true;
                        first = true;
                        _log._tasks.Add(this);
                        _log.Trim();
                    }
                }
            }

            if (!accepted)
            {
                return step;
            }

            Publish(save: true, announce: first);
            return step;
        }

        // Makes the new snapshot, tells whoever follows the run, and has it kept.
        public AgentTaskSnapshot Publish(bool save, bool announce = false)
        {
            AgentTaskSnapshot snapshot;
            lock (_log._gate)
            {
                snapshot = Build();
                Volatile.Write(ref _snapshot, snapshot);
            }

            if (announce)
            {
                _log.RaiseTaskStarted(this);
            }

            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Whoever shows the run is not the run's concern.
            }

            if (save && _registered)
            {
                _log.Enqueue(Id, (store, token) => store.SaveTaskAsync(snapshot, token));
            }

            _log.RaiseChanged();
            return snapshot;
        }

        // Built with the gate held (or before anything else can see the run).
        private AgentTaskSnapshot Build() => new()
        {
            Id = Id,
            ConversationId = ConversationId,
            StartedAt = StartedAt,
            EndedAt = _endedAt,
            Status = _status,
            Steps = [.. _steps.Select(step => step.Entry)],
            FailurePoint = _failurePoint,
            CancelRequested = _cancelRequested,
        };
    }

    // One tool call of a run.
    private sealed class StepScope(TaskScope task, AuditEntry entry, bool attached = true) : IAgentStepScope
    {
        public AuditEntry Entry { get; private set; } = entry;

        public int Sequence => Entry.Sequence;

        public void Asking() => Change(current => current.Status == AuditStatus.Running ? current with { Status = AuditStatus.WaitingForYou } : current);

        public void Answered(ConfirmationDecision decision) => Change(current => current with
        {
            Confirmation = decision,
            Status = current.Status == AuditStatus.WaitingForYou ? AuditStatus.Running : current.Status,
        });

        public void End(AuditStatus status, string? errorCode = null) =>
            Change(
                current => current.Status.IsFinished()
                    ? current
                    : current with
                    {
                        Status = status,
                        ErrorCode = status == AuditStatus.Succeeded ? null : AuditText.Code(errorCode),
                        EndedAt = task.Now(),
                    },
                save: true);

        // Used when the run ends: ends the step if it is still going on. Called with the gate held.
        public void Close(AuditStatus status, string? errorCode, DateTimeOffset now)
        {
            if (!Entry.Status.IsFinished())
            {
                Entry = Entry with { Status = status, ErrorCode = AuditText.Code(errorCode), EndedAt = now };
            }
        }

        private void Change(Func<AuditEntry, AuditEntry> change, bool save = false)
        {
            lock (task.Gate)
            {
                Entry = change(Entry);
            }

            if (attached)
            {
                task.Publish(save);
            }
        }
    }

    // One action that is not a step of a run.
    private sealed class ActionScope(AuditLog log, AuditKind kind, string name, string summary, RiskLevel? risk, Guid? conversationId, ConfirmationDecision? confirmation)
        : IAuditAction
    {
        public Guid Id { get; } = Guid.NewGuid();

        public AuditEntry Entry { get; private set; } = new()
        {
            Id = Guid.Empty,
            Kind = kind,
            Name = AuditText.Label(name),
            Risk = risk,
            Status = AuditStatus.Running,
            Summary = AuditText.Sentence(summary),
            Confirmation = confirmation,
            ConversationId = conversationId == Guid.Empty ? null : conversationId,
            StartedAt = log._clock.GetUtcNow(),
        };

        public void Register()
        {
            lock (log._gate)
            {
                Entry = Entry with { Id = Id };
                log._actions.Add(this);
                log.Trim();
            }

            Publish();
        }

        public void End(AuditStatus status, string? errorCode = null, string? summary = null)
        {
            lock (log._gate)
            {
                if (Entry.Status.IsFinished())
                {
                    return;
                }

                Entry = Entry with
                {
                    Status = status,
                    ErrorCode = status == AuditStatus.Succeeded ? null : AuditText.Code(errorCode),
                    Summary = summary is null ? Entry.Summary : AuditText.Sentence(summary),
                    EndedAt = log._clock.GetUtcNow(),
                };
            }

            Publish();
        }

        public void Dispose() => End(AuditStatus.Cancelled);

        private void Publish()
        {
            AuditEntry entry;
            lock (log._gate)
            {
                entry = Entry;
            }

            log.Enqueue(Id, (store, token) => store.SaveActionAsync(entry, token));
            log.RaiseChanged();
        }
    }
}
