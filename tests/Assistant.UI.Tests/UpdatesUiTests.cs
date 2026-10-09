using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Updates;
using Assistant.UI.Controls;
using Assistant.UI.Settings;
using Assistant.UI.Updates;
using Xunit;

namespace Assistant.UI.Tests;

// The update check as the user meets it: About names who made the Assistant and has a Check for updates button that says what it found, and a
// release found while the app runs (as it starts, or when the bar or the full window is opened) is offered once, in a small window, to open or to ignore.
public sealed partial class PromptInputControlTests
{
    private sealed class FakeUpdates : IUpdateChecker
    {
        public string CurrentVersion => "0.1.148";

        public event EventHandler<AvailableUpdate>? UpdateAvailable;

        public UpdateCheckResult Next { get; set; } = new(null);

        public TaskCompletionSource? Gate { get; set; }

        public int Triggers { get; private set; }

        public int Checks { get; private set; }

        public List<string> Dismissed { get; } = [];

        public void TriggerCheckIfDue() => Triggers++;

        public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return Next;
        }

        public Task DismissAsync(string version, CancellationToken cancellationToken = default)
        {
            Dismissed.Add(version);
            return Task.CompletedTask;
        }

        public void Raise(AvailableUpdate update) => UpdateAvailable?.Invoke(this, update);
    }

    private static readonly AvailableUpdate NewRelease = new("0.1.148", "v0.2.0", "https://github.com/MouayadYT/Kiri/releases/tag/v0.2.0");

    [Fact]
    public void AboutNamesWhoMadeIt_AndEachNameOpensTheirPage() => RunSta(() => WithTheme(() =>
    {
        using var errors = BindingErrors.Listen();
        var kit = CreateSettingsKit(updates: new FakeUpdates());
        var opened = new List<string>();
        kit.Model.About.OpenPage = opened.Add;
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.About);
            window.UpdateLayout();
            Pump();

            var page = Named<ContentControl>(window, "PageContent");
            var created = Descendants<SettingsRow>(page).Single(row => (string)row.Header == "Created by");
            var names = Descendants<Button>(created).Where(button => button.IsVisible).ToList();
            Assert.Equal(["MouayadYT", "MuhannadYT"], names.Select(button => (string)button.Content));

            foreach (var name in names)
            {
                Click(name);
                Pump();
            }

            Assert.Equal(["https://github.com/MouayadYT", "https://github.com/MuhannadYT"], opened);
            Assert.Equal("https://github.com/MouayadYT/Kiri", kit.Model.About.ProjectUrl);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            kit.Model.Dispose();
        }
    }));

    [Fact]
    public void CheckForUpdates_SaysWhatItFound_AndOffersTheNewReleasesPage() => RunSta(() => WithTheme(() =>
    {
        using var errors = BindingErrors.Listen();
        var updates = new FakeUpdates();
        var kit = CreateSettingsKit(updates: updates);
        var about = kit.Model.About;
        var opened = new List<string>();
        about.OpenPage = opened.Add;
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.About);
            window.UpdateLayout();
            Pump();
            var page = Named<ContentControl>(window, "PageContent");
            var check = Descendants<Button>(page).Single(button => AutomationName(button) == "Check for updates");
            Assert.True(check.IsVisible);
            Assert.False(about.HasUpdateStatus);

            // While it asks, it says so and cannot be pressed again.
            updates.Gate = new TaskCompletionSource();
            Click(check);
            Pump();
            Assert.Equal(AboutPage.CheckingText, about.UpdateStatus);
            Assert.False(about.CheckForUpdatesCommand.CanExecute(null));
            updates.Gate.SetResult();
            SettingsWait(about.Checking);
            Assert.Equal(AboutPage.UpToDateText, about.UpdateStatus);
            Assert.False(about.HasUpdate);
            Assert.True(about.CheckForUpdatesCommand.CanExecute(null));

            updates.Gate = null;
            updates.Next = new UpdateCheckResult(null, Failed: true);
            about.CheckForUpdatesCommand.Execute(null);
            SettingsWait(about.Checking);
            Assert.Equal(AboutPage.FailedText, about.UpdateStatus);

            updates.Next = new UpdateCheckResult(NewRelease);
            about.CheckForUpdatesCommand.Execute(null);
            SettingsWait(about.Checking);
            window.UpdateLayout();
            Pump();
            Assert.Equal("Version 0.2.0 is available.", about.UpdateStatus);
            Assert.True(about.HasUpdate);
            var open = Descendants<Button>(page).Single(button => AutomationName(button) == "Open the update's page on GitHub");
            Assert.True(open.IsVisible);
            Click(open);
            Pump();
            Assert.Equal([NewRelease.ReleaseUrl], opened);
            Assert.Equal(3, updates.Checks);
            Assert.Equal(0, updates.Triggers);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            kit.Model.Dispose();
        }
    }));

    [Fact]
    public void WithoutAnUpdateChecker_AboutHasNoButtonForIt() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit();
        Assert.False(kit.Model.About.CanCheckForUpdates);
        Assert.False(kit.Model.About.CheckForUpdatesCommand.CanExecute(null));
        kit.Model.Dispose();
    }));

    [Fact]
    public void AReleaseFoundWhileTheAppRuns_IsOfferedOnce_AndIgnoringItIsKept() => RunSta(() =>
    {
        var updates = new FakeUpdates();
        using var notifier = new UpdateNotifier(updates);
        var asked = new List<AvailableUpdate>();
        var opened = new List<string>();
        var answer = false;
        notifier.Ask = update =>
        {
            asked.Add(update);
            return answer;
        };
        notifier.OpenPage = opened.Add;

        // The app starting, or the user opening it, is what starts a check that is due; there is no timer.
        notifier.CheckIfDue();
        Assert.Equal(1, updates.Triggers);

        // What the check finds (on a background thread) is shown on the window's thread.
        var raised = Task.Run(() => updates.Raise(NewRelease));
        raised.Wait();
        Pump();
        Assert.Equal([NewRelease], asked);
        Assert.Equal(["v0.2.0"], updates.Dismissed);
        Assert.Empty(opened);

        answer = true;
        notifier.Show(NewRelease);
        Assert.Equal([NewRelease.ReleaseUrl], opened);
        Assert.Single(updates.Dismissed);
    });

    [Fact]
    public void TheUpdateWindowSaysWhichVersion_AndWhetherItsPageWasChosen() => RunSta(() => WithTheme(() =>
    {
        using var errors = BindingErrors.Listen();
        foreach (var choose in new[] { true, false })
        {
            var window = new UpdateAvailableWindow(NewRelease)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false,
            };
            window.Show();
            Pump();
            var texts = Descendants<TextBlock>(window).Where(text => text.IsVisible).Select(text => text.Text).ToList();
            Assert.Contains("Kiri 0.2.0 is available", texts);
            Assert.Equal("Open update page", window.OpenChoice.Content);
            Assert.Equal("Ignore this version", window.IgnoreChoice.Content);

            Click(choose ? window.OpenChoice : window.IgnoreChoice);
            Pump();
            Assert.False(window.IsVisible);
            Assert.Equal(choose, window.Opened);
        }

        Assert.Empty(errors.Messages);
    }));
}
