using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Domain;
using Assistant.UI.Settings;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// Settings > Activity, as the user meets it (PROJECT_SPEC §4.9, step 117): what the Assistant did on their behalf, newest first, with the steps of a run that can be opened, what
/// they answered when asked, where a run could not go on, a button that stops a run that goes on, and a button that clears the log after asking once more.
/// </summary>
public sealed partial class PromptInputControlTests
{
    private static readonly DateTimeOffset ActivityNow = new(2026, 10, 3, 14, 30, 0, TimeSpan.Zero);

    private sealed class FakeAuditHistory : IAuditHistory
    {
        public event EventHandler? Changed;

        public List<ActivityItem> Items { get; } = [];

        public List<Guid> Cancelled { get; } = [];

        public int Clears { get; private set; }

        public int Lists { get; private set; }

        public Exception? FailList { get; set; }

        public Exception? FailClear { get; set; }

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<IReadOnlyList<ActivityItem>> ListAsync(int limit, CancellationToken cancellationToken = default)
        {
            Lists++;
            if (FailList is not null)
            {
                return Task.FromException<IReadOnlyList<ActivityItem>>(FailList);
            }

            return Task.FromResult<IReadOnlyList<ActivityItem>>([.. Items.OrderByDescending(item => item.StartedAt).Take(limit)]);
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            if (FailClear is not null)
            {
                return Task.FromException(FailClear);
            }

            Clears++;
            Items.Clear();
            return Task.CompletedTask;
        }

        public bool CancelTask(Guid taskId)
        {
            Cancelled.Add(taskId);
            return Items.Any(item => item.Task is { IsRunning: true } task && task.Id == taskId);
        }
    }

    private static AuditEntry ActivityStep(
        Guid task, int number, AuditStatus status = AuditStatus.Succeeded, string name = "search_files", string summary = "Search your files",
        string? code = null, ConfirmationDecision? confirmation = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TaskId = task,
            Sequence = number,
            Kind = AuditKind.ToolCall,
            Name = name,
            Risk = RiskLevel.ReadOnly,
            Status = status,
            Summary = summary,
            ErrorCode = code,
            Confirmation = confirmation,
            StartedAt = ActivityNow.AddMinutes(-number),
            EndedAt = status.IsFinished() ? ActivityNow.AddMinutes(-number).AddSeconds(2) : null,
        };

    private static ActivityItem ActivityTask(AgentTaskStatus status, DateTimeOffset at, string? failure = null, bool cancelRequested = false, params AuditEntry[] steps)
    {
        var id = steps.Length > 0 && steps[0].TaskId is { } owned ? owned : Guid.NewGuid();
        return ActivityItem.Of(new AgentTaskSnapshot
        {
            Id = id,
            StartedAt = at,
            EndedAt = status == AgentTaskStatus.Running ? null : at.AddSeconds(9),
            Status = status,
            Steps = steps,
            FailurePoint = failure,
            CancelRequested = cancelRequested,
        });
    }

    private static ActivityItem ActivityAction(
        DateTimeOffset at, AuditKind kind = AuditKind.InstallIntegration, AuditStatus status = AuditStatus.Succeeded, string? code = null,
        ConfirmationDecision confirmation = ConfirmationDecision.Approved) =>
        ActivityItem.Of(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = "Todoist",
            Risk = RiskLevel.SideEffect,
            Status = status,
            Summary = "Install Todoist 2.1.0",
            Confirmation = confirmation,
            ErrorCode = code,
            StartedAt = at,
            EndedAt = status.IsFinished() ? at.AddSeconds(4) : null,
        });

    private static ActivityItem ThreeStepRun(DateTimeOffset at)
    {
        var id = Guid.NewGuid();
        return ActivityTask(AgentTaskStatus.Completed, at, null, false, ActivityStep(id, 1), ActivityStep(id, 2, summary: "Open a file", name: "open_file"), ActivityStep(id, 3, summary: "Send a message", name: "send_message", confirmation: ConfirmationDecision.Approved));
    }

    private static (SettingsKit Kit, FakeAuditHistory History) ActivityKit(params ActivityItem[] items)
    {
        var history = new FakeAuditHistory();
        history.Items.AddRange(items);
        return (CreateSettingsKit(activity: history), history);
    }

    // ---- the section ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ActivityIsASectionOfTheSettingsWindowRightBeforeAbout() => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        var titles = kit.Model.Sections.Select(section => section.Title).ToList();

        Assert.Equal(titles.IndexOf("About") - 1, titles.IndexOf("Activity"));
        kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Activity);
        Assert.Same(kit.Model.Activity, kit.Model.CurrentPage);
    });

    // ---- listing ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheLogIsListedNewestFirst_WithWhatEachWasAndHowItEnded() => RunSta(() =>
    {
        var older = ThreeStepRun(ActivityNow.AddHours(-3));
        var install = ActivityAction(ActivityNow.AddHours(-2));
        var failedId = Guid.NewGuid();
        var failed = ActivityTask(
            AgentTaskStatus.Cancelled, ActivityNow.AddHours(-1), "Stopped during step 1 (Search your files)", false, ActivityStep(failedId, 1, AuditStatus.Cancelled));
        var (kit, _) = ActivityKit(older, failed, install);

        var rows = kit.Model.Activity.Items;

        Assert.Equal([failed.Id, install.Id, older.Id], rows.Select(row => row.Id));
        Assert.True(kit.Model.Activity.HasItems);
        Assert.False(kit.Model.Activity.HasNoItems);

        Assert.Equal("Search your files and 2 more", rows[2].Title);
        Assert.Contains("3 steps", rows[2].Detail, StringComparison.Ordinal);
        Assert.Equal("Done", rows[2].StatusText);
        Assert.True(rows[2].IsDone);

        Assert.Equal("Install Todoist 2.1.0", rows[1].Title);
        Assert.Equal("Done", rows[1].StatusText);
        Assert.Equal("You allowed it", rows[1].ConfirmationText);
        Assert.True(rows[1].HasConfirmation);

        Assert.Equal("Search your files", rows[0].Title);
        Assert.Equal("Stopped", rows[0].StatusText);
        Assert.Equal("Stopped during step 1 (Search your files)", rows[0].FailurePoint);
        Assert.True(rows[0].HasFailure && rows[0].IsProblem);
    });

    [Fact]
    public void AnInstallThatDidNotWorkSaysWhy_AndAnOfferTheUserTurnedDownSaysThat() => RunSta(() =>
    {
        var failed = ActivityAction(ActivityNow.AddMinutes(-2), status: AuditStatus.Failed, code: "hash_mismatch");
        var declined = ActivityAction(ActivityNow.AddMinutes(-1), AuditKind.DeclineIntegration, AuditStatus.Declined, confirmation: ConfirmationDecision.Declined);
        var (kit, _) = ActivityKit(failed, declined);

        var rows = kit.Model.Activity.Items;

        Assert.Equal("Didn't work: the download was not what was reviewed", rows[1].StatusText);
        Assert.True(rows[1].IsProblem);
        Assert.Equal("You didn't allow it, so it wasn't done", rows[0].StatusText);
        Assert.Equal("You chose Don't allow", rows[0].ConfirmationText);
        Assert.False(rows[0].IsProblem);
    });

    [Fact]
    public void TheStepsOfARunAreOpenedAndClosedByItsButton_AndAnActionHasNone() => RunSta(() =>
    {
        var run = ThreeStepRun(ActivityNow.AddMinutes(-5));
        var install = ActivityAction(ActivityNow.AddMinutes(-10));
        var (kit, _) = ActivityKit(run, install);
        var runRow = kit.Model.Activity.Items[0];
        var installRow = kit.Model.Activity.Items[1];

        Assert.True(runRow.HasSteps);
        Assert.False(runRow.IsExpanded);
        Assert.Equal("Show steps", runRow.ToggleLabel);
        Assert.Equal(["Search your files", "Open a file", "Send a message"], runRow.Steps.Select(step => step.Summary));

        runRow.ToggleCommand.Execute(null);
        Assert.True(runRow.IsExpanded);
        Assert.Equal("Hide steps", runRow.ToggleLabel);
        Assert.Equal("You allowed it", runRow.Steps[2].ConfirmationText);
        runRow.ToggleCommand.Execute(null);
        Assert.False(runRow.IsExpanded);

        Assert.False(installRow.HasSteps);
        Assert.False(installRow.ToggleCommand.CanExecute(null));
        installRow.IsExpanded = true;
        Assert.False(installRow.IsExpanded);
    });

    [Fact]
    public void WithNothingToListThePageSaysWhatItIsFor() => RunSta(() =>
    {
        var (kit, _) = ActivityKit();

        Assert.True(kit.Model.Activity.HasNoItems);
        Assert.False(kit.Model.Activity.HasItems);
        Assert.Equal("Nothing yet.", kit.Model.Activity.EmptyText);
        Assert.False(kit.Model.Activity.ClearCommand.CanExecute(null));
    });

    [Fact]
    public void ALogThatCannotBeReadIsSaidSo_AndTheWindowStillOpens() => RunSta(() =>
    {
        var history = new FakeAuditHistory { FailList = new System.IO.IOException("C:\\private\\path is locked") };
        var kit = CreateSettingsKit(activity: history);

        Assert.True(kit.Model.Activity.HasNotice);
        Assert.Contains("couldn't be read", kit.Model.Activity.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("private", kit.Model.Activity.Notice, StringComparison.Ordinal);
        Assert.Empty(kit.Model.Activity.Items);
    });

    [Fact]
    public void WithoutALogThePageHasNothingToShow_AndNothingToClear() => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        Assert.False(kit.Model.Activity.HasHistory);
        Assert.Empty(kit.Model.Activity.Items);
        Assert.False(kit.Model.Activity.ClearCommand.CanExecute(null));
    });

    // ---- clearing -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ClearingAsksOnceMore_KeepingChangesNothing_AndClearingEmptiesTheLog() => RunSta(() =>
    {
        var (kit, history) = ActivityKit(ThreeStepRun(ActivityNow.AddMinutes(-5)), ActivityAction(ActivityNow.AddMinutes(-9)));
        var page = kit.Model.Activity;

        Assert.True(page.ClearCommand.CanExecute(null));
        page.ClearCommand.Execute(null);
        Assert.True(page.ConfirmingClear);
        Assert.Equal(0, history.Clears);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("Clear all logs?", page.ClearQuestion);
        Assert.False(page.ClearCommand.CanExecute(null));

        page.KeepCommand.Execute(null);
        Assert.False(page.ConfirmingClear);
        Assert.Equal(0, history.Clears);

        page.ClearCommand.Execute(null);
        page.ConfirmClearCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => page.Items.Count == 0, "The log was not cleared.");

        Assert.Equal(1, history.Clears);
        Assert.False(page.ConfirmingClear);
        Assert.True(page.HasNoItems);
    });

    [Fact]
    public void ALogThatCannotBeClearedSaysSo_AndKeepsWhatItListed() => RunSta(() =>
    {
        var (kit, history) = ActivityKit(ActivityAction(ActivityNow.AddMinutes(-9)));
        history.FailClear = new System.IO.IOException("locked");
        var page = kit.Model.Activity;

        page.ClearCommand.Execute(null);
        page.ConfirmClearCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => page.HasNotice, "The page did not say it failed.");

        Assert.Contains("couldn't be cleared", page.Notice, StringComparison.Ordinal);
        Assert.Single(page.Items);
        Assert.False(page.ConfirmingClear);
    });

    // ---- stopping a run -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARunThatGoesOnCanBeStoppedFromThePage_Once() => RunSta(() =>
    {
        var id = Guid.NewGuid();
        var running = ActivityTask(AgentTaskStatus.Running, ActivityNow.AddSeconds(-30), null, false, ActivityStep(id, 1, AuditStatus.Succeeded), ActivityStep(id, 2, AuditStatus.Running));
        var done = ThreeStepRun(ActivityNow.AddHours(-1));
        var (kit, history) = ActivityKit(running, done);
        var rows = kit.Model.Activity.Items;

        Assert.True(rows[0].IsRunning);
        Assert.Equal("Working on it", rows[0].StatusText);
        Assert.True(rows[0].CanCancel);
        Assert.False(rows[1].CanCancel);
        Assert.Equal("Cancel", rows[0].CancelLabel);

        rows[0].CancelCommand.Execute(null);
        Assert.Equal([id], history.Cancelled);

        // The log tells the page the run was asked to stop; the button says so and cannot be pressed again.
        history.Items[0] = ActivityTask(AgentTaskStatus.Running, ActivityNow.AddSeconds(-30), null, true, running.Task!.Steps.ToArray());
        history.Raise();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !rows[0].CanCancel, "The page did not follow the request to stop.");

        Assert.Equal("Stopping…", rows[0].CancelLabel);
        rows[0].CancelCommand.Execute(null);
        Assert.Equal([id], history.Cancelled);
    });

    // ---- following the log -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhatHappensWhileThePageIsOpenIsListed_AndALineTheUserOpenedStaysOpen() => RunSta(() =>
    {
        var run = ThreeStepRun(ActivityNow.AddMinutes(-5));
        var (kit, history) = ActivityKit(run);
        var page = kit.Model.Activity;
        page.Items[0].ToggleCommand.Execute(null);
        Assert.True(page.Items[0].IsExpanded);

        var install = ActivityAction(ActivityNow);
        history.Items.Add(install);
        history.Raise();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => page.Items.Count == 2, "The page did not list the new action.");

        Assert.Equal(install.Id, page.Items[0].Id);
        Assert.Equal(run.Id, page.Items[1].Id);
        Assert.True(page.Items[1].IsExpanded);
    });

    [Fact]
    public void ARunThatMovesFromWorkingToDoneIsUpdatedWhereItStands() => RunSta(() =>
    {
        var id = Guid.NewGuid();
        var running = ActivityTask(AgentTaskStatus.Running, ActivityNow.AddSeconds(-5), null, false, ActivityStep(id, 1, AuditStatus.Running));
        var (kit, history) = ActivityKit(running);
        var page = kit.Model.Activity;
        var row = page.Items[0];
        Assert.True(row.IsRunning);

        history.Items[0] = ActivityTask(AgentTaskStatus.Completed, ActivityNow.AddSeconds(-5), null, false, ActivityStep(id, 1, AuditStatus.Succeeded));
        history.Raise();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => row.IsDone, "The page did not follow the run to its end.");

        Assert.Same(row, page.Items[0]);
        Assert.Equal("Done", row.StatusText);
        Assert.False(row.CanCancel);
        Assert.True(row.Steps[0].IsDone);
    });

    [Fact]
    public void ThePageDoesNotReadTheLogWhileTheWindowIsPutAway_AndReadsItAgainWhenItOpens() => RunSta(() =>
    {
        var (kit, history) = ActivityKit(ActivityAction(ActivityNow.AddMinutes(-9)));
        var page = kit.Model.Activity;
        var reads = history.Lists;

        page.StopFollowing();
        history.Items.Add(ActivityAction(ActivityNow));
        history.Raise();
        Thread.Sleep(ActivityPage.RefreshDelay + TimeSpan.FromMilliseconds(250));
        Pump();

        Assert.Equal(reads, history.Lists);
        Assert.Single(page.Items);

        kit.Model.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal(2, page.Items.Count);
        history.Items.Add(ActivityAction(ActivityNow.AddMinutes(1)));
        history.Raise();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => page.Items.Count == 3, "The page did not follow the log once the window opened again.");
    });

    [Fact]
    public void PuttingTheSettingsWindowAwayStopsThePageFollowingTheLog() => RunSta(() => WithTheme(() =>
    {
        var history = new FakeAuditHistory();
        var kit = CreateSettingsKit(activity: history);
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            Pump();
            var reads = history.Lists;

            window.Hide();
            Pump();
            history.Items.Add(ActivityAction(ActivityNow));
            history.Raise();
            Thread.Sleep(ActivityPage.RefreshDelay + TimeSpan.FromMilliseconds(250));
            Pump();

            Assert.Equal(reads, history.Lists);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void ThePageStopsFollowingTheLogWhenTheWindowIsDone() => RunSta(() =>
    {
        var (kit, history) = ActivityKit(ActivityAction(ActivityNow.AddMinutes(-9)));
        kit.Model.Dispose();

        history.Items.Add(ActivityAction(ActivityNow));
        history.Raise();
        Thread.Sleep(Assistant.UI.Settings.ActivityPage.RefreshDelay + TimeSpan.FromMilliseconds(200));
        Pump();

        Assert.Single(kit.Model.Activity.Items);
    });

    // ---- how it is drawn --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePageIsDrawnWithTheLogAndItsButtons_WithoutABindingError() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        using var errors = OfferBindingErrors.Listen();
        var id = Guid.NewGuid();
        var running = ActivityTask(AgentTaskStatus.Running, ActivityNow.AddSeconds(-30), null, false, ActivityStep(id, 1, AuditStatus.Succeeded), ActivityStep(id, 2, AuditStatus.WaitingForYou, "send_message", "Send a message"));
        var (kit, _) = ActivityKit(
            running,
            ActivityAction(ActivityNow.AddMinutes(-20), status: AuditStatus.Failed, code: "hash_mismatch"),
            ThreeStepRun(ActivityNow.AddHours(-2)),
            ActivityTask(AgentTaskStatus.Incomplete, ActivityNow.AddHours(-3), "Used all the steps it is allowed after step 4 (Search your files)", false, ActivityStep(Guid.NewGuid(), 1)));
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            Pump();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Activity);
            window.UpdateLayout();
            Pump();
            var page = Named<ContentControl>(window, "PageContent");

            var texts = AllTextOf(page).ToList();
            Assert.Contains("Logs", texts);
            Assert.Contains("Search your files and 2 more", texts);
            Assert.Contains("Install Todoist 2.1.0", texts);
            Assert.Contains("Didn't work: the download was not what was reviewed", texts);
            Assert.Contains("You allowed it", texts);
            Assert.Contains("Used all the steps it is allowed after step 4 (Search your files)", texts);
            Assert.Contains("Working on it", texts);
            Assert.DoesNotContain(texts, text => text.Contains("never holds what you asked", StringComparison.Ordinal));
            Assert.True(ButtonsVisible(page, "Cancel"));
            Assert.Equal(3, Descendants<Button>(page).Count(button => button.IsVisible && button.Content as string == "Show steps"));
            Assert.True(ButtonsVisible(page, "Clear"));
            RenderFixture(Named<Grid>(window, "Root"), "settings-activity-collapsed.png", 2);

            // The steps of a run are drawn the same way the panel in the conversation draws them.
            var open = Descendants<Button>(page).First(button => button.IsVisible && button.Content as string == "Show steps");
            Click(open);
            Pump();
            Assert.Contains(AllTextOf(page), text => text == "Hide steps");
            Assert.Contains(AllTextOf(page), text => text == "Waiting for you");
            RenderFixture(Named<Grid>(window, "Root"), "settings-activity-open.png", 2);

            // Clearing asks first.
            Click(ButtonLabelled(page, "Clear"));
            Pump();
            Assert.Contains(AllTextOf(page), text => text == "Clear all logs?");
            Assert.True(ButtonsVisible(page, "Keep"));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    [Fact]
    public void WithoutALogThePageStillDrawsAndItsClearButtonCannotBePressed() => RunSta(() => WithTheme(() =>
    {
        using var errors = OfferBindingErrors.Listen();
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Activity);
            window.UpdateLayout();
            Pump();
            var page = Named<ContentControl>(window, "PageContent");

            Assert.True(ButtonsVisible(page, "Clear"));
            Assert.False(ButtonLabelled(page, "Clear").IsEnabled);
            Assert.NotEmpty(Descendants<Assistant.UI.Controls.SettingsRow>(page));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
        }
    }));
}
