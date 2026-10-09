using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Tools.Integrations;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The approval panel (PROJECT_SPEC section 4.8, step 108) ------------------------------------------------------

    private static IntegrationOffer OfferFor(IntegrationOfferKind kind = IntegrationOfferKind.Install, IntegrationOfferMaker maker = IntegrationOfferMaker.Official) => new()
    {
        OfferId = "offer-1",
        Kind = kind,
        AppName = "Todoist",
        IntegrationName = "@doist/todoist-mcp",
        Maker = maker,
        MakerText = "Official. Published under \u201CDoist\u201D, which belongs to Todoist's maker.",
        Provides = "Create a task. It lists a tool for this: \u201Cadd-tasks\u201D.",
        Source = "npm package \u201C@doist/todoist-mcp\u201D, version 13.4.0, from github.com/Doist/todoist-mcp.",
        Needs = ["An account with Todoist: it asks for a key (\u201CTODOIST_API_KEY\u201D).", "It does not need administrator rights."],
        Requirements = ["Node.js 24 is not set up for me yet, so I will download it too (about 36 MB).", "Nothing is run until you click Install, and then only to check that it starts."],
        Notes = ["It has scripts that npm would run when installing it. I do not run them, so it may not work."],
        CurrentVersion = kind == IntegrationOfferKind.Update ? "13.3.0" : null,
        NewVersion = "13.4.0",
    };

    private static InstallOutcome InstalledOutcome() => new() { Status = InstallStatus.Installed, Message = "Installed Todoist. It is listed in Settings > Integrations." };

    private static void WaitForTask(Task task, string failure = "The task did not finish.") =>
        WaitUntilFor(TimeSpan.FromSeconds(10), () => task.IsCompleted, failure);

    [Fact]
    public void TheApprovalPanelWaitsForTheUserAndNothingIsInstalledUntilInstallIsClicked() => RunSta(() =>
    {
        var installs = 0;
        var declines = 0;
        var panel = new IntegrationOfferContent(OfferFor(), (_, _) => { installs++; return Task.FromResult(InstalledOutcome()); }, () => declines++);

        Assert.Equal(IntegrationOfferState.Waiting, panel.State);
        Assert.True(panel.IsWaiting);
        Assert.False(panel.IsInstalling || panel.IsFinished);
        Assert.Equal("Install the Todoist integration?", panel.Title);
        Assert.Equal("Install", panel.InstallLabel);
        Assert.True(panel.IsOfficial);
        Assert.Equal("Official", panel.MakerBadge);
        Assert.True(panel.HasNotes);
        Assert.True(panel.InstallCommand.CanExecute(null));
        Assert.True(panel.CancelCommand.CanExecute(null));
        Assert.True(panel.IsWide);
        Assert.Equal((0, 0), (installs, declines));
    });

    [Fact]
    public void ClickingInstallInstallsShowsEachStepAndEndsInstalledWithAMessage() => RunSta(() =>
    {
        var seen = new List<string>();
        var release = new TaskCompletionSource();
        var panel = new IntegrationOfferContent(OfferFor(), async (progress, _) =>
        {
            progress.Report(new InstallProgress(InstallStep.Downloading, "Downloading the integration (1 of 4 MB)", 0.25));
            await release.Task;
            return InstalledOutcome();
        }, () => { });
        panel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IntegrationOfferContent.ProgressText)) seen.Add(panel.ProgressText); };

        panel.InstallCommand.Execute(null);

        Assert.Equal(IntegrationOfferState.Installing, panel.State);
        Assert.True(panel.IsInstalling);
        Assert.False(panel.InstallCommand.CanExecute(null));
        WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.ProgressText.StartsWith("Downloading", StringComparison.Ordinal), "The step was not shown.");
        Assert.Equal(0.25, panel.ProgressFraction);
        Assert.False(panel.IsIndeterminate);
        Assert.Equal(25, panel.ProgressPercent);

        release.SetResult();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.State == IntegrationOfferState.Installed, "It did not finish.");
        Assert.True(panel.IsInstalled);
        Assert.True(panel.IsFinished);
        Assert.Equal("Installed Todoist. It is listed in Settings > Integrations.", panel.ResultText);
        Assert.False(panel.InstallCommand.CanExecute(null));
        Assert.False(panel.CancelCommand.CanExecute(null));
    });

    [Fact]
    public void AnInstallationClickedTwiceRunsOnce() => RunSta(() =>
    {
        var installs = 0;
        var release = new TaskCompletionSource();
        var panel = new IntegrationOfferContent(OfferFor(), async (_, _) => { installs++; await release.Task; return InstalledOutcome(); }, () => { });

        panel.InstallCommand.Execute(null);
        panel.InstallCommand.Execute(null);
        var second = panel.InstallAsync();
        release.SetResult();
        WaitForTask(second);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.IsInstalled, "It did not finish.");

        Assert.Equal(1, installs);
    });

    [Fact]
    public void AFailedInstallationSaysWhyAndLeavesTheOfferFinished() => RunSta(() =>
    {
        var panel = new IntegrationOfferContent(
            OfferFor(), (_, _) => Task.FromResult(InstallOutcome.Fail(InstallFailure.HashMismatch, "What I downloaded is not what was reviewed, so I deleted it.")), () => { });

        var installing = panel.InstallAsync();
        WaitForTask(installing);

        Assert.Equal(IntegrationOfferState.Failed, panel.State);
        Assert.True(panel.IsProblem);
        Assert.True(panel.IsFinished);
        Assert.Equal("What I downloaded is not what was reviewed, so I deleted it.", panel.ResultText);
    });

    [Fact]
    public void AnInstallerThatThrowsIsAFailureThatSaysNothingInstalledAndNeverRepeatsTheError() => RunSta(() =>
    {
        var panel = new IntegrationOfferContent(OfferFor(), (_, _) => throw new InvalidOperationException("C:\\secret\\path blew up"), () => { });

        var installing = panel.InstallAsync();
        WaitForTask(installing);

        Assert.Equal(IntegrationOfferState.Failed, panel.State);
        Assert.Equal("Something went wrong, and nothing was installed.", panel.ResultText);
        Assert.DoesNotContain("secret", panel.ResultText, StringComparison.Ordinal);
    });

    [Fact]
    public void CancellingBeforeInstallingDeclinesTheOfferAndInstallsNothing() => RunSta(() =>
    {
        var installs = 0;
        var declines = 0;
        var panel = new IntegrationOfferContent(OfferFor(), (_, _) => { installs++; return Task.FromResult(InstalledOutcome()); }, () => declines++);

        panel.CancelCommand.Execute(null);

        Assert.Equal(IntegrationOfferState.Cancelled, panel.State);
        Assert.Equal("Cancelled. Nothing was downloaded or installed.", panel.ResultText);
        Assert.Equal((0, 1), (installs, declines));
        Assert.False(panel.InstallCommand.CanExecute(null));
        panel.CancelCommand.Execute(null);
        Assert.Equal(1, declines);
    });

    [Fact]
    public void CancellingWhileInstallingStopsTheInstallerAndEndsCancelled() => RunSta(() =>
    {
        var stopped = false;
        var panel = new IntegrationOfferContent(OfferFor(), async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
                return InstallOutcome.Cancel();
            }

            return InstalledOutcome();
        }, () => { });

        panel.InstallCommand.Execute(null);
        Assert.True(panel.CancelCommand.CanExecute(null));
        panel.CancelCommand.Execute(null);
        Assert.Equal("Cancelling", panel.ProgressText);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.State == IntegrationOfferState.Cancelled, "It did not stop.");

        Assert.True(stopped);
        Assert.Equal("Cancelled. Nothing was installed.", panel.ResultText);
    });

    [Fact]
    public void AnUpdateOfferSaysUpdateAndNamesTheVersion() => RunSta(() =>
    {
        var panel = new IntegrationOfferContent(OfferFor(IntegrationOfferKind.Update), (_, _) => Task.FromResult(InstalledOutcome()), () => { });

        Assert.Equal("Update Todoist to version 13.4.0?", panel.Title);
        Assert.Equal("Update", panel.InstallLabel);
    });

    [Theory]
    [InlineData(IntegrationOfferMaker.Official, "Official", true)]
    [InlineData(IntegrationOfferMaker.Community, "Community-made", false)]
    [InlineData(IntegrationOfferMaker.ClaimsOfficial, "Not confirmed official", false)]
    [InlineData(IntegrationOfferMaker.Unknown, "Maker unknown", false)]
    public void TheBadgeSaysWhoMadeItAndOnlyTheAppsOwnMakerIsOfficial(IntegrationOfferMaker maker, string badge, bool official) => RunSta(() =>
    {
        var panel = new IntegrationOfferContent(OfferFor(maker: maker), (_, _) => Task.FromResult(InstalledOutcome()), () => { });

        Assert.Equal(badge, panel.MakerBadge);
        Assert.Equal(official, panel.IsOfficial);
    });

    [Fact]
    public void ThePanelOpensSettingsOnlyWhenItCan() => RunSta(() =>
    {
        var opened = 0;
        var with = new IntegrationOfferContent(OfferFor(), (_, _) => Task.FromResult(InstalledOutcome()), () => { }, () => opened++);
        var without = new IntegrationOfferContent(OfferFor(), (_, _) => Task.FromResult(InstalledOutcome()), () => { });

        Assert.True(with.CanOpenSettings && with.OpenSettingsCommand.CanExecute(null));
        with.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, opened);
        Assert.False(without.CanOpenSettings || without.OpenSettingsCommand.CanExecute(null));
    });

    [Fact]
    public void ThePanelKeepsItsWordsOutOfToStringAndSaysEverythingForAssistiveTechnology() => RunSta(() =>
    {
        var panel = new IntegrationOfferContent(OfferFor(), (_, _) => Task.FromResult(InstalledOutcome()), () => { });

        Assert.Equal("IntegrationOfferContent", panel.ToString());
        Assert.Contains("Install the Todoist integration?", panel.Text, StringComparison.Ordinal);
        Assert.Contains("TODOIST_API_KEY", panel.Text, StringComparison.Ordinal);
        Assert.Contains("Node.js 24", panel.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void ANullOfferOrInstallerIsRefused() => RunSta(() =>
    {
        Assert.Throws<ArgumentNullException>(() => new IntegrationOfferContent(null!, (_, _) => Task.FromResult(InstalledOutcome()), () => { }));
        Assert.Throws<ArgumentNullException>(() => new IntegrationOfferContent(OfferFor(), null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => new IntegrationOfferContent(OfferFor(), (_, _) => Task.FromResult(InstalledOutcome()), null!));
    });

    // ---- How it is drawn -----------------------------------------------------------------------------------------------

    [Fact]
    public void ThePanelIsDrawnWithItsFactsAndItsButtonsAndTheButtonsFollowTheState() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var release = new TaskCompletionSource();
        var panel = new IntegrationOfferContent(OfferFor(), async (progress, _) =>
        {
            progress.Report(new InstallProgress(InstallStep.Checking, "Checking that it starts and offers what you asked for"));
            await release.Task;
            return InstalledOutcome();
        }, () => { }, () => { });
        var host = new ContentControl { Content = panel, Width = 520 };
        var window = new Window { Content = host, Width = 560, Height = 900, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();

            var texts = AllTextOf(host);
            Assert.Contains("Install the Todoist integration?", texts);
            Assert.Contains("Official", texts);
            Assert.Contains(texts, text => text.StartsWith("Official. Published under", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.StartsWith("Create a task.", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.StartsWith("npm package", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("TODOIST_API_KEY", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("Node.js 24", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("scripts that npm would run", StringComparison.Ordinal));
            RenderFixture(host, "integration-offer-waiting.png", 2);

            var install = ButtonLabelled(host, "Install");
            var cancel = ButtonLabelled(host, "Cancel");
            Assert.True(install.IsVisible && cancel.IsVisible);
            Assert.Equal("Install the Todoist integration?", AutomationPeer(install).GetName());

            Click(install);
            Pump();
            Assert.False(ButtonsVisible(host, "Install"));
            Assert.Contains(AllTextOf(host), text => text.StartsWith("Checking that it starts", StringComparison.Ordinal));
            Assert.NotNull(Descendants<ProgressBar>(host).FirstOrDefault(bar => bar.IsVisible));
            Assert.True(ButtonsVisible(host, "Cancel"));
            RenderFixture(host, "integration-offer-installing.png", 2);

            release.SetResult();
            WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.IsInstalled, "It did not finish.");
            Pump();
            Assert.Contains(AllTextOf(host), text => text.StartsWith("Installed Todoist.", StringComparison.Ordinal));
            Assert.True(ButtonLabelled(host, "Open Settings").IsVisible);
            Assert.False(ButtonsVisible(host, "Install") || ButtonsVisible(host, "Cancel"));
            RenderFixture(host, "integration-offer-installed.png", 2);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void ACancelledOrFailedPanelShowsItsMessageAndNoOpenSettingsButton() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        var panel = new IntegrationOfferContent(OfferFor(), (_, _) => Task.FromResult(InstallOutcome.Fail(InstallFailure.DownloadFailed, "I could not download it. Check your internet connection.")), () => { }, () => { });
        var host = new ContentControl { Content = panel, Width = 520 };
        var window = new Window { Content = host, Width = 560, Height = 900, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump();
            Click(ButtonLabelled(host, "Install"));
            WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.IsProblem, "It did not fail.");
            Pump();

            Assert.Contains(AllTextOf(host), text => text.StartsWith("I could not download it.", StringComparison.Ordinal));
            Assert.False(ButtonsVisible(host, "Open Settings"));
            RenderFixture(host, "integration-offer-failed.png", 2);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    private static IEnumerable<string> AllTextOf(DependencyObject root) =>
        Descendants<TextBlock>(root).Where(block => block.IsVisible).Select(block => block.Text);

    private static Button ButtonLabelled(DependencyObject root, string label) =>
        Descendants<Button>(root).First(button => button.Content as string == label);

    private static bool ButtonsVisible(DependencyObject root, string label) =>
        Descendants<Button>(root).Any(button => button.Content as string == label && button.IsVisible);

    private static ButtonAutomationPeer AutomationPeer(Button button) => new(button);

    // Collects the data binding errors WPF traces while it runs, so a template that binds to something that is not there fails a test.
    private sealed class OfferBindingErrors : TraceListener
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public static OfferBindingErrors Listen()
        {
            var listener = new OfferBindingErrors();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            return listener;
        }

        public override void Write(string? message) => _messages.Add(message ?? string.Empty);

        public override void WriteLine(string? message) => _messages.Add(message ?? string.Empty);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            }

            base.Dispose(disposing);
        }
    }

    // ---- In the conversation --------------------------------------------------------------------------------------------

    private sealed class OfferOrchestrator(IntegrationOffer? offer) : IAssistantOrchestrator
    {
        public async IAsyncEnumerable<AssistantResponseChunk> AskAsync(
            ConversationSession session, string prompt, IReadOnlyList<ContextItem>? contextItems = null, string? instructions = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return AssistantResponseChunk.ForTextDelta("I found one.");
            if (offer is not null)
            {
                yield return AssistantResponseChunk.ForIntegrationOffer(offer);
            }
        }
    }

    private sealed class RecordingBroker : IIntegrationOffers
    {
        public List<string> Accepted { get; } = [];

        public List<string> Declined { get; } = [];

        public Task<IntegrationOffer> OfferAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IntegrationOffer> OfferConnectAsync(KnownEndpoint endpoint, InstalledIntegration? existing, IntegrationCapability? capability, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IntegrationOffer> OfferUpdateAsync(InstallCandidate candidate, InstalledIntegration current, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Accepted.Add(offerId);
            return Task.FromResult(InstalledOutcome());
        }

        public void Decline(string offerId) => Declined.Add(offerId);
    }

    private sealed class RecordingSettingsOpener : ISettingsLauncher
    {
        public List<SettingsSection> Sections { get; } = [];

        public void Show() => Sections.Add(SettingsSection.General);

        public void Show(SettingsSection section) => Sections.Add(section);
    }

    [Fact]
    public void AnOfferInTheAnswerBecomesAnApprovalPanelAfterTheAssistantsWordsAndInstallingItAcceptsThatOffer() => RunSta(() =>
    {
        var broker = new RecordingBroker();
        var opener = new RecordingSettingsOpener();
        var answers = new ModelAnswerProvider(new OfferOrchestrator(OfferFor()), new FixedClock(Now), offers: broker, settings: opener);
        var shown = new List<MessageViewModel>();

        var answering = answers.StreamAnswerAsync("Add milk to Todoist", shown.Add, CancellationToken.None);
        WaitForTask(answering);

        var message = Assert.Single(shown);
        Assert.Equal([typeof(TextContent), typeof(IntegrationOfferContent)], message.Content.Select(part => part.GetType()));
        var panel = (IntegrationOfferContent)message.Content[1];
        Assert.Equal(IntegrationOfferState.Waiting, panel.State);
        Assert.Empty(broker.Accepted);

        panel.InstallCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => panel.IsInstalled, "It did not finish.");
        Assert.Equal(["offer-1"], broker.Accepted);
        panel.OpenSettingsCommand.Execute(null);
        Assert.Equal([SettingsSection.Integrations], opener.Sections);
    });

    [Fact]
    public void DecliningThePanelTellsTheBrokerAndAnOfferWithNoWayToInstallIsNotShownAtAll() => RunSta(() =>
    {
        var broker = new RecordingBroker();
        var shown = new List<MessageViewModel>();
        var withBroker = new ModelAnswerProvider(new OfferOrchestrator(OfferFor()), new FixedClock(Now), offers: broker);
        WaitForTask(withBroker.StreamAnswerAsync("Add milk to Todoist", shown.Add, CancellationToken.None));
        ((IntegrationOfferContent)shown[0].Content[1]).CancelCommand.Execute(null);
        Assert.Equal(["offer-1"], broker.Declined);

        var without = new ModelAnswerProvider(new OfferOrchestrator(OfferFor()), new FixedClock(Now));
        var plain = new List<MessageViewModel>();
        WaitForTask(without.StreamAnswerAsync("Add milk to Todoist", plain.Add, CancellationToken.None));
        Assert.Equal([typeof(TextContent)], plain[0].Content.Select(part => part.GetType()));
    });

    [Fact]
    public void AnApprovalPanelIsNotSavedWithTheAnswerSoOnlyItsWordsComeBackFromTheHistory() => RunSta(() =>
    {
        var broker = new RecordingBroker();
        var answers = new ModelAnswerProvider(new OfferOrchestrator(OfferFor()), new FixedClock(Now), offers: broker);
        var shown = new List<MessageViewModel>();
        WaitForTask(answers.StreamAnswerAsync("Add milk to Todoist", shown.Add, CancellationToken.None));

        var saved = new Assistant.UI.History.MessageMapper(new FakeClipboard()).ToDomain(shown[0]);

        Assert.Equal("I found one.", saved.Text);
        Assert.Empty(saved.Cards);
    });
}
