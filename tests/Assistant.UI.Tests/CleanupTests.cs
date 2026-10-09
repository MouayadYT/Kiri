using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.UI.History;
using Assistant.UI.Onboarding;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

// Cleanup: the chats from the bar are deleted after some hours and the chats of the full window after some days, each when its switch is on, with
// numbers the user types over, in Settings and in setup.
public sealed partial class PromptInputControlTests
{
    private static ConversationSummary ChatLastAddedTo(double hoursAgo) =>
        new(Guid.NewGuid(), "Chat", RetentionNow.AddHours(-hoursAgo - 1), RetentionNow.AddHours(-hoursAgo), 2);

    private static HistoryRetentionService Retention(DeletingHistory history, ConversationSurfaces surfaces) =>
        new(history, new InMemorySettings(new AppSettings()), new SteppingClock(RetentionNow), NullLogger<HistoryRetentionService>.Instance, surfaces);

    [Fact]
    public async Task ChatsFromTheBarGoAfterTheirHoursAndFullScreenChatsAfterTheirDays_EachOnlyWhileItsSwitchIsOn()
    {
        var history = new DeletingHistory();
        var (barOld, barNew) = (ChatLastAddedTo(11), ChatLastAddedTo(9));
        var (windowOld, windowNew) = (ChatLastAddedTo(8 * 24), ChatLastAddedTo(6 * 24));
        var unknown = ChatLastAddedTo(11); // From before the record was kept: it counts as a full screen chat.
        history.Summaries.AddRange([barOld, barNew, windowOld, windowNew, unknown]);
        var surfaces = new ConversationSurfaces((string?)null);
        surfaces.Note(barOld.Id, ConversationSurface.Bar);
        surfaces.Note(barNew.Id, ConversationSurface.Bar);
        surfaces.Note(windowOld.Id, ConversationSurface.Window);
        surfaces.Note(windowNew.Id, ConversationSurface.Window);
        using var service = Retention(history, surfaces);

        // With both switches off, as they are until the user turns one on, nothing is deleted.
        Assert.Equal(0, await service.PruneAsync(HistoryRetention.UntilDeleted, new CleanupSettings(), CancellationToken.None));
        Assert.Empty(history.Deleted);

        Assert.Equal(1, await service.PruneAsync(HistoryRetention.UntilDeleted, new CleanupSettings { DeleteBarChats = true }, CancellationToken.None));
        Assert.Equal([barOld.Id], history.Deleted);

        Assert.Equal(1, await service.PruneAsync(HistoryRetention.UntilDeleted, new CleanupSettings { DeleteBarChats = true, DeleteWindowChats = true }, CancellationToken.None));
        Assert.Equal([barOld.Id, windowOld.Id], history.Deleted);

        // What was deleted is forgotten in the record too; what was kept is still known.
        Assert.Null(surfaces.Of(barOld.Id));
        Assert.Equal(ConversationSurface.Bar, surfaces.Of(barNew.Id));
        Assert.Equal(ConversationSurface.Window, surfaces.Of(windowNew.Id));
    }

    [Fact]
    public async Task TheTimesAreTheOnesTheUserTyped_AndKeepHistoryForStillAppliesToEveryChat()
    {
        var history = new DeletingHistory();
        var (bar, window, old) = (ChatLastAddedTo(3), ChatLastAddedTo(3 * 24), ChatLastAddedTo(40 * 24));
        history.Summaries.AddRange([bar, window, old]);
        var surfaces = new ConversationSurfaces((string?)null);
        surfaces.Note(bar.Id, ConversationSurface.Bar);
        using var service = Retention(history, surfaces);

        // Ten hours and seven days would keep the first two; two hours and two days do not.
        Assert.Equal(0, await service.PruneAsync(HistoryRetention.UntilDeleted, new CleanupSettings { DeleteBarChats = true }, CancellationToken.None));
        var typed = new CleanupSettings { DeleteBarChats = true, BarChatHours = 2, DeleteWindowChats = true, WindowChatDays = 2 };
        Assert.Equal(3, await service.PruneAsync(HistoryRetention.UntilDeleted, typed, CancellationToken.None));

        // "Keep history for" deletes by itself, whatever Cleanup says.
        history.Deleted.Clear();
        Assert.Equal(1, await service.PruneAsync(HistoryRetention.ThirtyDays, new CleanupSettings(), CancellationToken.None));
        Assert.Equal([old.Id], history.Deleted);
    }

    [Fact]
    public async Task WhereAChatWasHadIsKeptAsIdsInOneFile_AndAChatOfTheFullWindowStaysOne()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kiri-surfaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "chat-surfaces.json");
            var (bar, moved) = (Guid.NewGuid(), Guid.NewGuid());
            var surfaces = new ConversationSurfaces(file);
            surfaces.Note(bar, ConversationSurface.Bar);
            surfaces.Note(moved, ConversationSurface.Bar);
            surfaces.Note(moved, ConversationSurface.Window);
            surfaces.Note(moved, ConversationSurface.Bar);
            await surfaces.Written;
            surfaces.Write();

            var again = new ConversationSurfaces(file);
            Assert.Equal(ConversationSurface.Bar, again.Of(bar));
            Assert.Equal(ConversationSurface.Window, again.Of(moved));
            Assert.Null(again.Of(Guid.NewGuid()));
            Assert.Equal(["chat-surfaces.json"], Directory.GetFiles(folder).Select(Path.GetFileName));

            // A record that cannot be read is begun again, and every chat then counts as a full screen chat.
            var broken = Path.Combine(folder, "broken.json");
            File.WriteAllText(broken, "{ not json");
            Assert.Null(new ConversationSurfaces(broken).Of(bar));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AChatAskedInTheBarIsTheBars_AndOneMovedToTheFullWindowOrAddedToThereIsTheWindows() => RunSta(() =>
    {
        var surfaces = new ConversationSurfaces((string?)null);
        var recorder = new StepRecorder();
        var floating = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), new ScriptedAnswers(), recorder: recorder, surfaces: surfaces);

        floating.StartNew("what is the capital of France?");
        Pump();

        Assert.NotEmpty(recorder.Recorded);
        Assert.Equal(ConversationSurface.Bar, surfaces.Of(floating.Id));

        // "Open in History window" moves it to the full window, and it is one of its chats from then on.
        var window = new HistoryViewModel(new SteppingClock(RetentionNow), answers: new ScriptedAnswers(), recorder: recorder, surfaces: surfaces);
        window.Open(floating.Id, floating.Messages, floating.UpdatedAt);
        Assert.Equal(ConversationSurface.Window, surfaces.Of(floating.Id));

        // A chat begun in the full window is one of its chats as soon as something is said in it.
        window.NewConversationCommand.Execute(null);
        Assert.True(window.Send("and of Spain?"));
        Pump();
        var begun = recorder.Recorded[^1].Conversation;
        Assert.NotEqual(floating.Id, begun);
        Assert.Equal(ConversationSurface.Window, surfaces.Of(begun));
    });

    [Fact]
    public void CleanupAndContextAreNotSectionsOfTheirOwn_TheyAreDrawnOnPrivacyAndModel_WithBothSwitchesOffAndTheTimesAskedFor() => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        var titles = kit.Model.Sections.Select(section => section.Title).ToList();
        Assert.DoesNotContain("Cleanup", titles);
        Assert.DoesNotContain("Context", titles);
        Assert.Contains("Privacy", titles);

        // Asked to open one of them, the window opens the page that holds it.
        var page = kit.Model.Cleanup;
        Assert.False(page.DeleteBarChats);
        Assert.False(page.DeleteWindowChats);
        Assert.Equal(("10", "7"), (page.BarChatHours.Text, page.WindowChatDays.Text));
    });

    [Fact]
    public void TheNumbersAreTypedOver_TheSwitchesTurnEachRuleOn_AndANumberThatCannotBeKeptIsNotSaved() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Cleanup;

        page.DeleteBarChats = true;
        page.BarChatHours.Text = "24";
        page.WindowChatDays.Text = "30";
        kit.Settle();
        Assert.Equal(new CleanupSettings { DeleteBarChats = true, BarChatHours = 24, WindowChatDays = 30 }, kit.Saved.Cleanup);

        page.BarChatHours.Text = "0";
        page.WindowChatDays.Text = "soon";
        kit.Settle();
        Assert.True(page.BarChatHours.HasError);
        Assert.True(page.WindowChatDays.HasError);
        Assert.Contains("1 to 8,760", page.BarChatHours.AdviceText, StringComparison.Ordinal);
        Assert.Equal((24, 30), (kit.Saved.Cleanup.BarChatHours, kit.Saved.Cleanup.WindowChatDays));

        page.WindowChatDays.Text = "14";
        page.DeleteWindowChats = true;
        page.DeleteBarChats = false;
        kit.Settle();
        Assert.Equal(new CleanupSettings { BarChatHours = 24, DeleteWindowChats = true, WindowChatDays = 14 }, kit.Saved.Cleanup);
    });

    [Fact]
    public void TheCleanupGroupOfThePrivacyPageIsDrawnWithItsTwoRowsTheirNumbersAndNoBindingErrors() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Privacy);
            Pump();
            Pump();

            var texts = AllTextOf(window).ToList();
            Assert.Contains("Cleanup", texts);
            Assert.Contains("Keep history for", texts);
            Assert.Contains("Delete all chats from the search bar after", texts);
            Assert.Contains("Delete all full screen chats after", texts);
            Assert.Contains("hours", texts);
            Assert.Contains("days", texts);
            var hours = Descendants<TextBox>(window).Single(box => AutomationName(box) == "Hours before chats from the search bar are deleted");
            var days = Descendants<TextBox>(window).Single(box => AutomationName(box) == "Days before full screen chats are deleted");
            var barSwitch = Descendants<CheckBox>(window).Single(box => AutomationName(box) == "Delete all chats from the search bar");
            var windowSwitch = Descendants<CheckBox>(window).Single(box => AutomationName(box) == "Delete all full screen chats");
            Assert.True(hours.IsVisible && days.IsVisible && barSwitch.IsVisible && windowSwitch.IsVisible);
            Assert.Equal(("10", "7"), (hours.Text, days.Text));
            Assert.Equal((false, false), (barSwitch.IsChecked, windowSwitch.IsChecked));

            // The first row is above the second, as asked.
            Assert.True(hours.TranslatePoint(new Point(0, 0), window).Y < days.TranslatePoint(new Point(0, 0), window).Y);
            RenderFixture(window, "settings-privacy.png", 2);

            // The number is clicked and typed over, and the switch is pressed.
            days.Text = "14";
            windowSwitch.IsChecked = true;
            Pump();
            kit.Settle();
            Assert.Equal(new CleanupSettings { DeleteWindowChats = true, WindowChatDays = 14 }, kit.Saved.Cleanup);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void SetupHasACleanupPageOfItsOwn_AndSavesWhatWasChosenThere() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        Assert.False(kit.Setup.DeleteBarChats);
        Assert.False(kit.Setup.DeleteWindowChats);
        Assert.Equal(("10", "7"), (kit.Setup.BarChatHours, kit.Setup.WindowChatDays));
        Assert.True(kit.Setup.CanContinueCleanup);

        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); Pump();
            window.ShowStep(OnboardingWindow.CleanupStepIndex); window.UpdateLayout(); Pump();

            // It comes after gaming and before the connections.
            Assert.Equal(OnboardingWindow.GamingStepIndex + 1, OnboardingWindow.CleanupStepIndex);
            Assert.Equal(OnboardingWindow.CleanupStepIndex + 1, OnboardingWindow.ConnectionsStepIndex);
            Assert.Equal("Cleanup", Named<TextBlock>(window, "Heading").Text);
            Assert.Equal("05  CLEANUP", Named<TextBlock>(window, "CleanupStep").Text);
            Assert.Equal("Next →", Named<Button>(window, "NextButton").Content);
            Assert.True(Named<Button>(window, "NextButton").IsEnabled);
            var page = Assert.Single(Descendants<CleanupSetupControl>(window));
            Assert.True(page.IsVisible);
            Assert.False(Assert.Single(Descendants<GameModeSetupControl>(window)).IsVisible);

            // The same two options as Settings, the bar's first, each with its number in the sentence.
            var bar = Named<CheckBox>(page, "BarChatsQuestion");
            var full = Named<CheckBox>(page, "WindowChatsQuestion");
            var hours = Named<TextBox>(page, "BarChatHoursField");
            var days = Named<TextBox>(page, "WindowChatDaysField");
            Assert.Equal(("Delete all chats from the search bar after", false), (bar.Content, bar.IsChecked));
            Assert.Equal(("Delete all full screen chats after", false), (full.Content, full.IsChecked));
            Assert.Equal(("10", "7"), (hours.Text, days.Text));
            Assert.True(bar.TranslatePoint(new Point(0, 0), page).Y < full.TranslatePoint(new Point(0, 0), page).Y);
            var said = Descendants<TextBlock>(page).Select(text => text.Text).ToList();
            Assert.Contains("You can change this anytime in Settings → Privacy.", said);
            Assert.DoesNotContain(said, text => text.Contains("gone for good", StringComparison.Ordinal) || text.Contains("quick questions", StringComparison.Ordinal));
            RenderFixture((FrameworkElement)window.Content, "onboarding-cleanup.png");

            bar.IsChecked = true;
            days.Text = "30";
            window.UpdateLayout(); Pump();
            Assert.True(kit.Setup.DeleteBarChats);
            Assert.Equal("30", kit.Setup.WindowChatDays);

            // A number that cannot be kept is said on the page, and the page cannot be left with it.
            hours.Text = "0";
            window.UpdateLayout(); Pump();
            Assert.False(Named<Button>(window, "NextButton").IsEnabled);
            Assert.True(Named<TextBlock>(page, "CleanupProblemText").IsVisible);
            Assert.Contains("1 to 8,760", kit.Setup.CleanupProblem, StringComparison.Ordinal);
            Assert.False(SettingsResult(kit.Setup.ApplyCleanupAsync()));
            Assert.Equal(new CleanupSettings(), kit.Settings.LoadAsync().Result.Cleanup);

            hours.Text = "12";
            window.UpdateLayout(); Pump();
            Assert.True(Named<Button>(window, "NextButton").IsEnabled);
            RenderFixture((FrameworkElement)window.Content, "onboarding-cleanup-on.png");
        }
        finally { window.Close(); }

        Assert.True(SettingsResult(kit.Setup.ApplyCleanupAsync()));
        Assert.Equal(new CleanupSettings { DeleteBarChats = true, BarChatHours = 12, WindowChatDays = 30 }, kit.Settings.LoadAsync().Result.Cleanup);
        Assert.Equal(string.Empty, kit.Setup.Status);

        // Opened again from Settings, setup shows what was saved.
        SettingsWait(kit.Settings.SaveAsync(kit.Settings.LoadAsync().Result with { Cleanup = new CleanupSettings { DeleteWindowChats = true, WindowChatDays = 3 } }));
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        Assert.Equal((false, "10", true, "3"), (kit.Setup.DeleteBarChats, kit.Setup.BarChatHours, kit.Setup.DeleteWindowChats, kit.Setup.WindowChatDays));
    }));
}