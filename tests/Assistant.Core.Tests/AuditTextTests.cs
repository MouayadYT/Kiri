using Assistant.Core.Agent;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// The words of the activity log (PROJECT_SPEC §3.2, §4.8, step 117): everything it keeps is fixed words, a tool's name, a tidied name or a code from a fixed few, so
/// nothing private can get into it by way of a name, a summary or a failure.
/// </summary>
public sealed class AuditTextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    // ---- names ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ANameIsTidied_ControlsAndInvisibleMarksGoAndSpacesAreCollapsed()
    {
        var tidy = AuditText.Label("  Todo\u200Bist \t\n  Pro\u202E\u0007  ");

        Assert.Equal("Todoist Pro", tidy);
    }

    [Fact]
    public void ALongNameIsCut_AndSaysSo()
    {
        var tidy = AuditText.Label(string.Join(' ', Enumerable.Repeat("Microsoft", 12)));

        Assert.True(tidy.Length <= AuditText.MaxLabelLength);
        Assert.EndsWith("…", tidy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk-live-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    [InlineData("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=")]
    public void ARunThatLooksLikeAKeyOrATokenIsNeverKept(string secret)
    {
        var tidy = AuditText.Label("Notion " + secret);
        var sentence = AuditText.Sentence("Install Notion with " + secret + " now");

        Assert.DoesNotContain(secret[..16], tidy, StringComparison.Ordinal);
        Assert.DoesNotContain(secret[..16], sentence, StringComparison.Ordinal);
        Assert.StartsWith("Notion", tidy, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongNameWithNoDigitInItIsNotTakenForAKey()
    {
        Assert.Equal("Visual-Studio-Code-Insiders-Edition-Preview", AuditText.Label("Visual-Studio-Code-Insiders-Edition-Preview"));
    }

    [Fact]
    public void AnOrdinaryShortNameIsKeptAsItIs()
    {
        Assert.Equal("Microsoft To Do", AuditText.Label("Microsoft To Do"));
        Assert.Equal("Install Todoist 2.1.0", AuditText.Sentence("Install Todoist 2.1.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingStaysNothing(string? text)
    {
        Assert.Equal(string.Empty, AuditText.Label(text));
        Assert.Equal(string.Empty, AuditText.Sentence(text));
    }

    [Theory]
    [InlineData("send_message", "send_message")]
    [InlineData("mcp_samplecalendar_list_events", "mcp_samplecalendar_list_events")]
    [InlineData("Send Message", "unknown_tool")]
    [InlineData("send_message; DROP TABLE x", "unknown_tool")]
    [InlineData("C:\\secret\\file", "unknown_tool")]
    [InlineData("_hidden", "unknown_tool")]
    [InlineData("1st_tool", "unknown_tool")]
    [InlineData("", "unknown_tool")]
    [InlineData(null, "unknown_tool")]
    public void AToolIsKeptByNameOnlyWhenItIsTheShapeOfARegistryName(string? name, string expected) =>
        Assert.Equal(expected, AuditText.ToolName(name));

    [Fact]
    public void ANameLongerThanTheRegistryAllowsIsNotKept() =>
        Assert.Equal("unknown_tool", AuditText.ToolName(new string('a', AuditText.MaxToolNameLength + 1)));

    // ---- what a tool is ---------------------------------------------------------------------------------------------

    [Fact]
    public void EveryToolTheAssistantHasHasItsOwnWords()
    {
        // The Assistant's own tools, by name; a tool added later must be given words here too (the tools' own test checks the registry against this list).
        string[] tools =
        [
            "open_application", "open_file", "reveal_file", "open_folder", "get_volume", "set_volume", "mute", "unmute", "take_screenshot", "calculate",
            "get_calendar_events", "search_calendar_events", "draft_message", "send_message", "remember_person", "search_files", "read_file_text", "read_screen_text", "search_web",
            "set_alarm", "start_timer", "control_timer", "stopwatch", "start_focus_session", "control_home_device", "get_home_devices", "get_time",
            "set_do_not_disturb", "remember", "set_clock_display",
        ];

        foreach (var tool in tools)
        {
            Assert.True(AuditText.HasPhrase(tool), tool);
            Assert.NotEqual("Use a tool", AuditText.ToolPhrase(tool));
        }

        Assert.Equal(tools.Order(), AuditText.KnownTools.Order());
    }

    [Fact]
    public void AConnectedAppsToolIsSaidToBeOne_WithTheAppsName()
    {
        Assert.Equal("Use a connected app (samplecalendar)", AuditText.ToolPhrase("mcp_samplecalendar_list_events"));
        Assert.Equal("Use a connected app", AuditText.ToolPhrase("mcp_"));
        Assert.Equal("Use a tool", AuditText.ToolPhrase("something_else"));
        Assert.Equal("Use a tool", AuditText.ToolPhrase("Not A Name"));
    }

    // ---- why it did not work ------------------------------------------------------------------------------------

    [Fact]
    public void EveryCodeOfAToolResultHasWordsOfItsOwn()
    {
        string[] codes =
        [
            ToolErrors.UnknownTool, ToolErrors.InvalidArguments, ToolErrors.PermissionOff, ToolErrors.NotAllowed, ToolErrors.Declined, ToolErrors.NoAnswer,
            ToolErrors.CouldNotAsk, ToolErrors.TimedOut, ToolErrors.Failed, ToolErrors.Repeated, ToolErrors.TooManyCalls, ToolErrors.ResultTooLarge,
        ];

        foreach (var code in codes)
        {
            Assert.True(AuditText.HasReason(code), code);
            Assert.NotEqual("something went wrong", AuditText.Reason(code));
            Assert.Equal(code, AuditText.Code(code));
        }
    }

    [Fact]
    public void ACodeTheLogHasNoWordsForIsKeptAsTheGeneralOne_SoAServerCannotPutItsOwnTextThere()
    {
        Assert.Equal(ToolErrors.Failed, AuditText.Code("secret_token_abc123_was_invalid"));
        Assert.Null(AuditText.Code(null));
        Assert.Null(AuditText.Code(""));
        Assert.Equal("something went wrong", AuditText.Reason("secret_token_abc123_was_invalid"));
    }

    [Theory]
    [InlineData(AuditStatus.Running, "Working")]
    [InlineData(AuditStatus.WaitingForYou, "Waiting for you")]
    [InlineData(AuditStatus.Succeeded, "Done")]
    [InlineData(AuditStatus.Cancelled, "Stopped")]
    [InlineData(AuditStatus.Interrupted, "Interrupted when the app closed")]
    public void AStatusIsSaidInWords(AuditStatus status, string expected) => Assert.Equal(expected, AuditText.StatusText(status));

    [Fact]
    public void AStepThatDidNotWorkSaysWhy()
    {
        Assert.Equal("Didn't work: the tool failed", AuditText.StatusText(AuditStatus.Failed, null, ToolErrors.Failed));
        Assert.Equal("Didn't work", AuditText.StatusText(AuditStatus.Failed));
        Assert.Equal("Took too long, so it was given up", AuditText.StatusText(AuditStatus.TimedOut, null, ToolErrors.TimedOut));
        Assert.Equal("Not run: the same call had been made already", AuditText.StatusText(AuditStatus.Skipped, null, ToolErrors.Repeated));
    }

    [Fact]
    public void AStepTheUserDidNotAllowSaysWhatTheyAnswered()
    {
        Assert.Equal("You didn't allow it, so it wasn't done", AuditText.StatusText(AuditStatus.Declined, ConfirmationDecision.Declined));
        Assert.Equal("No answer in time, so it wasn't done", AuditText.StatusText(AuditStatus.Declined, ConfirmationDecision.NoAnswer));
        Assert.Equal("You couldn't be asked, so it wasn't done", AuditText.StatusText(AuditStatus.Declined, ConfirmationDecision.CouldNotAsk));
        Assert.Equal("Not done", AuditText.StatusText(AuditStatus.Declined));
    }

    [Fact]
    public void WhatTheUserAnsweredIsSaidInWords_AndNothingWhenTheyWereNotAsked()
    {
        Assert.Equal("You allowed it", AuditText.ConfirmationText(ConfirmationDecision.Approved));
        Assert.Equal("You chose Don't allow", AuditText.ConfirmationText(ConfirmationDecision.Declined));
        Assert.Equal("No answer in time", AuditText.ConfirmationText(ConfirmationDecision.NoAnswer));
        Assert.Equal("You couldn't be asked", AuditText.ConfirmationText(ConfirmationDecision.CouldNotAsk));
        Assert.Null(AuditText.ConfirmationText(null));
    }

    [Fact]
    public void EveryStatusAndTaskStatusHasWords()
    {
        foreach (var status in Enum.GetValues<AuditStatus>())
        {
            Assert.NotEmpty(AuditText.StatusText(status));
        }

        foreach (var status in Enum.GetValues<AgentTaskStatus>())
        {
            Assert.NotEmpty(AuditText.TaskStatusText(status));
        }
    }

    // ---- how long the log is kept ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HistoryRetention.UntilDeleted, 90)]
    [InlineData(HistoryRetention.SevenDays, 7)]
    [InlineData(HistoryRetention.ThirtyDays, 30)]
    [InlineData(HistoryRetention.NinetyDays, 90)]
    public void TheLogIsKeptNoLongerThanNinetyDays_AndNoLongerThanTheUserKeepsTheirHistory(HistoryRetention retention, int days) =>
        Assert.Equal(TimeSpan.FromDays(days), AuditRetention.MaxAgeFor(retention));

    // ---- how a run ended ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentOutcome.Completed, AgentStopReason.Answered, AgentTaskStatus.Completed)]
    [InlineData(AgentOutcome.Completed, AgentStopReason.RoundLimit, AgentTaskStatus.Incomplete)]
    [InlineData(AgentOutcome.Completed, AgentStopReason.ToolCallLimit, AgentTaskStatus.Incomplete)]
    [InlineData(AgentOutcome.Completed, AgentStopReason.TimeLimit, AgentTaskStatus.Incomplete)]
    [InlineData(AgentOutcome.Completed, AgentStopReason.Looping, AgentTaskStatus.Incomplete)]
    [InlineData(AgentOutcome.Stopped, AgentStopReason.Cancelled, AgentTaskStatus.Cancelled)]
    [InlineData(AgentOutcome.Abandoned, AgentStopReason.Answered, AgentTaskStatus.Cancelled)]
    [InlineData(AgentOutcome.Failed, AgentStopReason.Failed, AgentTaskStatus.Failed)]
    public void ARunIsDescribedByHowItEndedAndWhy(AgentOutcome outcome, AgentStopReason reason, AgentTaskStatus expected) =>
        Assert.Equal(expected, AgentTaskRules.StatusOf(outcome, reason));

    private static AuditEntry Step(
        int number, AuditStatus status, string summary = "Open an application", string? code = null, ConfirmationDecision? confirmation = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Sequence = number,
            Kind = AuditKind.ToolCall,
            Name = "open_application",
            Status = status,
            Summary = summary,
            ErrorCode = code,
            Confirmation = confirmation,
            StartedAt = At,
            EndedAt = status.IsFinished() ? At : null,
        };

    [Fact]
    public void AStopWhileAStepWentOn_SaysWhichStep()
    {
        var point = AgentTaskRules.FailurePoint(
            AgentTaskStatus.Cancelled, AgentStopReason.Cancelled, [Step(1, AuditStatus.Succeeded), Step(2, AuditStatus.Running, "Send a message")]);

        Assert.Equal("Stopped during step 2 (Send a message)", point);
    }

    [Fact]
    public void AStopBetweenStepsSaysWhereItCameAfter_AndAStopBeforeTheFirstSaysThat()
    {
        Assert.Equal(
            "Stopped after step 1 (Open an application)",
            AgentTaskRules.FailurePoint(AgentTaskStatus.Cancelled, AgentStopReason.Cancelled, [Step(1, AuditStatus.Succeeded)]));
        Assert.Equal("Stopped before the first step", AgentTaskRules.FailurePoint(AgentTaskStatus.Cancelled, AgentStopReason.Cancelled, []));
    }

    [Fact]
    public void AModelThatCouldNotGoOn_IsToldAsThat()
    {
        Assert.Equal(
            "The model couldn't go on after step 1 (Open an application)",
            AgentTaskRules.FailurePoint(AgentTaskStatus.Failed, AgentStopReason.Failed, [Step(1, AuditStatus.Succeeded)]));
        Assert.Equal("The model couldn't start", AgentTaskRules.FailurePoint(AgentTaskStatus.Failed, AgentStopReason.Failed, []));
    }

    [Theory]
    [InlineData(AgentStopReason.TimeLimit, "Ran out of time after step 2 (Open an application)")]
    [InlineData(AgentStopReason.Looping, "Kept repeating itself after step 2 (Open an application)")]
    [InlineData(AgentStopReason.RoundLimit, "Used all the steps it is allowed after step 2 (Open an application)")]
    [InlineData(AgentStopReason.ToolCallLimit, "Used all the steps it is allowed after step 2 (Open an application)")]
    public void ARunThatHitABoundSaysWhichBoundAndWhere(AgentStopReason reason, string expected) =>
        Assert.Equal(expected, AgentTaskRules.FailurePoint(AgentTaskStatus.Incomplete, reason, [Step(1, AuditStatus.Succeeded), Step(2, AuditStatus.Succeeded)]));

    [Fact]
    public void ARunThatEndedAfterAStepThatDidNotWork_SaysWhichAndWhy()
    {
        Assert.Equal(
            "Step 2 (Open an application) took too long",
            AgentTaskRules.FailurePoint(AgentTaskStatus.Completed, AgentStopReason.Answered, [Step(1, AuditStatus.Succeeded), Step(2, AuditStatus.TimedOut)]));
        Assert.Equal(
            "Step 1 (Open an application) didn't work: the tool failed",
            AgentTaskRules.FailurePoint(AgentTaskStatus.Completed, AgentStopReason.Answered, [Step(1, AuditStatus.Failed, code: ToolErrors.Failed)]));
    }

    [Fact]
    public void AStepTheUserDidNotAllow_IsTheirChoiceAndNotAFailure_ButOneNobodyAnsweredIs()
    {
        Assert.Null(AgentTaskRules.FailurePoint(
            AgentTaskStatus.Completed, AgentStopReason.Answered, [Step(1, AuditStatus.Declined, confirmation: ConfirmationDecision.Declined)]));
        Assert.Equal(
            "Step 1 (Open an application) wasn't done: there was no answer in time",
            AgentTaskRules.FailurePoint(
                AgentTaskStatus.Completed, AgentStopReason.Answered, [Step(1, AuditStatus.Declined, confirmation: ConfirmationDecision.NoAnswer)]));
        Assert.Equal(
            "Step 1 (Open an application) wasn't done: you couldn't be asked",
            AgentTaskRules.FailurePoint(
                AgentTaskStatus.Completed, AgentStopReason.Answered, [Step(1, AuditStatus.Declined, confirmation: ConfirmationDecision.CouldNotAsk)]));
    }

    [Fact]
    public void ARunThatWentWell_HasNoFailurePoint_AndStepsThatWereNotRunDoNotCount()
    {
        Assert.Null(AgentTaskRules.FailurePoint(
            AgentTaskStatus.Completed, AgentStopReason.Answered, [Step(1, AuditStatus.Succeeded), Step(2, AuditStatus.Skipped, code: ToolErrors.Repeated)]));
        Assert.Null(AgentTaskRules.FailurePoint(AgentTaskStatus.Completed, AgentStopReason.Answered, []));
    }

    [Fact]
    public void ARunThatWasInterrupted_SaysWhere()
    {
        Assert.Equal(
            "Interrupted during step 1 (Open an application)",
            AgentTaskRules.FailurePoint(AgentTaskStatus.Interrupted, AgentStopReason.Answered, [Step(1, AuditStatus.Interrupted)]));
        Assert.Equal("Interrupted when the app closed", AgentTaskRules.FailurePoint(AgentTaskStatus.Interrupted, AgentStopReason.Answered, []));
    }

    // ---- when a run is worth a panel -------------------------------------------------------------------------------------

    private static AgentTaskSnapshot Task(AgentTaskStatus status, params AuditEntry[] steps) =>
        new() { Id = Guid.NewGuid(), StartedAt = At, Status = status, Steps = steps };

    [Fact]
    public void ARunWithOneStepThatWentWellIsNotWorthAPanel_ButTwoStepsAreOrAProblem()
    {
        Assert.False(Task(AgentTaskStatus.Completed, Step(1, AuditStatus.Succeeded)).IsWorthShowing);
        Assert.False(Task(AgentTaskStatus.Running, Step(1, AuditStatus.Running)).IsWorthShowing);
        Assert.False(Task(AgentTaskStatus.Completed, Step(1, AuditStatus.Declined, confirmation: ConfirmationDecision.Declined)).IsWorthShowing);
        Assert.False(Task(AgentTaskStatus.Completed, Step(1, AuditStatus.Succeeded), Step(2, AuditStatus.Skipped, code: ToolErrors.Repeated)).IsWorthShowing);

        Assert.True(Task(AgentTaskStatus.Running, Step(1, AuditStatus.Succeeded), Step(2, AuditStatus.Running)).IsWorthShowing);
        Assert.True(Task(AgentTaskStatus.Running, Step(1, AuditStatus.Failed, code: ToolErrors.Failed)).IsWorthShowing);
        Assert.True(Task(AgentTaskStatus.Running, Step(1, AuditStatus.TimedOut)).IsWorthShowing);
        Assert.True(Task(AgentTaskStatus.Cancelled, Step(1, AuditStatus.Succeeded)).IsWorthShowing);
        Assert.True(Task(AgentTaskStatus.Incomplete, Step(1, AuditStatus.Succeeded)).IsWorthShowing);
    }
}
