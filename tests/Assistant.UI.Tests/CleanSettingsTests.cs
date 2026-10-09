using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.UI.Controls;
using Assistant.UI.Onboarding;
using Assistant.UI.Settings;
using Xunit;

namespace Assistant.UI.Tests;

// The Settings pages and the setup screens with the small print taken out: what each says, the logs a page at a time, and the lists that open from a link.
public sealed partial class PromptInputControlTests
{
    // ---- the logs ------------------------------------------------------------------------------------------------------------------

    private static (SettingsKit Kit, FakeAuditHistory History) LogsKit(int count) =>
        ActivityKit([.. Enumerable.Range(1, count).Select(index => ThreeStepRun(ActivityNow.AddMinutes(-index)))]);

    [Fact]
    public void TheLogsAreListedTenToAPage_WithButtonsThatMoveBetweenThePages() => RunSta(() =>
    {
        var (kit, _) = LogsKit(25);
        var page = kit.Model.Activity;

        Assert.Equal(ActivityPage.PageSize, page.PageItems.Count);
        Assert.Equal((0, 3, true, "Page 1 of 3"), (page.PageIndex, page.PageCount, page.HasPages, page.PageText));
        Assert.False(page.PreviousPageCommand.CanExecute(null));
        Assert.Equal(page.Items.Take(10), page.PageItems);

        page.NextPageCommand.Execute(null);
        Assert.Equal(("Page 2 of 3", 10), (page.PageText, page.PageItems.Count));
        Assert.Equal(page.Items.Skip(10).Take(10), page.PageItems);

        page.NextPageCommand.Execute(null);
        Assert.Equal(("Page 3 of 3", 5), (page.PageText, page.PageItems.Count));
        Assert.False(page.NextPageCommand.CanExecute(null));

        page.PreviousPageCommand.Execute(null);
        Assert.Equal(("Page 2 of 3", 10), (page.PageText, page.PageItems.Count));
    });

    [Fact]
    public void ALogThatFitsOnOnePageHasNoPageButtons_AndAPageThatIsGoneIsLeftForTheLastOne() => RunSta(() =>
    {
        var (kit, history) = LogsKit(4);
        var page = kit.Model.Activity;
        Assert.Equal((false, 1, 4), (page.HasPages, page.PageCount, page.PageItems.Count));

        // On the last page when the log is cleared down to one page: the page that was open is not left empty.
        var (many, manyHistory) = LogsKit(23);
        many.Model.Activity.NextPageCommand.Execute(null);
        many.Model.Activity.NextPageCommand.Execute(null);
        Assert.Equal(3, many.Model.Activity.PageItems.Count);
        manyHistory.Items.RemoveRange(5, 18);
        many.Model.Activity.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal((0, 1, 5), (many.Model.Activity.PageIndex, many.Model.Activity.PageCount, many.Model.Activity.PageItems.Count));
        Assert.Empty(history.Cancelled);
    });

    [Fact]
    public void LogsAreDeletedAfterFortyEightHoursUntilTheUserTurnsThatOff_AndTheHoursAreTypedOver() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Activity;
        Assert.True(page.DeleteLogs);
        Assert.Equal("48", page.LogHours.Text);

        page.LogHours.Text = "24";
        kit.Settle();
        Assert.Equal(new CleanupSettings { LogHours = 24 }, kit.Saved.Cleanup);

        page.DeleteLogs = false;
        kit.Settle();
        Assert.False(kit.Saved.Cleanup.DeleteLogs);

        // An hour the Assistant cannot keep is said and not saved.
        page.LogHours.Text = "0";
        kit.Settle();
        Assert.True(page.LogHours.HasError);
        Assert.Equal(24, kit.Saved.Cleanup.LogHours);
    });

    [Fact]
    public void TheLogsPageIsDrawnWithThePageButtonsAndTheClearGroup_AndSaysNothingElse() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        using var errors = OfferBindingErrors.Listen();
        var (kit, _) = LogsKit(14);
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Activity);
            window.UpdateLayout();
            Pump();
            var page = Named<ContentControl>(window, "PageContent");
            var texts = AllTextOf(page).ToList();

            Assert.Contains("Logs", texts);
            Assert.Contains("Page 1 of 2", texts);
            Assert.True(ButtonsVisible(page, "Next"));
            Assert.True(ButtonsVisible(page, "Previous"));
            Assert.Equal(ActivityPage.PageSize, Descendants<Button>(page).Count(button => button.IsVisible && button.Content as string == "Show steps"));
            Assert.Contains("Clear logs", texts);
            Assert.Contains("Delete all logs after", texts);
            Assert.Equal("48", Descendants<TextBox>(page).Single(box => AutomationName(box) == "Hours before logs are deleted").Text);
            Assert.True(Descendants<CheckBox>(page).Single(box => AutomationName(box) == "Delete all logs").IsChecked);
            Assert.DoesNotContain(texts, text => text.Contains("never holds", StringComparison.Ordinal) || text.Contains("does not undo", StringComparison.Ordinal));
            RenderFixture(Named<Grid>(window, "Root"), "settings-logs.png", 2);

            Click(ButtonLabelled(page, "Next"));
            Pump();
            Assert.Contains("Page 2 of 2", AllTextOf(page));
            Assert.Equal(4, Descendants<Button>(page).Count(button => button.IsVisible && button.Content as string == "Show steps"));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    // ---- game mode, in Settings ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCreativeAppsHaveAListThatOpensFromALinkUnderTheirSwitch() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit(new AppSettings { GameMode = new GameModeSettings { CreativeApps = true } });
        var (window, _, _) = CreateSettingsWindow(kit);
        using var errors = BindingErrors.Listen();
        try
        {
            window.Show(); Pump(); window.UpdateLayout();
            var texts = Descendants<TextBlock>(window).Where(text => text.IsVisible).Select(text => text.Text).ToList();
            var link = Descendants<Button>(window).Single(button => AutomationName(button) == "View the full list of creative apps");
            var popup = Descendants<Popup>(window).Single(candidate => ReferenceEquals(candidate.PlacementTarget, link));

            // The page says what it does in a few words, and nothing under them.
            Assert.Contains("Pause AI when a game is running", texts);
            Assert.Contains("Pause AI when a creative app is running", texts);
            Assert.DoesNotContain(texts, text => text.Contains("graphics memory", StringComparison.Ordinal) || text.Contains("Premiere", StringComparison.Ordinal));
            Assert.Equal("Click here to view the full list", link.Content);
            Assert.False(popup.IsOpen);

            // The list is the apps' own names, in a small window over the page.
            Assert.Contains("Adobe Premiere Pro", kit.Model.General.CreativeAppNames);
            Assert.Contains("Blender", kit.Model.General.CreativeAppNames);
            Assert.Equal(kit.Model.General.CreativeAppNames.Distinct(), kit.Model.General.CreativeAppNames);
            link.Command!.Execute(null);
            Pump();
            Assert.True(popup.IsOpen);
            Assert.True(kit.Model.General.IsCreativeListOpen);
            popup.IsOpen = false;
            Assert.False(kit.Model.General.IsCreativeListOpen);
            Assert.Empty(errors.Messages);
        }
        finally { window.CloseForGood(); kit.Model.Dispose(); }
    }));

    // ---- the pages as a whole -----------------------------------------------------------------------------------------------------

    [Fact]
    public void NoPageOfSettingsCarriesSmallPrintUnderItsRows() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        using var errors = BindingErrors.Listen();
        var people = new Assistant.Core.People.InMemoryPersonStore();
        var kit = CreateSettingsKit(
            new AppSettings { Model = new ModelSettings { ContextLength = 20_000 } },
            existingFiles: [ProfileFile("chat-4b", "model.gguf")],
            people: people, personResolver: new Assistant.Core.People.PersonResolver(people), remembered: new Assistant.Core.Memory.InMemoryMemoryStore());
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            foreach (var section in kit.Model.Sections)
            {
                kit.Model.SelectedSection = section;
                window.UpdateLayout();
                Pump();

                // A row says what it is. What it is for is said, in a few words, only where there is something to allow (Permissions), and the model's own status
                // and the build's version are values and not sentences about the row.
                var described = Descendants<SettingsRow>(Named<ContentControl>(window, "PageContent"))
                    .Where(row => row.Description.Length > 0).ToList();
                var allowed = section.Section switch
                {
                    SettingsSection.Permissions => described.Count,
                    SettingsSection.Model => described.Count(row => row.Header is "Local model"),
                    SettingsSection.About => described.Count(row => row.Header is "Version"),
                    _ => 0,
                };
                Assert.True(described.Count == allowed, $"{section.Title} still describes {string.Join(", ", described.Select(row => row.Header))}.");

                // And not in a paragraph of its own either: no text block on the page runs on.
                var paragraphs = Descendants<TextBlock>(Named<ContentControl>(window, "PageContent"))
                    .Where(text => text.IsVisible && text.Text.Length > 140 && !text.Text.StartsWith("\\\\", StringComparison.Ordinal) && text.Text[1] != ':').ToList();
                Assert.True(paragraphs.Count == 0, $"{section.Title} has a paragraph: {paragraphs.FirstOrDefault()?.Text}");
            }
        }
        finally
        {
            window.CloseForGood();
        }

        Assert.Empty(errors.Messages);
    })));

    [Fact]
    public void TheAboutPageHasTheVersionAndTheTwoPlacesThingsAreKept_AndNothingAboutThisPc() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.About);
            window.UpdateLayout();
            Pump();
            var rows = Descendants<SettingsRow>(Named<ContentControl>(window, "PageContent")).Select(row => (string)row.Header).ToList();

            Assert.Equal(["Version", "Created by", "Data folder", "Settings file"], rows);
            Assert.DoesNotContain(AllTextOf(window), text => text.Contains("On this PC", StringComparison.Ordinal));
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void ThePermissionsPageStartsWithItsFirstGroupAndEachRowSaysWhatItIsInAFewWords() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Permissions);
            window.UpdateLayout();
            Pump();
            var texts = AllTextOf(Named<ContentControl>(window, "PageContent")).ToList();

            Assert.DoesNotContain(texts, text => text.Contains("never looks at anything on its own", StringComparison.Ordinal));
            Assert.Contains("Files", texts);
            Assert.Contains("Find your files and read the pictures you attach.", texts);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    // ---- setup ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryPageOfSetupHasItsHeadingAndNothingUnderIt_AndTheLastIsOnlyATick() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); Pump();
            (int Step, string Heading)[] steps =
            [
                (0, "Choose an AI model"), (1, "Choose a voice model"), (2, "Voice mode"), (OnboardingWindow.GamingStepIndex, "Game mode"),
                (OnboardingWindow.CleanupStepIndex, "Cleanup"), (OnboardingWindow.ConnectionsStepIndex, "Let's get you connected."),
                (OnboardingWindow.SearchStepIndex, "Search"), (OnboardingWindow.DoneStepIndex, "We're all set."),
            ];
            foreach (var (step, heading) in steps)
            {
                window.ShowStep(step); window.UpdateLayout(); Pump();
                Assert.Equal(heading, Named<TextBlock>(window, "Heading").Text);
                Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "Subtitle").Visibility);
                Assert.Equal(string.Empty, Named<TextBlock>(window, "Subtitle").Text);
            }

            // The last page: a tick, and no words under it.
            var done = Named<StackPanel>(window, "DonePage");
            Assert.True(done.IsVisible);
            Assert.DoesNotContain(Descendants<TextBlock>(done), text => text.Text.Length > 1);
            RenderFixture((FrameworkElement)window.Content, "onboarding-done-minimal.png");
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheSearchPageOfSetupSaysEnableWebSearchAndNothingUnderIt() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); Pump();
            window.ShowStep(OnboardingWindow.SearchStepIndex); window.UpdateLayout(); Pump();
            var page = Assert.Single(Descendants<SearchSetupControl>(window), control => control.IsVisible);
            var words = Descendants<TextBlock>(page).Where(text => text.IsVisible).Select(text => text.Text).ToList();

            Assert.Equal("Enable web search", Descendants<CheckBox>(page).First().Content);
            Assert.DoesNotContain(words, text => text.Contains("look up current information", StringComparison.Ordinal) || text.Contains("Search queries go to", StringComparison.Ordinal)
                || text.Contains("provided by HasData", StringComparison.Ordinal) || text.Contains("All three run online", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheVoiceModePageOffersAllowVoiceModeAndWhatHandyDetectedWithItsButtonsAndNothingElse() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        SettingsWait(kit.Setup.InitializeAsync(detectHardware: false));
        kit.Setup.EnableVoiceControl = true;
        kit.Setup.SelectedAsr = kit.Setup.AsrModels.Single(model => model.Id == "handy");
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); Pump();
            window.ShowStep(2); window.UpdateLayout(); Pump();
            var page = Assert.Single(Descendants<VoiceControlSetupControl>(window), control => control.IsVisible);
            var words = Descendants<TextBlock>(page).Where(text => text.IsVisible).Select(text => text.Text).ToList();

            Assert.Equal("Allow voice mode", Descendants<CheckBox>(page).First().Content);
            Assert.Contains(words, text => text.StartsWith("Handy ", StringComparison.Ordinal));
            Assert.DoesNotContain(words, text => text.Contains("No additional ASR", StringComparison.Ordinal) || text.Contains("Ctrl+V paste and turn off", StringComparison.Ordinal)
                || text.Contains("runs its selected model once", StringComparison.Ordinal) || text.Contains("Use the microphone button", StringComparison.Ordinal));
            var buttons = Descendants<Button>(page).Where(button => button.IsVisible).Select(button => button.Content as string).ToList();
            Assert.Contains("Refresh", buttons);
            Assert.Contains("Test speed", buttons);
            Assert.Contains(buttons, label => label is "Open Handy" or "Get Handy");
            RenderFixture((FrameworkElement)window.Content, "onboarding-voice-mode.png");
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheModelsAreOfferedWithTheirMemoryAndDownloadInTheWordsAsked() => RunSta(() =>
    {
        using var kit = new SetupKit();
        var model = kit.Setup.AiModels.First(choice => choice.Model.ParametersBillions > 0);

        // "~4.9 GB RAM to 8.0 GB when reading files", then the size of the download.
        Assert.Matches(@"^~\d+\.\d GB RAM to \d+\.\d GB when reading files$", model.Ram);
        Assert.EndsWith(" download", model.Size, StringComparison.Ordinal);
    });
}