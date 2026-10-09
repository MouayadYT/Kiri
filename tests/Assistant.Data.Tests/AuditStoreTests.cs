using System.Text;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>
/// The activity log over a real SQLite database (PROJECT_SPEC §3.5, §5.9, step 117): runs with their steps and actions of their own are kept and read back as they were,
/// replaced as they change, left out when a newer build wrote what this one does not know, tidied when the app restarts and when they are old, deleted for real when the user
/// clears them, and an upgrade from the schema before it keeps what was there.
/// </summary>
public sealed class AuditStoreTests : IDisposable
{
    private static readonly Guid Conversation = Guid.Parse(SqlHelpers.ConversationId);
    private readonly TestDatabase _database = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly SqliteAuditStore _store;

    public AuditStoreTests() => _store = CreateStore();

    public void Dispose() => _database.Dispose();

    private SqliteAuditStore CreateStore(IDatabaseInitializer? initializer = null, ISettingsService? settings = null) =>
        new(
            _database.Connections, initializer ?? _database.CreateInitializer(), new AuditRepository(), _database.Time, _logs.CreateFactory().CreateLogger<SqliteAuditStore>(),
            settings);

    private sealed class Retaining(HistoryRetention retention) : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings { Privacy = new PrivacySettings { HistoryRetention = retention }, Cleanup = new CleanupSettings { DeleteLogs = false } });

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Unreadable : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => throw new IOException("settings are locked");

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private AuditEntry Step(
        Guid task, int number, AuditStatus status = AuditStatus.Succeeded, string name = "open_application", string summary = "Open an application",
        RiskLevel? risk = RiskLevel.SideEffect, ConfirmationDecision? confirmation = null, string? code = null, Guid? conversation = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TaskId = task,
            ConversationId = conversation,
            Sequence = number,
            Kind = AuditKind.ToolCall,
            Name = name,
            Risk = risk,
            Status = status,
            Summary = summary,
            Confirmation = confirmation,
            ErrorCode = code,
            StartedAt = _database.Time.GetUtcNow(),
            EndedAt = status.IsFinished() ? _database.Time.GetUtcNow().AddSeconds(2) : null,
        };

    private AgentTaskSnapshot Run(
        AgentTaskStatus status = AgentTaskStatus.Completed, Guid? conversation = null, string? failure = null, DateTimeOffset? at = null, params AuditEntry[] steps)
    {
        var id = steps.Length > 0 && steps[0].TaskId is { } owned ? owned : Guid.NewGuid();
        return new AgentTaskSnapshot
        {
            Id = id,
            ConversationId = conversation ?? Guid.Empty,
            StartedAt = at ?? _database.Time.GetUtcNow(),
            EndedAt = status.IsFinished() ? (at ?? _database.Time.GetUtcNow()).AddSeconds(5) : null,
            Status = status,
            FailurePoint = failure,
            Steps = steps,
        };
    }

    private AuditEntry Action(AuditKind kind = AuditKind.InstallIntegration, AuditStatus status = AuditStatus.Succeeded, string name = "Todoist", DateTimeOffset? at = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = name,
            Risk = RiskLevel.SideEffect,
            Status = status,
            Summary = "Install Todoist 2.1.0",
            Confirmation = ConfirmationDecision.Approved,
            StartedAt = at ?? _database.Time.GetUtcNow(),
            EndedAt = status.IsFinished() ? (at ?? _database.Time.GetUtcNow()).AddSeconds(3) : null,
        };

    // ---- what is kept ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARunIsKeptWithItsStepsInOrder_AndReadBackAsItWas()
    {
        var id = Guid.NewGuid();
        var first = Step(id, 1, name: "search_files", summary: "Search your files", risk: RiskLevel.ReadOnly);
        var second = Step(id, 2, AuditStatus.Declined, confirmation: ConfirmationDecision.NoAnswer, code: "no_answer");
        var third = Step(id, 3, AuditStatus.Skipped, code: "repeated_call");
        var run = Run(AgentTaskStatus.Completed, null, "Step 2 (Open an application) wasn't done: there was no answer in time", null, first, second, third);

        await _store.SaveTaskAsync(run);

        var read = Assert.Single(await _store.ListAsync(10)).Task!;
        Assert.Equal(run.Id, read.Id);
        Assert.Equal((run.Status, run.StartedAt, run.EndedAt, run.FailurePoint), (read.Status, read.StartedAt, read.EndedAt, read.FailurePoint));
        Assert.Equal([1, 2, 3], read.Steps.Select(step => step.Sequence));
        Assert.Equal(run.Steps, read.Steps);
        Assert.Equal(ConfirmationDecision.NoAnswer, read.Steps[1].Confirmation);
        Assert.Equal(RiskLevel.ReadOnly, read.Steps[0].Risk);
        Assert.Equal("repeated_call", read.Steps[2].ErrorCode);
    }

    [Fact]
    public async Task SavingARunAgainReplacesWhatWasKeptOfIt_ItsStatusAndItsSteps()
    {
        var id = Guid.NewGuid();
        var running = Step(id, 1, AuditStatus.Running);
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Running, null, null, null, running));

        var done = running with { Status = AuditStatus.Succeeded, EndedAt = running.StartedAt.AddSeconds(1) };
        var second = Step(id, 2);
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, null, done, second) with { Id = id });

        var read = Assert.Single(await _store.ListAsync(10)).Task!;
        Assert.Equal(AgentTaskStatus.Completed, read.Status);
        Assert.Equal([AuditStatus.Succeeded, AuditStatus.Succeeded], read.Steps.Select(step => step.Status));
        using var connection = _database.Open();
        Assert.Equal(2, connection.Count("task_audit_records"));
        Assert.Equal(1, connection.Count("agent_tasks"));
    }

    [Fact]
    public async Task AnActionOfItsOwnIsKeptAndReadBack()
    {
        var action = Action();

        await _store.SaveActionAsync(action);

        var read = Assert.Single(await _store.ListAsync(10)).Action!;
        Assert.Equal(action, read);
        Assert.Null(read.TaskId);
    }

    [Fact]
    public async Task RunsAndActionsAreListedNewestFirst_AndLimited()
    {
        var now = _database.Time.GetUtcNow();
        var older = Action(at: now.AddMinutes(-3));
        var run = Run(AgentTaskStatus.Completed, null, null, now.AddMinutes(-2), Step(Guid.NewGuid(), 1));
        run = run with { Steps = [run.Steps[0] with { TaskId = run.Id }] };
        var newer = Action(AuditKind.RemoveIntegration, at: now.AddMinutes(-1));
        await _store.SaveActionAsync(older);
        await _store.SaveTaskAsync(run);
        await _store.SaveActionAsync(newer);

        var all = await _store.ListAsync(10);
        var two = await _store.ListAsync(2);

        Assert.Equal([newer.Id, run.Id, older.Id], all.Select(item => item.Id));
        Assert.Equal([newer.Id, run.Id], two.Select(item => item.Id));
    }

    [Fact]
    public async Task WhenThereAreMoreThanTheLimitTheNewestAreListed()
    {
        var now = _database.Time.GetUtcNow();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddMinutes(i), Step(id, 1)));
        }

        var two = await _store.ListAsync(2);

        Assert.Equal([ids[4], ids[3]], two.Select(item => item.Id));
    }

    [Fact]
    public async Task AToolTheAssistantHasNoDefinitionOfKeepsNoRisk()
    {
        var id = Guid.NewGuid();
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, null, Step(id, 1, risk: null)));

        Assert.Null(Assert.Single(Assert.Single(await _store.ListAsync(10)).Task!.Steps).Risk);
    }

    // ---- what a row may leave unknown ------------------------------------------------------------------------------------

    [Fact]
    public async Task ARowAnewerBuildWroteWithAKindOrStatusThisOneDoesNotKnowIsLeftOut_NotFailedOn()
    {
        var id = Guid.NewGuid();
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, null, Step(id, 1), Step(id, 2)));
        await _store.SaveActionAsync(Action());
        using (var connection = _database.Open())
        {
            connection.Execute("UPDATE task_audit_records SET kind = 'Teleport' WHERE step_number = 2");
            connection.Execute(
                "INSERT INTO agent_tasks (id, status, started_at) VALUES ($id, 'Levitating', $at)",
                ("$id", Guid.NewGuid().ToString("D")), ("$at", SqlHelpers.Timestamp));
            connection.Execute(
                "INSERT INTO task_audit_records (id, kind, tool_name, risk_level, outcome, summary, started_at) VALUES ($id, 'InstallIntegration', 'X', 'SideEffect', 'Vanished', 'x', $at)",
                ("$id", Guid.NewGuid().ToString("D")), ("$at", SqlHelpers.Timestamp));
        }

        var items = await _store.ListAsync(10);

        Assert.Equal(2, items.Count);
        Assert.Single(items.Single(item => item.Task is not null).Task!.Steps);
    }

    // ---- links to conversations ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ARowWhoseIdIsNotOneTheAppWroteIsLeftOut_NotFailedOn()
    {
        await _store.SaveActionAsync(Action());
        using (var connection = _database.Open())
        {
            connection.Execute(
                "INSERT INTO task_audit_records (id, kind, tool_name, risk_level, outcome, summary, started_at) VALUES ('not-an-id', 'InstallIntegration', 'X', 'SideEffect', 'Succeeded', 'x', $at)",
                ("$at", SqlHelpers.Timestamp));
        }

        Assert.Single(await _store.ListAsync(10));
    }

    [Fact]
    public async Task ARunOfAConversationThatIsNotSavedIsKeptWithoutALink_AndLinksOnceItIs()
    {
        var id = Guid.NewGuid();
        var run = Run(AgentTaskStatus.Running, Conversation, null, null, Step(id, 1, AuditStatus.Running, conversation: Conversation));

        // The conversation is saved a little after the run begins: until then there is no row for the run to be linked to.
        await _store.SaveTaskAsync(run);
        using (var connection = _database.Open())
        {
            Assert.Null(connection.ExecuteScalar<string>("SELECT conversation_id FROM agent_tasks"));
            connection.InsertConversation();
        }

        await _store.SaveTaskAsync(run with { Status = AgentTaskStatus.Completed, EndedAt = run.StartedAt.AddSeconds(1) });

        using var check = _database.Open();
        Assert.Equal(SqlHelpers.ConversationId, check.ExecuteScalar<string>("SELECT conversation_id FROM agent_tasks"));
        Assert.Equal(SqlHelpers.ConversationId, check.ExecuteScalar<string>("SELECT conversation_id FROM task_audit_records"));
    }

    [Fact]
    public async Task TheLogOutlivesTheConversationItCameFrom()
    {
        using (var connection = _database.Initialize())
        {
            connection.InsertConversation();
        }

        var id = Guid.NewGuid();
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, Conversation, null, null, Step(id, 1, conversation: Conversation)));
        using (var connection = _database.Open())
        {
            connection.Execute("DELETE FROM conversations");
        }

        var read = Assert.Single(await _store.ListAsync(10)).Task!;
        Assert.Equal(Guid.Empty, read.ConversationId);
        Assert.Single(read.Steps);
        Assert.Null(read.Steps[0].ConversationId);
    }

    // ---- a restart ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatWasGoingOnWhenTheAppClosedIsInterrupted_AndSaysWhere()
    {
        var id = Guid.NewGuid();
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Running, null, null, null, Step(id, 1), Step(id, 2, AuditStatus.WaitingForYou, summary: "Send a message")));
        await _store.SaveActionAsync(Action(status: AuditStatus.Running));

        var read = await CreateStore().ListAsync(10);

        var run = read.Single(item => item.Task is not null).Task!;
        Assert.Equal(AgentTaskStatus.Interrupted, run.Status);
        Assert.Equal([AuditStatus.Succeeded, AuditStatus.Interrupted], run.Steps.Select(step => step.Status));
        Assert.Equal("Interrupted during step 2 (Send a message)", run.FailurePoint);
        Assert.Equal(AuditStatus.Interrupted, read.Single(item => item.Action is not null).Action!.Status);
    }

    [Fact]
    public async Task WhatHadEndedIsNotTouchedByARestart()
    {
        var id = Guid.NewGuid();
        var run = Run(AgentTaskStatus.Completed, null, null, null, Step(id, 1));
        await _store.SaveTaskAsync(run);

        var read = Assert.Single(await CreateStore().ListAsync(10)).Task!;

        Assert.Equal(AgentTaskStatus.Completed, read.Status);
        Assert.Equal(AuditStatus.Succeeded, read.Steps[0].Status);
    }

    // ---- how much is kept ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatIsOlderThanTheLimitIsDeletedAsARunEnds_StepsIncluded()
    {
        var now = _database.Time.GetUtcNow();
        var oldId = Guid.NewGuid();
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now - AuditRetention.MaxAge - TimeSpan.FromDays(1), Step(oldId, 1)));
        await _store.SaveActionAsync(Action(at: now - AuditRetention.MaxAge - TimeSpan.FromDays(1)));
        var keptId = Guid.NewGuid();

        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now, Step(keptId, 1)));

        var items = await _store.ListAsync(10);
        Assert.Equal(keptId, Assert.Single(items).Id);
        using var connection = _database.Open();
        Assert.Equal(1, connection.Count("task_audit_records"));
    }

    [Theory]
    [InlineData(HistoryRetention.SevenDays, 7)]
    [InlineData(HistoryRetention.ThirtyDays, 30)]
    [InlineData(HistoryRetention.NinetyDays, 90)]
    [InlineData(HistoryRetention.UntilDeleted, 90)]
    public async Task TheLogIsNotKeptLongerThanTheUserKeepsTheirConversations_OrNinetyDays(HistoryRetention retention, int days)
    {
        var store = CreateStore(settings: new Retaining(retention));
        var now = _database.Time.GetUtcNow();
        var inside = Guid.NewGuid();
        var outside = Guid.NewGuid();
        await store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddDays(-days).AddHours(1), Step(inside, 1)));
        await store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddDays(-days).AddHours(-1), Step(outside, 1)));

        // A run that ends is what applies the limit; the one that is older than it goes, and so does the first, if the limit is about to pass it.
        var current = Guid.NewGuid();
        await store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now, Step(current, 1)));

        var ids = (await store.ListAsync(10)).Select(item => item.Id).ToList();
        Assert.Contains(current, ids);
        Assert.Contains(inside, ids);
        Assert.DoesNotContain(outside, ids);
    }

    private sealed class Logging(bool deleteAfterHours, int hours = 48) : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings { Cleanup = new CleanupSettings { DeleteLogs = deleteAfterHours, LogHours = hours } });

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task LogsAreDeletedAfterFortyEightHoursUnlessTheUserTurnedThatOff_OrChoseOtherHours()
    {
        var now = _database.Time.GetUtcNow();
        var (recent, day3) = (Guid.NewGuid(), Guid.NewGuid());
        var end = Run(AgentTaskStatus.Completed, null, null, now, Step(Guid.NewGuid(), 1));

        // The default: two days. What ended three days ago goes when the next run ends; what ended a day ago stays.
        var store = CreateStore(settings: new Logging(deleteAfterHours: true));
        await store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddDays(-1), Step(recent, 1)));
        await store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddDays(-3), Step(day3, 1)));
        await store.SaveTaskAsync(end);
        var ids = (await store.ListAsync(10)).Select(item => item.Id).ToList();
        Assert.Contains(recent, ids);
        Assert.DoesNotContain(day3, ids);

        // Other hours are the user's: six hours leaves what ended a day ago out, too.
        await CreateStore(settings: new Logging(deleteAfterHours: true, hours: 6)).SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now, Step(Guid.NewGuid(), 1)));
        Assert.DoesNotContain(recent, (await store.ListAsync(10)).Select(item => item.Id));

        // Off, nothing goes by hours.
        var kept = Guid.NewGuid();
        var off = CreateStore(settings: new Logging(deleteAfterHours: false));
        await off.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddDays(-5), Step(kept, 1)));
        await off.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now, Step(Guid.NewGuid(), 1)));
        Assert.Contains(kept, (await off.ListAsync(10)).Select(item => item.Id));
    }

    [Fact]
    public async Task PruningByHoursDeletesWhatEndedBeforeThemAndLeavesTheRest()
    {
        var now = _database.Time.GetUtcNow();
        var (old, young) = (Guid.NewGuid(), Guid.NewGuid());
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddHours(-50), Step(old, 1)));
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddHours(-47), Step(young, 1)));

        await _store.PruneAsync(TimeSpan.FromHours(48));

        var ids = (await _store.ListAsync(10)).Select(item => item.Id).ToList();
        Assert.Contains(young, ids);
        Assert.DoesNotContain(old, ids);
    }

    [Fact]
    public async Task SettingsThatCannotBeReadMeanTheLongestTheLogIsKept()
    {
        var store = CreateStore(settings: new Unreadable());
        var now = _database.Time.GetUtcNow();
        var sixtyDaysOld = Guid.NewGuid();
        await store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now.AddDays(-60), Step(sixtyDaysOld, 1)));

        await store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, now, Step(Guid.NewGuid(), 1)));

        Assert.Contains(sixtyDaysOld, (await store.ListAsync(10)).Select(item => item.Id));
    }

    [Fact]
    public void OnlyTheNewestRunsAndActionsAreKept()
    {
        using var connection = _database.Initialize();
        var repository = new AuditRepository();
        var now = _database.Time.GetUtcNow();
        for (var i = 0; i < 6; i++)
        {
            var id = Guid.NewGuid();
            var run = Run(AgentTaskStatus.Completed, null, null, now.AddMinutes(i), Step(id, 1));
            repository.SaveTask(connection, run);
            repository.SaveAction(connection, Action(at: now.AddMinutes(i)));
        }

        repository.Prune(connection, now.AddDays(-90), maxTasks: 3, maxActions: 2);

        Assert.Equal(3, connection.Count("agent_tasks"));
        Assert.Equal(3 + 2, connection.Count("task_audit_records"));
        Assert.Equal(
            [now.AddMinutes(3), now.AddMinutes(4), now.AddMinutes(5)],
            connection.Query("SELECT started_at FROM agent_tasks ORDER BY started_at", reader => reader.GetTimestamp(0)));
    }

    // ---- clearing ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClearingDeletesEveryRunStepAndAction_ForReal()
    {
        const string Marker = "ZebraMangoQuartz";
        var id = Guid.NewGuid();
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, null, Step(id, 1, summary: Marker)));
        await _store.SaveActionAsync(Action() with { Summary = Marker + " action", Name = Marker });
        Assert.True(FilesContain(Marker));

        await _store.ClearAsync();

        Assert.Empty(await _store.ListAsync(10));
        using var connection = _database.Open();
        Assert.Equal(0, connection.Count("task_audit_records"));
        Assert.Equal(0, connection.Count("agent_tasks"));
        Assert.False(FilesContain(Marker), "what was cleared is still in the database's files");
    }

    [Fact]
    public async Task ClearingTheLogLeavesTheConversationsAlone()
    {
        using (var connection = _database.Initialize())
        {
            connection.InsertConversation();
            connection.InsertMessage("m1");
        }

        await _store.SaveActionAsync(Action());
        await _store.ClearAsync();

        using var check = _database.Open();
        Assert.Equal(1, check.Count("conversations"));
        Assert.Equal(1, check.Count("messages"));
    }

    // ---- failures ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADatabaseThatCannotBeOpenedIsADatabaseException_WhoseMessageHoldsNoPathOrName()
    {
        const string Secret = "TodoistSecretName";
        var store = CreateStore(new FailingInitializer());

        var failure = await Assert.ThrowsAsync<DatabaseException>(() => store.SaveActionAsync(Action(name: Secret) with { Summary = Secret }));

        Assert.DoesNotContain(Secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, _logs.AllText, StringComparison.Ordinal);
        Assert.Contains("IOException", _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADatabaseFromANewerBuildIsADatabaseException_AndIsLeftAsItIs()
    {
        var latest = _database.CreateInitializer().Initialize().CurrentVersion;
        using (var connection = _database.Open())
        {
            new SchemaVersionRepository().Record(connection, latest + 1, "from_the_future", DateTimeOffset.UnixEpoch);
        }

        var failure = await Assert.ThrowsAsync<DatabaseException>(() => CreateStore().ListAsync(10));
        Assert.IsType<DatabaseTooNewException>(failure.InnerException);
        using var check = _database.Open();
        Assert.Equal(latest + 1, check.ExecuteScalar<int>("SELECT MAX(version) FROM schema_version"));
    }

    [Fact]
    public async Task NoOperationLogsAnythingButTheTypeOfAFailureAndCounts()
    {
        const string Secret = "TodoistSecretName";
        var id = Guid.NewGuid();
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, null, Step(id, 1, summary: Secret, name: "send_message")));
        await _store.SaveActionAsync(Action(name: Secret) with { Summary = Secret });
        await _store.ListAsync(10);
        await _store.ClearAsync();

        Assert.DoesNotContain(Secret, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("send_message", _logs.AllText, StringComparison.Ordinal);
    }

    // ---- the schema -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ATaskHoldsNoContentAndNoColumnOfTheLogCanHoldBytes()
    {
        using var connection = _database.Initialize();

        Assert.Equal(["id", "conversation_id", "status", "failure_point", "started_at", "completed_at"], connection.Columns("agent_tasks"));
        Assert.Equal(
            [
                "id", "conversation_id", "tool_name", "risk_level", "outcome", "started_at", "completed_at", "task_id", "kind", "step_number", "summary", "confirmation", "error_code",
            ],
            connection.Columns("task_audit_records"));
        Assert.All(
            connection.Query("SELECT type FROM pragma_table_info('agent_tasks') UNION ALL SELECT type FROM pragma_table_info('task_audit_records')", reader => reader.GetString(0)),
            type => Assert.Contains(type, new[] { "TEXT", "INTEGER" }));
    }

    [Fact]
    public async Task UpgradingFromTheSchemaBeforeItKeepsWhatWasThere_AndGivesItTheDefaults()
    {
        using (var connection = _database.Open())
        {
            _database.CreateMigrator(new MigrationCatalog(MigrationCatalog.LoadDefault().Migrations.Where(migration => migration.Version <= 4))).Migrate(connection);
            connection.InsertConversation();
            connection.Execute(
                "INSERT INTO task_audit_records (id, conversation_id, tool_name, risk_level, outcome, started_at) VALUES ($id, $c, 'copy_text', 'SideEffect', 'Succeeded', $at)",
                ("$id", Guid.NewGuid().ToString("D")), ("$c", SqlHelpers.ConversationId), ("$at", SqlHelpers.Timestamp));
        }

        using (var connection = _database.Open())
        {
            var result = _database.CreateMigrator(MigrationCatalog.LoadDefault()).Migrate(connection);
            Assert.Equal(4, result.PreviousVersion);
            Assert.Empty(connection.Query("PRAGMA foreign_key_check", _ => 0));
            Assert.Equal(SqlHelpers.AllMigrationVersions(), connection.AppliedVersions());
        }

        var read = Assert.Single(await CreateStore().ListAsync(10)).Action!;
        Assert.Equal(("copy_text", AuditKind.ToolCall, AuditStatus.Succeeded, string.Empty, 0), (read.Name, read.Kind, read.Status, read.Summary, read.Sequence));
        Assert.Equal(Conversation, read.ConversationId);
    }

    [Fact]
    public async Task DeletingARunDeletesItsSteps_ButDeletingAStepsConversationDeletesNothingOfTheLog()
    {
        var id = Guid.NewGuid();
        await _store.SaveTaskAsync(Run(AgentTaskStatus.Completed, null, null, null, Step(id, 1), Step(id, 2)));
        using var connection = _database.Open();
        Assert.Equal(2, connection.Count("task_audit_records"));

        connection.Execute("DELETE FROM agent_tasks");

        Assert.Equal(0, connection.Count("task_audit_records"));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private bool FilesContain(string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        return new[] { string.Empty, "-wal", "-shm" }
            .Select(suffix => _database.Options.DatabasePath + suffix)
            .Where(File.Exists)
            .Select(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            })
            .Any(bytes => bytes.AsSpan().IndexOf(needle) >= 0);
    }

    private sealed class FailingInitializer : IDatabaseInitializer
    {
        public MigrationResult Initialize() => throw new IOException("C:\\Users\\someone\\assistant.db is locked");
    }
}
