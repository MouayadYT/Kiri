using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Events;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// <c>demo task</c> and <c>demo task fail</c> (PROJECT_SPEC §4.8, step 117): a made-up task with several steps, through the real loop, panel, question and activity log, for the user to try:
/// the panel shows each step, the button stops the task, the question comes before the step that changes something, and the task that cannot go on says where.
/// </summary>
public sealed partial class PromptInputControlTests
{
    private static (DemoAnswerProvider Demo, AuditLog Log) TaskDemo(TimeSpan? slowStep = null)
    {
        var bus = NewEventBus();
        var log = NewTaskLog();
        var broker = new ConfirmationBroker(bus, TimeProvider.System, NullLogger<ConfirmationBroker>.Instance, TimeSpan.FromSeconds(30));
        var demo = new AgentTaskDemo(bus, log, broker, new InMemorySettingsService(), new ImagePreprocessor(), TimeProvider.System, slowStep: slowStep ?? TimeSpan.FromMilliseconds(50));
        return (new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System, taskDemo: demo), log);
    }

    [Fact]
    public void TheTaskDemoShowsAPanelWithItsThreeSteps_AsksBeforeTheStepThatChangesSomething_AndIsListedAfterwards() => RunSta(() =>
    {
        var (demo, log) = TaskDemo();
        var shown = new List<MessageViewModel>();

        var run = demo.StreamAnswerAsync(Guid.NewGuid(), "demo task", shown.Add, CancellationToken.None);
        WaitUntilFor(
            TimeSpan.FromSeconds(10),
            () => shown.Count == 1 && shown[0].Content.OfType<ToolConfirmationContent>().Any() && shown[0].Content.OfType<AgentTaskContent>().Any(),
            "The panel and the question did not appear.");
        var answer = shown[0];
        var panel = answer.Content.OfType<AgentTaskContent>().Single();
        var question = answer.Content.OfType<ToolConfirmationContent>().Single();

        Assert.Equal("Open Notepad?", question.Title);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => panel.Steps.Count == 3 && panel.Steps[2].IsWaiting, "The panel did not show the step waiting for the user.");
        Assert.Equal(["Search your files", "Read a file", "Open an application"], panel.Steps.Select(step => step.Summary));
        Assert.Equal(["Done", "Done", "Waiting for you"], panel.Steps.Select(step => step.StatusText));

        WaitUntilFor(TimeSpan.FromSeconds(10), () => question.IsArmed, "The button was never armed.");
        question.ApproveCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => run.IsCompleted, "The demo did not finish.");

        Assert.Equal("Done", panel.Title);
        Assert.Equal("You allowed it", panel.Steps[2].ConfirmationText);
        Assert.Equal(MessageStatus.Complete, answer.Status);
        Assert.Contains(answer.Content.OfType<TextContent>(), text => text.Text.Contains("all three steps done", StringComparison.Ordinal));

        var listed = Assert.Single(log.ListAsync(10).GetAwaiter().GetResult()).Task!;
        Assert.Equal(AgentTaskStatus.Completed, listed.Status);
        Assert.Equal(3, listed.StepCount);
    });

    [Fact]
    public void ThePanelsCancelButtonStopsTheTaskDemoWhileItsSlowStepWorks() => RunSta(() =>
    {
        var (demo, log) = TaskDemo(TimeSpan.FromMinutes(1));
        var shown = new List<MessageViewModel>();

        var run = demo.StreamAnswerAsync(Guid.NewGuid(), "demo task", shown.Add, CancellationToken.None);
        WaitUntilFor(
            TimeSpan.FromSeconds(10), () => shown.Count == 1 && shown[0].Content.OfType<AgentTaskContent>().Any(), "The panel did not appear.");
        var panel = shown[0].Content.OfType<AgentTaskContent>().Single();
        WaitUntilFor(TimeSpan.FromSeconds(10), () => panel.Steps.Count == 2 && panel.Steps[1].IsWorking, "The slow step did not start.");

        panel.CancelCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(10), () => run.IsCompleted, "The demo did not stop.");

        Assert.Equal(MessageStatus.Stopped, shown[0].Status);
        Assert.Equal("Stopped", panel.Title);
        Assert.Equal("Stopped during step 2 (Read a file)", panel.FailurePoint);
        Assert.Equal(AgentTaskStatus.Cancelled, Assert.Single(log.ListAsync(10).GetAwaiter().GetResult()).Task!.Status);
    });

    [Fact]
    public void TheFailingTaskDemoStopsAtTheStepThatDidNotWork_AndSaysWhere() => RunSta(() =>
    {
        var (demo, log) = TaskDemo();
        var shown = new List<MessageViewModel>();

        var run = demo.StreamAnswerAsync(Guid.NewGuid(), "demo task fail", shown.Add, CancellationToken.None);
        WaitUntilFor(TimeSpan.FromSeconds(15), () => run.IsCompleted, "The demo did not finish.");

        var panel = Assert.Single(shown.SelectMany(answer => answer.Content.OfType<AgentTaskContent>()));
        Assert.Equal("Step 2 (Read a file) didn't work: the tool failed", panel.FailurePoint);
        Assert.Equal(["Done", "Didn't work: the tool failed"], panel.Steps.Select(step => step.StatusText));
        Assert.Empty(shown[0].Content.OfType<ToolConfirmationContent>());
        Assert.Contains(shown[0].Content.OfType<TextContent>(), text => text.Text.Contains("did not open the application", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(log.ListAsync(10).GetAwaiter().GetResult()).Task!.StepCount);
    });

    [Fact]
    public void TheTaskDemoIsListed_AndWithoutItsPartsItSaysItIsNotThere() => RunSta(() =>
    {
        var listed = new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System).Answer("demo");
        var unavailable = new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System).Answer("demo task");

        Assert.Contains("demo task", listed!.Text, StringComparison.Ordinal);
        Assert.Contains("demo task fail", listed.Text, StringComparison.Ordinal);
        Assert.Equal("The task demo is not available here.", unavailable!.Text);
        Assert.Equal("The task demo is not available here.", new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System).Answer("demo task fail")!.Text);
    });

    [Fact]
    public void AsInstalled_TheSamplesAreOff_SoDemoIsAnOrdinaryQuestion_AndTheBarShowsNoMadeUpResults() => RunSta(() =>
    {
        // The UI tests turn the samples on for themselves (DemosOn); the app turns them on only for ASSISTANT_DEMOS=1.
        Assert.True(DemoAnswerProvider.DemosRequested);
        Assert.Equal("ASSISTANT_DEMOS", DemoAnswerProvider.DemosVariable);

        var off = new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System) { DemosEnabled = false };
        foreach (var question in new[] { "demo", "demo task", "Demo confirm", "demo integration", "demo reminder" })
        {
            Assert.Null(off.Answer(question));
        }

        Assert.NotNull(new DemoAnswerProvider(new FakeClipboard(), TimeProvider.System).Answer("demo"));

        // The bar's made-up message results for "demo..." are not shown either.
        Assert.Empty(new PlaceholderSearchResults(includeBraveSample: false, includeDemoSample: false).Search("demo").SelectMany(section => section.Items));
        Assert.NotEmpty(new PlaceholderSearchResults().Search("demo").SelectMany(section => section.Items));
    });
}
