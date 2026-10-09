using System.IO;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;
using Assistant.Windows.Selection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The Permissions page (PROJECT_SPEC §4.9): what the user allows the Assistant to use --------------------------

    private static readonly PermissionCapability[] EveryPermission = Enum.GetValues<PermissionCapability>();

    [Fact]
    public void ThePermissionsPageListsEveryCapabilityInThreeGroups() => RunSta(() =>
    {
        var page = CreateSettingsKit().Model.Permissions;

        Assert.Equal(["On this PC", "Other apps and the web", "Actions"], page.Groups.Select(group => group.Title));
        Assert.Equal(
            [
                [
                    PermissionCapability.Files, PermissionCapability.ScreenCapture, PermissionCapability.SelectedText,
                    PermissionCapability.SelectedTextByCopy, PermissionCapability.ClipboardHistory,
                ],
                [PermissionCapability.Calendar, PermissionCapability.Messaging, PermissionCapability.ExternalSearch],
                [PermissionCapability.DestructiveActions],
            ],
            page.Groups.Select(group => group.Items.Select(item => item.Capability).ToArray()));
        // Each capability is listed once, so none can be missing from the page.
        Assert.Equal(EveryPermission.Order(), page.Items.Select(item => item.Capability).Order());
        Assert.Equal(PermissionCatalog.All.Select(definition => definition.Title), page.Items.Select(item => item.Title));
        Assert.Equal(PermissionCatalog.All.Select(definition => definition.Summary), page.Items.Select(item => item.Summary));
        // The first row of each group has no line above it.
        Assert.Equal([false, true, true, true, true, false, true, true, false], page.Items.Select(item => item.ShowsDivider));
        // Each row says what it is in a few words, and nothing is said over the groups.
        Assert.All(page.Items, item => Assert.InRange(item.Summary.Split(' ').Length, 2, 10));
    });

    [Fact]
    public void EveryCapabilityTheAssistantCanDoCanBeChangedAndOnlyDestructiveActionsSaysItNeverCan() => RunSta(() =>
    {
        var page = CreateSettingsKit().Model.Permissions;

        Assert.Equal(
            [
                SettingStatus.Available, SettingStatus.Available, SettingStatus.Available, SettingStatus.Available,
                SettingStatus.Available, SettingStatus.Available, SettingStatus.Available, SettingStatus.Available,
                SettingStatus.AlwaysOff,
            ],
            page.Items.Select(item => item.Status));
        Assert.Equal([true, true, true, true, true, true, true, true, false], page.Items.Select(item => item.IsAvailable));
        // Nothing but Files, Screen Capture and Selected Text is on, before anything is saved: what sends something away, what acts on
        // another app and the clipboard (Selected Text by Copy), and what keeps what is copied (Clipboard History), start off.
        Assert.Equal([true, true, true, false, false, false, false, false, false], page.Items.Select(item => item.IsOn));
        Assert.Same(page.Items[0], page[PermissionCapability.Files]);
    });

    [Fact]
    public void SelectedTextByCopyStartsOffAndIsSavedAtOnceWhenTheUserAllowsIt() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var policy = new SettingsPermissionPolicy(kit.Settings);
        Assert.False(kit.Model.Permissions[PermissionCapability.SelectedTextByCopy].IsOn);
        Assert.Equal(PermissionDecisionReason.TurnedOff, policy.CheckAsync(PermissionCapability.SelectedTextByCopy).GetAwaiter().GetResult().Reason);

        kit.Model.Permissions[PermissionCapability.SelectedTextByCopy].IsOn = true;
        kit.Settle();

        Assert.True(kit.Saved.Permissions.SelectedTextByCopy);
        Assert.True(kit.Saved.Permissions.SelectedText);
        Assert.True(policy.CheckAsync(PermissionCapability.SelectedTextByCopy).GetAwaiter().GetResult().IsAllowed);
    });

    [Fact]
    public void TurningFilesOffIsSavedAtOnceAndOnlyThatSettingChanges() => RunSta(() =>
    {
        var saved = new AppSettings { Privacy = new PrivacySettings { HistoryEnabled = false } };
        var kit = CreateSettingsKit(saved);
        var policy = new SettingsPermissionPolicy(kit.Settings);

        kit.Model.Permissions[PermissionCapability.Files].IsOn = false;
        kit.Settle();

        Assert.Equal(saved with { Permissions = saved.Permissions with { Files = false } }, kit.Saved);
        Assert.False(kit.Model.HasNotice);
        Assert.False(kit.Model.Permissions[PermissionCapability.Files].IsOn);
        // What the page shows is what holds: the next check of the capability is refused.
        Assert.Equal(PermissionDecisionReason.TurnedOff, policy.CheckAsync(PermissionCapability.Files).GetAwaiter().GetResult().Reason);

        kit.Model.Permissions[PermissionCapability.Files].IsOn = true;
        kit.Settle();

        Assert.True(kit.Saved.Permissions.Files);
        Assert.True(policy.CheckAsync(PermissionCapability.Files).GetAwaiter().GetResult().IsAllowed);
    });

    [Fact]
    public void ASwitchForSomethingTheAssistantCannotDoDoesNotMoveAndNothingIsSaved() => RunSta(() =>
    {
        var settings = new RecordingSettings();
        var kit = CreateSettingsKit(service: settings);
        var page = kit.Model.Permissions;
        var savesBefore = settings.Saves;
        var changed = new List<string?>();

        foreach (var item in page.Items.Where(item => !item.IsAvailable))
        {
            item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            item.IsOn = true;
        }

        kit.Settle();

        Assert.Equal(savesBefore, settings.Saves);
        Assert.All(page.Items.Where(item => !item.IsAvailable), item => Assert.False(item.IsOn));
        // Each refused write was announced, so a switch that had moved on screen reads its real position again.
        Assert.Equal(page.Items.Count(item => !item.IsAvailable), changed.Count(name => name == nameof(PermissionItem.IsOn)));
        Assert.Equal(new PermissionSettings(), kit.Saved.Permissions);
    });

    // ---- Off, ask every time, allowed (step 119) -------------------------------------------------------------------------

    private static readonly PermissionCapability[] Askable =
    [
        PermissionCapability.ScreenCapture, PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy,
        PermissionCapability.Calendar, PermissionCapability.Messaging, PermissionCapability.ExternalSearch,
    ];

    [Fact]
    public void ARowThatCanAskOffersThreeChoicesAndTheOthersOfferNoneAndAreASwitch() => RunSta(() =>
    {
        var page = CreateSettingsKit().Model.Permissions;

        foreach (var item in page.Items)
        {
            Assert.Equal(Askable.Contains(item.Capability), item.SupportsAskEveryTime);
            Assert.Equal(!Askable.Contains(item.Capability), item.ShowsSwitch);
            Assert.Equal(item.SupportsAskEveryTime ? new[] { "Off", "Ask every time", "Allowed" } : Array.Empty<string>(), item.Choices.Select(choice => choice.Label));
        }
    });

    [Fact]
    public void ChoosingAskEveryTimeIsSavedAtOnceAndTheNextCheckSaysItMustBeAskedAbout() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var policy = new SettingsPermissionPolicy(kit.Settings);
        var screen = kit.Model.Permissions[PermissionCapability.ScreenCapture];

        screen.SelectedChoice = screen.Choices[1];
        kit.Settle();

        Assert.Equal(PermissionMode.AskEveryTime, screen.Mode);
        Assert.False(screen.IsOn);
        Assert.Equal(PermissionMode.AskEveryTime, kit.Saved.Permissions.ModeOf(PermissionCapability.ScreenCapture));
        Assert.Equal(PermissionDecisionReason.AskEveryTime, policy.CheckAsync(PermissionCapability.ScreenCapture).GetAwaiter().GetResult().Reason);

        // Back to allowed and to off: each is what is saved and what the next check says.
        screen.SelectedChoice = screen.Choices[2];
        kit.Settle();
        Assert.True(policy.CheckAsync(PermissionCapability.ScreenCapture).GetAwaiter().GetResult().IsAllowed);
        Assert.False(kit.Saved.Permissions.Ask.ScreenCapture);

        screen.SelectedChoice = screen.Choices[0];
        kit.Settle();
        Assert.Equal(PermissionDecisionReason.TurnedOff, policy.CheckAsync(PermissionCapability.ScreenCapture).GetAwaiter().GetResult().Reason);
    });

    [Fact]
    public void EachAskableCapabilityKeepsItsOwnChoiceAndLeavesTheOthersAlone() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Permissions;

        foreach (var capability in Askable)
        {
            page[capability].Mode = PermissionMode.AskEveryTime;
        }

        kit.Settle();

        Assert.All(Askable, capability => Assert.Equal(PermissionMode.AskEveryTime, kit.Saved.Permissions.ModeOf(capability)));
        Assert.True(kit.Saved.Permissions.Files);
        Assert.False(kit.Saved.Permissions.ClipboardHistory);

        page[PermissionCapability.Messaging].Mode = PermissionMode.Off;
        kit.Settle();

        Assert.Equal(PermissionMode.Off, kit.Saved.Permissions.ModeOf(PermissionCapability.Messaging));
        Assert.Equal(PermissionMode.AskEveryTime, kit.Saved.Permissions.ModeOf(PermissionCapability.Calendar));
    });

    [Fact]
    public void AskingIsRefusedForARowThatCannotAskAndForOneThatIsNotAvailableAndNothingIsSaved() => RunSta(() =>
    {
        var settings = new RecordingSettings();
        var kit = CreateSettingsKit(service: settings);
        var page = kit.Model.Permissions;
        var saves = settings.Saves;

        page[PermissionCapability.Files].Mode = PermissionMode.AskEveryTime;
        page[PermissionCapability.ClipboardHistory].Mode = PermissionMode.AskEveryTime;
        page[PermissionCapability.DestructiveActions].Mode = PermissionMode.Allowed;
        page[PermissionCapability.DestructiveActions].Mode = PermissionMode.AskEveryTime;
        kit.Settle();

        Assert.Equal(saves, settings.Saves);
        Assert.Equal(PermissionMode.Allowed, page[PermissionCapability.Files].Mode);
        Assert.Equal(PermissionMode.Off, page[PermissionCapability.ClipboardHistory].Mode);
        Assert.Equal(PermissionMode.Off, page[PermissionCapability.DestructiveActions].Mode);
    });

    [Fact]
    public void WhatIsSavedAsAskedAboutIsShownAsAskedAboutAndAHandEditedAskForAnOffSwitchIsShownOff() => RunSta(() =>
    {
        var asking = new PermissionSettings().WithMode(PermissionCapability.Calendar, PermissionMode.AskEveryTime)
            .WithMode(PermissionCapability.Messaging, PermissionMode.AskEveryTime) with { Messaging = false };
        var kit = CreateSettingsKit(new AppSettings { Permissions = asking });

        Assert.Equal(PermissionMode.AskEveryTime, kit.Model.Permissions[PermissionCapability.Calendar].Mode);
        Assert.Equal("Ask every time", kit.Model.Permissions[PermissionCapability.Calendar].SelectedChoice!.Label);
        Assert.Equal(PermissionMode.Off, kit.Model.Permissions[PermissionCapability.Messaging].Mode);
    });

    [Fact]
    public void TheAppRefusesWhatIsAskedAboutUntilTheUserSaysYesBecauseEveryServiceChecksThePolicy()
    {
        using var host = AppHost.Create();

        // The services that read the screen, a selection and files are the checked ones: the container hands out the decorated services, never the bare ones.
        Assert.IsType<Assistant.Windows.Capture.PermissionCheckedScreenCapture>(host.Services.GetRequiredService<Assistant.Windows.Capture.IScreenCapture>());
        Assert.IsType<PermissionCheckedSelectionService>(host.Services.GetRequiredService<ISelectionService>());
        Assert.IsType<PermissionCheckedCopySelectionService>(host.Services.GetRequiredService<ICopySelectionService>());

        var gate = host.Services.GetRequiredService<IPermissionGate>();
        Assert.IsType<PermissionGate>(gate);
        Assert.Same(gate, host.Services.GetRequiredService<IPermissionGate>());
        Assert.IsType<Assistant.UI.Permissions.WpfPermissionPrompt>(host.Services.GetRequiredService<IPermissionPrompt>());
    }

    [Fact]
    public void ThePermissionsPageNowListsCalendarAndMessagingAsThingsThatCanBeChosen() => RunSta(() =>
    {
        var page = CreateSettingsKit().Model.Permissions;

        Assert.True(page[PermissionCapability.Calendar].IsAvailable);
        Assert.True(page[PermissionCapability.Messaging].IsAvailable);
        Assert.False(page[PermissionCapability.DestructiveActions].IsAvailable);
        Assert.Equal(SettingStatus.AlwaysOff, page[PermissionCapability.DestructiveActions].Status);
    });

    [Fact]
    public void ThePageNeverShowsAnythingAsAllowedThatTheAssistantCannotDoWhateverIsSaved() => RunSta(() =>
    {
        // Every switch on, as a hand-edited file could have it, except Files.
        var everything = EveryPermission.Aggregate(new PermissionSettings(), (permissions, capability) => permissions.With(capability, true));
        var saved = new AppSettings { Permissions = everything with { Files = false } };
        var kit = CreateSettingsKit(saved);

        Assert.All(kit.Model.Permissions.Items, item => Assert.Equal(item.IsAvailable && item.Capability != PermissionCapability.Files, item.IsOn));

        // A change made here keeps what was saved for the others: the file is not rewritten to say something else.
        kit.Model.Permissions[PermissionCapability.Files].IsOn = true;
        kit.Settle();

        Assert.True(kit.Model.Permissions[PermissionCapability.Files].IsOn);
        Assert.Equal(everything, kit.Saved.Permissions);
        Assert.All(kit.Model.Permissions.Items.Where(item => !item.IsAvailable), item => Assert.False(item.IsOn));
    });

    [Fact]
    public void ASwitchThatCouldNotBeSavedPutsItselfBackAndSaysSo() => RunSta(() =>
    {
        var settings = new RecordingSettings { SaveFailure = new IOException("disk full") };
        var kit = CreateSettingsKit(service: settings);

        kit.Model.Permissions[PermissionCapability.Files].IsOn = false;
        kit.Settle();

        Assert.True(kit.Model.HasNotice);
        Assert.True(kit.Model.Permissions[PermissionCapability.Files].IsOn);
        Assert.True(kit.Saved.Permissions.Files);
    });

    [Fact]
    public void ASwitchChangedElsewhereIsShownWhenTheWindowIsShownAgain() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        kit.Settings.UpdateAsync(settings => settings with { Permissions = settings.Permissions with { Files = false } })
            .GetAwaiter().GetResult();

        kit.Model.LoadAsync().GetAwaiter().GetResult();

        Assert.False(kit.Model.Permissions[PermissionCapability.Files].IsOn);
    });

    [Fact]
    public void ThePermissionsPageIsDrawnWithASwitchOrThreeChoicesPerCapabilityAndOnlyWhatWorksCanBeUsed() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Permissions);
            window.UpdateLayout();
            Pump();
            var content = Named<ContentControl>(window, "PageContent");

            Assert.Equal("Permissions", Named<TextBlock>(window, "PageTitle").Text);
            var rows = Descendants<SettingsRow>(content).ToArray();
            Assert.Equal(PermissionCatalog.All.Select(definition => definition.Title), rows.Select(row => row.Header));
            Assert.Equal(PermissionCatalog.All.Select(definition => definition.Summary), rows.Select(row => row.Description));
            Assert.Equal(["", "", "", "", "", "", "", "", "Always off"], rows.Select(row => row.StatusText));

            // A capability whose uses can be asked about one by one has three choices (Off, Ask every time, Allowed); the others (Files, Clipboard History and Destructive
            // Actions) are a switch. Each row shows one of the two.
            var switches = Descendants<CheckBox>(content).Where(box => box.IsVisible).ToArray();
            var choices = Descendants<ComboBox>(content).Where(box => box.IsVisible).ToArray();
            Assert.Equal(
                new[] { PermissionCapability.Files, PermissionCapability.ClipboardHistory, PermissionCapability.DestructiveActions }.Select(capability => $"{PermissionCatalog.Get(capability).Title} permission"),
                switches.Select(box => System.Windows.Automation.AutomationProperties.GetName(box)));
            Assert.Equal([true, true, false], switches.Select(box => box.IsEnabled));
            Assert.Equal([true, false, false], switches.Select(box => box.IsChecked == true));
            Assert.Equal(
                new[]
                {
                    PermissionCapability.ScreenCapture, PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy, PermissionCapability.Calendar,
                    PermissionCapability.Messaging, PermissionCapability.ExternalSearch,
                }.Select(capability => $"{PermissionCatalog.Get(capability).Title} permission"),
                choices.Select(box => System.Windows.Automation.AutomationProperties.GetName(box)));
            Assert.All(choices, box => Assert.Equal(["Off", "Ask every time", "Allowed"], box.Items.Cast<PermissionChoice>().Select(choice => choice.Label)));
            Assert.Equal(["Allowed", "Allowed", "Off", "Off", "Off", "Off"], choices.Select(box => ((PermissionChoice)box.SelectedItem).Label));

            // Switching Files off through the page's own control saves it, and so does choosing to be asked each time for the calendar.
            ((IToggleProvider)new CheckBoxAutomationPeer(switches[0])).Toggle();
            choices[3].SelectedItem = choices[3].Items[1];
            kit.Settle();
            Pump();

            Assert.False(kit.Saved.Permissions.Files);
            Assert.False(switches[0].IsChecked);
            Assert.Equal(PermissionMode.AskEveryTime, kit.Saved.Permissions.ModeOf(PermissionCapability.Calendar));
            Assert.True(kit.Saved.Permissions.Ask.Calendar);
            Assert.Equal(PermissionDecisionReason.AskEveryTime, new SettingsPermissionPolicy(kit.Settings).CheckAsync(PermissionCapability.Calendar).GetAwaiter().GetResult().Reason);

            // What cannot be changed cannot be: Destructive Actions has a switch that does not work.
            Assert.Throws<ElementNotEnabledException>(() => ((IToggleProvider)new CheckBoxAutomationPeer(switches[2])).Toggle());
            Assert.False(kit.Saved.Permissions.DestructiveActions);
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of the whole page in a window tall enough to hold it, with Files on and off.
    [Fact]
    public void RenderThePermissionsPageWholeWhenAskedTo() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        foreach (var (name, files) in new[] { ("on", true), ("off", false) })
        {
            var kit = CreateSettingsKit(new AppSettings { Permissions = new PermissionSettings { Files = files } });
            var (window, _, _) = CreateSettingsWindow(kit);
            try
            {
                window.Height = 1200;
                window.Show();
                kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Permissions);
                window.UpdateLayout();
                Pump();
                RenderFixture(Named<Grid>(window, "Root"), $"settings-permissions-files-{name}.png", 1);
            }
            finally
            {
                window.CloseForGood();
            }
        }
    })));

    [Fact]
    public void TheAppGivesWhateverReadsFilesTheSamePolicyThePermissionsPageChanges()
    {
        using var host = AppHost.Create();

        var policy = host.Services.GetRequiredService<IPermissionPolicy>();
        Assert.IsType<SettingsPermissionPolicy>(policy);
        Assert.Same(policy, host.Services.GetRequiredService<IPermissionPolicy>());

        // The image test the app really uses was given that same policy: without it, "demo image" would say it is not available.
        var answers = Assert.IsType<DemoAnswerProvider>(host.Services.GetRequiredService<IAnswerProvider>());
        var given = typeof(DemoAnswerProvider)
            .GetField("_permissions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(answers);
        Assert.Same(policy, given);
    }

    // ---- The one thing that reads a file today, the image test, asks first -----------------------------------------------

    [Fact]
    public void DemoImageDoesNotEvenOpenTheFileDialogWhileFilesIsOff() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        settings.SaveAsync(new AppSettings { Permissions = new PermissionSettings { Files = false } }).GetAwaiter().GetResult();
        var model = new ScriptedModel { Active = new Assistant.Core.Contracts.ModelInfo("vision-model", 8192) { SupportsVision = true } };
        var picker = new CountingPicker(@"C:\Pictures\never-read.png");
        var demo = ImageTest(model, picker, settings);
        var shown = new List<MessageViewModel>();

        demo.StreamAnswerAsync("demo image What is this?", shown.Add, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(0, picker.Asked);
        Assert.Empty(model.Requests);
        var answer = Assert.Single(shown);
        Assert.Contains("Files are turned off", answer.Text, StringComparison.Ordinal);
        Assert.Contains("Permissions", answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("never-read", answer.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void DemoImageAsksAgainOnceFilesIsTurnedBackOn() => RunSta(() =>
    {
        var settings = new InMemorySettingsService();
        var model = new ScriptedModel { Active = new Assistant.Core.Contracts.ModelInfo("vision-model", 8192) { SupportsVision = true } };
        var picker = new CountingPicker(null);
        var demo = ImageTest(model, picker, settings);
        var shown = new List<MessageViewModel>();

        settings.SaveAsync(new AppSettings { Permissions = new PermissionSettings { Files = false } }).GetAwaiter().GetResult();
        demo.StreamAnswerAsync("demo image", shown.Add, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(0, picker.Asked);

        settings.SaveAsync(new AppSettings()).GetAwaiter().GetResult();
        demo.StreamAnswerAsync("demo image", shown.Add, CancellationToken.None).GetAwaiter().GetResult();

        // On again, the dialog opens (the test's picker chooses nothing, so nothing is asked of the model).
        Assert.Equal(1, picker.Asked);
        Assert.Contains("No image was chosen", shown[^1].Text, StringComparison.Ordinal);
    });

    [Fact]
    public void DemoImageWithoutAPermissionPolicyDoesNothingRatherThanReadingFiles() => RunSta(() =>
    {
        var picker = new CountingPicker(@"C:\Pictures\never-read.png");
        var demo = new DemoAnswerProvider(
            new FakeClipboard(), new FixedClock(Now), localModel: LocalAnswers(new ScriptedModel()), imagePicker: picker,
            images: new Assistant.Windows.Imaging.ImagePreprocessor());
        var shown = new List<MessageViewModel>();

        demo.StreamAnswerAsync("demo image", shown.Add, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(0, picker.Asked);
        Assert.Equal("The image test is not available here.", Assert.Single(shown).Text);
    });

    private sealed class CountingPicker(string? path) : IImagePicker
    {
        public int Asked { get; private set; }

        public Task<string?> PickAsync(CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(path);
        }
    }
}
