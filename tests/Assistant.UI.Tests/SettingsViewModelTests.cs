using System.IO;
using System.Windows.Input;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Assistant.UI.Settings;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The settings window's view model: what each page shows and saves ----------------------------------------

    private static void SettingsWait(Task task)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < timeout)
        {
            Pump();
        }

        task.GetAwaiter().GetResult();
    }

    // The result of a task that really is asynchronous, waiting for it without blocking the UI thread its continuations need.
    private static T SettingsResult<T>(Task<T> task)
    {
        SettingsWait(task);
        return task.Result;
    }

    private static string[] Titles(SettingsViewModel model) => [.. model.Sections.Select(section => section.Title)];

    [Fact]
    public void TheSidebarListsTheSectionsInOrderWithAsrBeforeIntegrations() => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        Assert.Equal(
            ["General", "Model", "Voice", "ASR", "Integrations", "Privacy", "Permissions", "People", "Memory", "Hotkeys", "Activity", "About"], Titles(kit.Model));
        Assert.Equal("General", kit.Model.SelectedSection.Title);
        Assert.IsType<GeneralPage>(kit.Model.CurrentPage);

        kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Voice);

        Assert.IsType<VoicePage>(kit.Model.CurrentPage);
        Assert.Equal(
            [
                typeof(GeneralPage), typeof(ModelPage), typeof(VoicePage), typeof(AsrPage), typeof(IntegrationsPage),
                typeof(PrivacyPage), typeof(PermissionsPage), typeof(PeoplePage), typeof(MemoryPage), typeof(HotkeysPage), typeof(ActivityPage), typeof(AboutPage),
            ],
            kit.Model.Sections.Select(section =>
            {
                kit.Model.SelectedSection = section;
                return kit.Model.CurrentPage.GetType();
            }));
    });

    [Fact]
    public void EveryPageShowsWhatIsSaved() => RunSta(() => WithCulture("en-US", () =>
    {
        var saved = new AppSettings
        {
            Ui = new UiSettings { BarResultsPerGroup = 6, FilesScopeOnByDefault = true },
            LaunchAtLogin = new LaunchAtLoginSettings { Enabled = true },
            Privacy = new PrivacySettings { HistoryEnabled = false, HistoryRetention = HistoryRetention.SevenDays, ExcludedFolders = [@"C:\Taxes"] },
            Integrations = new IntegrationSettings { ExplorerContextMenuEnabled = true },
            Voice = new VoiceSettings { TextToSpeechModelId = "piper", WakeWordEnabled = true },
            ContextLimits = new ContextLimitSettings { ReservedOutputTokens = 2048, MaxAttachedFiles = 20, MaxRetrievedFiles = 7, MaxFileSizeBytes = 8L * 1024 * 1024 },
            Hotkeys = new HotkeySettings { SearchOrAsk = null },
        };
        var kit = CreateSettingsKit(saved);
        var model = kit.Model;

        Assert.True(model.General.StartAtSignIn);
        Assert.Equal(6, model.General.ResultsPerGroup);
        Assert.True(model.General.FilesByDefault);
        Assert.False(model.Privacy.HistoryEnabled);
        Assert.Equal(HistoryRetention.SevenDays, model.Privacy.Retention.Value);
        Assert.Equal([@"C:\Taxes"], model.Privacy.ExcludedFolders);
        Assert.True(model.Integrations.ExplorerContextMenu);
        Assert.False(model.Integrations.BrowserBridge);
        Assert.Equal("piper", model.Voice.SelectedModel.Id);
        Assert.True(model.Voice.WakeWordEnabled);
        Assert.Equal("2,048", model.Context.ReservedForAnswer.Text);
        Assert.Equal("20", model.Context.MaxAttachedFiles.Text);
        Assert.Equal("7", model.Context.MaxRetrievedFiles.Text);
        Assert.Equal("8", model.Context.MaxFileSizeMegabytes.Text);
        Assert.Equal("Off", model.Hotkeys.SearchOrAsk.DisplayText);
        Assert.Equal("Alt + Shift + W", model.Hotkeys.SelectedText.DisplayText);
        Assert.Equal("Alt + Shift + S", model.Hotkeys.VisualIntelligence.DisplayText);
        Assert.Equal("Alt + Shift + C", model.Hotkeys.SelectedTextByCopy.DisplayText);
    }));

    [Fact]
    public void ShowingTheSettingsNeverWritesThem() => RunSta(() =>
    {
        var settings = new RecordingSettings();
        var kit = CreateSettingsKit(service: settings);

        kit.Model.LoadAsync().GetAwaiter().GetResult();
        kit.Model.LoadAsync().GetAwaiter().GetResult();
        foreach (var section in kit.Model.Sections)
        {
            kit.Model.SelectedSection = section;
        }

        Assert.Equal(0, settings.Saves);
        Assert.True(settings.Loads >= 2);
    });

    [Fact]
    public void AChangeIsSavedAtOnceAndOnlyThatSettingChanges() => RunSta(() =>
    {
        var saved = SettingsVoiceAndPrivacySample();
        var kit = CreateSettingsKit(saved);

        kit.Model.Privacy.HistoryEnabled = false;
        kit.Settle();

        Assert.False(kit.Saved.Privacy.HistoryEnabled);
        Assert.Equal(saved with { Privacy = saved.Privacy with { HistoryEnabled = false } }, kit.Saved);
        Assert.False(kit.Model.HasNotice);
    });

    private static AppSettings SettingsVoiceAndPrivacySample() => new()
    {
        Voice = new VoiceSettings { TextToSpeechModelId = "kokoro-82m-onnx" },
        Privacy = new PrivacySettings { HistoryRetention = HistoryRetention.NinetyDays },
        ContextLimits = new ContextLimitSettings { NormalContextTokens = 5000 },
    };

    [Fact]
    public void ChangesMadeInQuickSuccessionAreAllSavedInOrder() => RunSta(() =>
    {
        var settings = new RecordingSettings { SaveDelay = TimeSpan.FromMilliseconds(40) };
        var kit = CreateSettingsKit(service: settings);

        kit.Model.Privacy.HistoryEnabled = false;
        kit.Model.General.FilesByDefault = true;
        kit.Model.Integrations.BrowserBridge = true;
        kit.Model.Voice.WakeWordEnabled = true;
        SettingsWait(kit.Model.WhenSavedAsync());

        var final = kit.Saved;
        Assert.False(final.Privacy.HistoryEnabled);
        Assert.True(final.Ui.FilesScopeOnByDefault);
        Assert.True(final.Integrations.BrowserBridgeEnabled);
        Assert.True(final.Voice.WakeWordEnabled);
        Assert.Equal(4, settings.Saves);
        Assert.Equal(1, settings.MostSavesAtOnce);
    });

    [Fact]
    public void ASettingSavedElsewhereIsNotUndoneByAChangeHere() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        // Another window saves a model file while the settings window is open.
        kit.Settings.UpdateAsync(settings => settings with
        {
            Model = settings.Model with { ModelFilePath = @"C:\Models\other.gguf" },
        }).GetAwaiter().GetResult();

        kit.Model.Privacy.HistoryEnabled = false;
        kit.Settle();

        Assert.Equal(@"C:\Models\other.gguf", kit.Saved.Model.ModelFilePath);
        Assert.False(kit.Saved.Privacy.HistoryEnabled);
        Assert.True(kit.Model.Model.UsesOwnFile);
    });

    [Fact]
    public void ASaveThatFailsSaysSoAndPutsThePageBack() => RunSta(() =>
    {
        var settings = new RecordingSettings { SaveFailure = new IOException("disk full") };
        var kit = CreateSettingsKit(service: settings);

        kit.Model.Privacy.HistoryEnabled = false;
        kit.Settle();

        Assert.True(kit.Model.HasNotice);
        Assert.Contains("couldn't be saved", kit.Model.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("disk full", kit.Model.Notice, StringComparison.Ordinal);
        Assert.True(kit.Model.Privacy.HistoryEnabled);
        Assert.True(kit.Saved.Privacy.HistoryEnabled);

        // The next change that can be saved clears the notice.
        settings.SaveFailure = null;
        kit.Model.Privacy.HistoryEnabled = false;
        kit.Settle();
        Assert.False(kit.Model.HasNotice);
        Assert.False(kit.Saved.Privacy.HistoryEnabled);
    });

    [Fact]
    public void ASettingTheServiceRefusesIsExplainedAndPutBack() => RunSta(() =>
    {
        var settings = new RecordingSettings
        {
            SaveFailure = new SettingsValidationException([new SettingsIssue("Ui.BarResultsPerGroup", SettingsIssueKind.OutOfRange)]),
        };
        var kit = CreateSettingsKit(service: settings);

        kit.Model.General.ResultsPerGroup = 9;
        kit.Settle();

        Assert.Contains("can't be saved", kit.Model.Notice, StringComparison.Ordinal);
        Assert.Equal(new UiSettings().BarResultsPerGroup, kit.Model.General.ResultsPerGroup);
    });

    [Theory]
    [InlineData(SettingsLoadOutcome.Loaded, false)]
    [InlineData(SettingsLoadOutcome.NoFile, false)]
    [InlineData(SettingsLoadOutcome.Repaired, true)]
    [InlineData(SettingsLoadOutcome.RestoredFromBackup, true)]
    [InlineData(SettingsLoadOutcome.ResetToDefaults, true)]
    public void TheWindowSaysWhenTheSettingsCouldNotBeReadAsTheyWereLeft(SettingsLoadOutcome outcome, bool notice) => RunSta(() =>
    {
        var kit = CreateSettingsKit(report: new FixedReport(outcome));

        Assert.Equal(notice, kit.Model.HasNotice);
        Assert.Equal(notice, kit.Model.Notice.Length > 0);
    });

    [Fact]
    public void AnUnreadableSettingsFileIsExplainedInTheWindow() => RunSta(() =>
    {
        var report = new FixedReport(SettingsLoadOutcome.RestoredFromBackup);
        var kit = CreateSettingsKit(report: report);
        kit.Model.LoadAsync().GetAwaiter().GetResult();

        Assert.Contains("last good copy", kit.Model.Notice, StringComparison.Ordinal);
    });

    // ---- Number fields --------------------------------------------------------------------------------------------

    [Fact]
    public void ANumberTheEngineCanTakeIsSavedAsItIsTyped() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit();

        kit.Model.Model.NormalLimit.Text = "6,000";
        kit.Settle();

        Assert.Equal(6000, kit.Saved.ContextLimits.NormalContextTokens);
        Assert.False(kit.Model.Model.NormalLimit.HasError);
        Assert.Equal("6,000", kit.Model.Model.NormalLimit.Text);
    }));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("-4096")]
    [InlineData("255")]
    [InlineData("99999999999999")]
    public void ANumberTheEngineCannotTakeIsExplainedAndNotSaved(string typed) => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        kit.Model.Model.NormalLimit.Text = typed;
        kit.Settle();

        var field = kit.Model.Model.NormalLimit;
        Assert.True(field.HasError);
        Assert.Equal(ContextAdviceLevel.Error, field.AdviceLevel);
        Assert.NotEmpty(field.AdviceText);
        Assert.Equal(ContextLimitSettings.Roomy.NormalContextTokens, kit.Saved.ContextLimits.NormalContextTokens);
        Assert.Equal(typed, field.Text);
    });

    [Fact]
    public void WhatWasTypedIsNotWipedWhileTheUserFixesIt() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit();
        kit.Model.Model.NormalLimit.Text = "40x";

        // Something else is saved meanwhile, which shows every page again.
        kit.Model.Privacy.HistoryEnabled = false;
        kit.Settle();
        Assert.Equal("40x", kit.Model.Model.NormalLimit.Text);
        Assert.True(kit.Model.Model.NormalLimit.HasError);

        kit.Model.Model.NormalLimit.Text = "4000";
        kit.Settle();
        Assert.False(kit.Model.Model.NormalLimit.HasError);
        Assert.Equal(4000, kit.Saved.ContextLimits.NormalContextTokens);

        // Opening the window again drops anything typed and not accepted.
        kit.Model.Model.NormalLimit.Text = "zzz";
        kit.Model.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal("4,000", kit.Model.Model.NormalLimit.Text);
        Assert.False(kit.Model.Model.NormalLimit.HasError);
    }));

    [Fact]
    public void ANumberSavedElsewhereReplacesWhatIsShownButNotWhileTheUserTypes() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit();
        kit.Settings.UpdateAsync(settings => settings with
        {
            ContextLimits = settings.ContextLimits with { NormalContextTokens = 9000 },
        }).GetAwaiter().GetResult();

        kit.Model.Privacy.HistoryEnabled = false;
        kit.Settle();

        Assert.Equal("9,000", kit.Model.Model.NormalLimit.Text);
    }));

    [Fact]
    public void NumberFieldsReadGroupsOfDigitsAndRefuseEverythingElse()
    {
        Assert.True(NumberField.TryParse("16,384", out var comma));
        Assert.Equal(16384, comma);
        Assert.True(NumberField.TryParse("16 384", out var space));
        Assert.Equal(16384, space);
        Assert.True(NumberField.TryParse("16\u00A0384", out var nonBreaking));
        Assert.Equal(16384, nonBreaking);
        Assert.True(NumberField.TryParse("99999999999", out var huge));
        Assert.Equal(int.MaxValue, huge);
        Assert.False(NumberField.TryParse("", out _));
        Assert.False(NumberField.TryParse("1.5", out _));
        Assert.False(NumberField.TryParse("-3", out _));
        Assert.False(NumberField.TryParse("١٢٣", out _));
    }

    // ---- The Model page -------------------------------------------------------------------------------------------

    [Fact]
    public void TheModelPageListsEveryProfileWithItsLocationAndStatus() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(existingFiles: [ProfileFile("chat-4b", "model.gguf"), ProfileFile("chat-4b", "mmproj.gguf")]);
        var page = kit.Model.Model;

        Assert.Equal(["chat-4b", "chat-9b"], page.Profiles.Select(profile => profile.Id));
        Assert.Equal(["Standard (4B class)", "Large (8-9B class)"], page.Profiles.Select(profile => profile.DisplayName));
        Assert.True(page.Profiles[0].IsDefault);
        Assert.False(page.Profiles[1].IsDefault);

        var standard = page.Profiles[0];
        Assert.True(standard.IsInstalled);
        Assert.Equal("Installed.", standard.Status);
        Assert.Equal(Path.Combine(SettingsPaths.ModelsDirectory, "chat-4b"), standard.Location);
        Assert.Contains("Q4_K_M", standard.Summary, StringComparison.Ordinal);
        Assert.Contains("reads text and images", standard.Summary, StringComparison.Ordinal);
        Assert.Contains("8 GB", standard.Summary, StringComparison.Ordinal);

        var large = page.Profiles[1];
        Assert.False(large.IsInstalled);
        Assert.StartsWith("Not installed", large.Status, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(SettingsPaths.ModelsDirectory, "chat-9b"), large.Location);
        Assert.Equal(SettingsPaths.ModelsDirectory, page.ModelsFolder);
        Assert.Same(standard, page.SelectedProfile);
    }));

    [Fact]
    public void AProfileWhoseProjectorIsMissingOrWhosePCIsTooSmallSaysSo() => RunSta(() =>
    {
        var kit = CreateSettingsKit(existingFiles: [ProfileFile("chat-4b", "model.gguf"), ProfileFile("chat-9b", "model.gguf"), ProfileFile("chat-9b", "mmproj.gguf")], memory: 8 * SettingsGiB);

        Assert.Contains("projector file is missing", kit.Model.Model.Profiles[0].Status, StringComparison.Ordinal);
        Assert.Contains("less memory than the 16 GB", kit.Model.Model.Profiles[1].Status, StringComparison.Ordinal);
    });

    [Fact]
    public void ChoosingAProfileSavesItAndTheDefaultIsSavedAsNone() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Model;

        page.SelectedProfile = page.Profiles[1];
        kit.Settle();
        Assert.Equal("chat-9b", kit.Saved.Model.ProfileId);
        Assert.Same(page.Profiles[1], page.SelectedProfile);

        page.SelectedProfile = page.Profiles[0];
        kit.Settle();
        Assert.Null(kit.Saved.Model.ProfileId);
        Assert.Same(page.Profiles[0], page.SelectedProfile);
    });

    [Fact]
    public void AModelFileThePickedByHandOverridesTheProfilesAndCanBeGivenUp() => RunSta(() =>
    {
        var kit = CreateSettingsKit(new AppSettings
        {
            Model = new ModelSettings
            {
                ModelFilePath = @"C:\Models\mine.gguf", ProjectorFilePath = @"C:\Models\mine-mmproj.gguf",
                ChatTemplateFilePath = @"C:\Models\mine.jinja", ProfileId = "chat-9b",
            },
        });
        var page = kit.Model.Model;

        Assert.True(page.UsesOwnFile);
        Assert.False(page.CanChooseProfile);
        Assert.Equal(@"C:\Models\mine.gguf", page.OwnFilePath);

        page.UseProfileInsteadCommand.Execute(null);
        kit.Settle();

        Assert.False(page.UsesOwnFile);
        Assert.True(page.CanChooseProfile);
        Assert.Null(kit.Saved.Model.ModelFilePath);
        Assert.Null(kit.Saved.Model.ProjectorFilePath);
        Assert.Null(kit.Saved.Model.ChatTemplateFilePath);
        Assert.Equal("chat-9b", kit.Saved.Model.ProfileId);
    });

    [Fact]
    public void TheHardwarePresetIsAutomaticOrOneOfThree() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(memory: 16 * SettingsGiB);
        var page = kit.Model.Model;

        Assert.Equal(4, page.Presets.Count);
        Assert.Equal("Automatic (Balanced for this PC)", page.SelectedPreset.Label);
        Assert.Equal(["compact", "balanced", "performance"], page.Presets.Skip(1).Select(preset => preset.Id));
        Assert.Contains("16", page.MemoryText, StringComparison.Ordinal);

        page.SelectedPreset = page.Presets[1];
        kit.Settle();
        Assert.Equal("compact", kit.Saved.Model.HardwarePresetId);

        page.SelectedPreset = page.Presets[0];
        kit.Settle();
        Assert.Null(kit.Saved.Model.HardwarePresetId);
    }));

    [Fact]
    public void AnEightGigabyteMachineIsToldItsPresetIsCompact() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(memory: 8 * SettingsGiB);

        Assert.Equal("Automatic (Compact for this PC)", kit.Model.Model.SelectedPreset.Label);

        // The window is the ordinary conversations' 8,000 tokens whatever the PC: only a preset the user chose themselves holds it to less.
        Assert.Equal("8,000", kit.Model.Model.CustomWindow.Text);
    }));

    [Fact]
    public void TheModelStatusFollowsTheLifecycle() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Model;
        Assert.Equal("The local model isn't loaded.", page.StatusText);
        Assert.False(page.IsReady);

        Publish(kit.Bus, new ModelStatusChanged(ModelStatus.Loading));
        Assert.True(page.IsBusy);
        Assert.Equal("Loading the local model…", page.StatusText);

        Publish(kit.Bus, new ModelStatusChanged(ModelStatus.Ready));
        Assert.True(page.IsReady);
        Assert.Equal("The local model is ready.", page.StatusText);

        Publish(kit.Bus, new ModelStatusChanged(ModelStatus.Failed) { Failure = ModelFailure.ModelNotFound });
        Assert.True(page.IsFailed);
        Assert.Contains("couldn't be found", page.StatusText, StringComparison.Ordinal);

        kit.Model.Dispose();
        Publish(kit.Bus, new ModelStatusChanged(ModelStatus.Ready));
        Assert.True(page.IsFailed);
    });

    [Fact]
    public void ALoadedModelIsNamedWithItsWindow() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit();
        kit.Lifecycle.LoadedModel = new ModelInfo("chat-4b", 8192);

        kit.Model.LoadAsync().GetAwaiter().GetResult();

        Assert.Equal("Loaded: chat-4b, with a window of 8,192 tokens.", kit.Model.Model.LoadedText);
    }));

    [Fact]
    public void TheWindowTheModelGetsIsTheProfilesHeldToThePresetUnlessTheUserSetsOne() => RunSta(() => WithCulture("en-US", () =>
    {
        var compact = CreateSettingsKit(new AppSettings { Model = new ModelSettings { HardwarePresetId = "compact" } });
        Assert.Equal("4,096", compact.Model.Model.CustomWindow.Text);
        Assert.Contains("held to what the Compact preset allows", compact.Model.Model.WindowSummary, StringComparison.Ordinal);
        Assert.False(compact.Model.Model.UseCustomWindow);

        var custom = CreateSettingsKit(new AppSettings { Model = new ModelSettings { ContextLength = 12_288 } });
        Assert.True(custom.Model.Model.UseCustomWindow);
        Assert.Equal("12,288", custom.Model.Model.CustomWindow.Text);
        Assert.StartsWith("The model is loaded with a window of 12,288 tokens, as it was set", custom.Model.Model.WindowSummary, StringComparison.Ordinal);

        // With nothing chosen the model has the two windows: the ordinary one, and the larger while a conversation carries files.
        var automatic = CreateSettingsKit(new AppSettings());
        Assert.Equal("8,000", automatic.Model.Model.CustomWindow.Text);
        Assert.Contains("loaded with 8,000 tokens", automatic.Model.Model.WindowSummary, StringComparison.Ordinal);
        Assert.Contains("When a conversation carries files it is loaded again with", automatic.Model.Model.WindowSummary, StringComparison.Ordinal);
    }));

    [Fact]
    public void TurningOnTheCustomWindowStartsFromTheWindowInUseAndTurningItOffGoesBackToAutomatic() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Model;

        page.UseCustomWindow = true;
        kit.Settle();
        Assert.Equal(8000, kit.Saved.Model.ContextLength);
        Assert.True(page.UseCustomWindow);

        page.UseCustomWindow = false;
        kit.Settle();
        Assert.Null(kit.Saved.Model.ContextLength);
        Assert.False(page.UseCustomWindow);
    });

    [Fact]
    public void AHugeWindowIsWarnedAboutWithTheMemoryItMayNeedAndStillSaved() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(memory: 16 * SettingsGiB);
        var page = kit.Model.Model;
        page.UseCustomWindow = true;
        kit.Settle();

        page.CustomWindow.Text = "300000";
        kit.Settle();

        Assert.Equal(300_000, kit.Saved.Model.ContextLength);
        Assert.Equal(ContextAdviceLevel.Warning, page.CustomWindow.AdviceLevel);
        Assert.False(page.CustomWindow.HasError);
        Assert.Contains("may need about", page.CustomWindow.AdviceText, StringComparison.Ordinal);
        Assert.Contains("You can still use it", page.CustomWindow.AdviceText, StringComparison.Ordinal);
    }));

    [Fact]
    public void AWindowTheEngineCannotTakeIsRefusedAndTheOldOneStays() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(new AppSettings { Model = new ModelSettings { ContextLength = 12_288 } });
        var page = kit.Model.Model;

        page.CustomWindow.Text = "100";
        kit.Settle();

        Assert.True(page.CustomWindow.HasError);
        Assert.Contains("256", page.CustomWindow.AdviceText, StringComparison.Ordinal);
        Assert.Equal(12_288, kit.Saved.Model.ContextLength);

        page.CustomWindow.Text = "2000000";
        kit.Settle();
        Assert.True(page.CustomWindow.HasError);
        Assert.Equal(12_288, kit.Saved.Model.ContextLength);
    }));

    [Fact]
    public void ContextLimitsResizeTheWindowAndAHeavyLimitBelowNormalShowsAWarning() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(new AppSettings { ContextLimits = new ContextLimitSettings { NormalContextTokens = 4096, HeavyContextTokens = 20000 } });
        var page = kit.Model.Model;

        // The context fields are the two windows the model is loaded with: no single window is written down beside them, and the model is put
        // away so that its next use loads it with the one the conversation needs.
        Assert.Equal(ContextAdviceLevel.None, page.NormalLimit.AdviceLevel);
        Assert.Equal(ContextAdviceLevel.None, page.HeavyLimit.AdviceLevel);
        page.HeavyLimit.Text = "22000";
        kit.Settle();
        Assert.Equal(22000, kit.Saved.ContextLimits.HeavyContextTokens);
        Assert.Null(kit.Saved.Model.ContextLength);
        Assert.True(kit.Lifecycle.Unloads > 0);

        page.HeavyLimit.Text = "2000";
        kit.Settle();
        Assert.Equal(2000, kit.Saved.ContextLimits.HeavyContextTokens);
        Assert.Equal(ContextAdviceLevel.Warning, page.HeavyLimit.AdviceLevel);
        Assert.Contains("smaller than the normal limit", page.HeavyLimit.AdviceText, StringComparison.Ordinal);

        page.NormalLimit.Text = "0";
        kit.Settle();
        Assert.Equal(0, kit.Saved.ContextLimits.NormalContextTokens);
        Assert.Contains("No limit", page.NormalLimit.AdviceText, StringComparison.Ordinal);
    }));

    [Fact]
    public void TheGraphicsCardChoiceIsShownAsSaved() => RunSta(() =>
    {
        var kit = CreateSettingsKit(new AppSettings { Model = new ModelSettings { UseGpuAcceleration = false } });

        Assert.False(kit.Model.Model.UseGpuAcceleration);
    });

    // ---- The Context page -----------------------------------------------------------------------------------------

    [Fact]
    public void TheReserveForTheAnswerIsSavedAndTheFileLimitsAreShownInMegabytes() => RunSta(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Context;

        Assert.Equal("1,024", page.ReservedForAnswer.Text);
        Assert.Equal("50", page.MaxFileSizeMegabytes.Text);

        page.ReservedForAnswer.Text = "2000";
        page.MaxFileSizeMegabytes.Text = "20";
        kit.Settle();

        Assert.Equal(2000, kit.Saved.ContextLimits.ReservedOutputTokens);
        Assert.Equal(20L * 1024 * 1024, kit.Saved.ContextLimits.MaxFileSizeBytes);

        page.ReservedForAnswer.Text = "10";
        page.MaxAttachedFiles.Text = "0";
        page.MaxFileSizeMegabytes.Text = "5000";
        kit.Settle();
        Assert.True(page.ReservedForAnswer.HasError);
        Assert.True(page.MaxAttachedFiles.HasError);
        Assert.True(page.MaxFileSizeMegabytes.HasError);
        Assert.Equal(2000, kit.Saved.ContextLimits.ReservedOutputTokens);
        Assert.Equal(new ContextLimitSettings().MaxAttachedFiles, kit.Saved.ContextLimits.MaxAttachedFiles);
    }));

    // ---- The Hotkeys page -----------------------------------------------------------------------------------------

    [Fact]
    public void ANewShortcutIsRecordedAndSaved() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var editor = kit.Model.Hotkeys.SearchOrAsk;
        Assert.Equal("Alt + A", editor.DisplayText);

        editor.RecordCommand.Execute(null);
        Assert.True(editor.IsRecording);
        Assert.True(editor.TryAccept(HotkeyModifiers.Control | HotkeyModifiers.Alt, "q"));
        kit.Settle();

        Assert.False(editor.IsRecording);
        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Q"), kit.Saved.Hotkeys.SearchOrAsk);
        Assert.Equal("Ctrl + Alt + Q", editor.DisplayText);
    });

    [Fact]
    public void TheCopyShortcutCanBeChangedAndReportsAnotherShortcutsKeys() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var copy = kit.Model.Hotkeys.SelectedTextByCopy;
        Assert.Equal("Selected text by copy", copy.Title);

        // The keys of another shortcut are refused, naming it; and the others are refused its keys too.
        copy.RecordCommand.Execute(null);
        Assert.False(copy.TryAccept(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "W"));
        Assert.Contains("Selected text", copy.Problem, StringComparison.Ordinal);
        copy.CancelRecording();
        var other = kit.Model.Hotkeys.VisualIntelligence;
        other.RecordCommand.Execute(null);
        Assert.False(other.TryAccept(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "C"));
        Assert.Contains("Selected text by copy", other.Problem, StringComparison.Ordinal);
        other.CancelRecording();

        copy.RecordCommand.Execute(null);
        Assert.True(copy.TryAccept(HotkeyModifiers.Control | HotkeyModifiers.Alt, "k"));
        kit.Settle();
        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K"), kit.Saved.Hotkeys.SelectedTextByCopy);
        Assert.Equal(new HotkeySettings().SelectedTextActions, kit.Saved.Hotkeys.SelectedTextActions);
    });

    [Fact]
    public void KeysThatCannotWorkAreExplainedAndRecordingGoesOn() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var editor = kit.Model.Hotkeys.SearchOrAsk;
        editor.RecordCommand.Execute(null);

        Assert.False(editor.TryAccept(HotkeyModifiers.Shift, "Q"));
        Assert.Contains("Alt, Ctrl or the Windows key", editor.Problem, StringComparison.Ordinal);
        Assert.True(editor.HasProblem);
        Assert.True(editor.IsRecording);

        Assert.False(editor.TryAccept(HotkeyModifiers.Alt, null));
        Assert.Contains("can't be used", editor.Problem, StringComparison.Ordinal);

        Assert.False(editor.TryAccept(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "W"));
        Assert.Contains("Selected text", editor.Problem, StringComparison.Ordinal);

        kit.Settle();
        Assert.Equal(new HotkeySettings().SearchOrAsk, kit.Saved.Hotkeys.SearchOrAsk);

        editor.CancelRecording();
        Assert.False(editor.IsRecording);
        Assert.False(editor.HasProblem);
    });

    [Fact]
    public void AShortcutCanBeResetOrTurnedOff() => RunSta(() =>
    {
        var kit = CreateSettingsKit(new AppSettings
        {
            Hotkeys = new HotkeySettings { SearchOrAsk = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K") },
        });
        var editor = kit.Model.Hotkeys.SearchOrAsk;
        Assert.True(editor.ResetCommand.CanExecute(null));

        editor.ResetCommand.Execute(null);
        kit.Settle();
        Assert.Equal(new HotkeySettings().SearchOrAsk, kit.Saved.Hotkeys.SearchOrAsk);
        Assert.False(editor.ResetCommand.CanExecute(null));

        editor.TurnOffCommand.Execute(null);
        kit.Settle();
        Assert.Null(kit.Saved.Hotkeys.SearchOrAsk);
        Assert.Equal("Off", editor.DisplayText);
        Assert.False(editor.TurnOffCommand.CanExecute(null));
        Assert.True(editor.ResetCommand.CanExecute(null));
    });

    [Fact]
    public void KeysAreNamedTheWayTheSettingsNameThem()
    {
        Assert.Equal("A", HotkeyKeyMap.NameOf(Key.A));
        Assert.Equal("Z", HotkeyKeyMap.NameOf(Key.Z));
        Assert.Equal("5", HotkeyKeyMap.NameOf(Key.D5));
        Assert.Equal("F1", HotkeyKeyMap.NameOf(Key.F1));
        Assert.Equal("F24", HotkeyKeyMap.NameOf(Key.F24));
        Assert.Equal("Space", HotkeyKeyMap.NameOf(Key.Space));
        Assert.Equal("Backspace", HotkeyKeyMap.NameOf(Key.Back));
        Assert.Equal("PageDown", HotkeyKeyMap.NameOf(Key.Next));
        Assert.Null(HotkeyKeyMap.NameOf(Key.NumPad5));
        Assert.Null(HotkeyKeyMap.NameOf(Key.LeftShift));
        Assert.Null(HotkeyKeyMap.NameOf(Key.MediaPlayPause));
        Assert.All(
            Enumerable.Range((int)Key.A, 26).Select(key => HotkeyKeyMap.NameOf((Key)key)),
            name => Assert.True(HotkeyNames.IsValid(name)));

        Assert.Equal(
            HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows,
            HotkeyKeyMap.ModifiersOf(ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows));
        Assert.Equal(HotkeyModifiers.None, HotkeyKeyMap.ModifiersOf(ModifierKeys.None));
    }

    // ---- Voice, integrations, About -------------------------------------------------------------------------------

    [Fact]
    public void TheVoicePageOffersThePlannedThreeModelsAndTheKiriWakeWord() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Voice;

        Assert.Equal(["KittenTTS Mini 0.8 (80M)", "Kokoro-82M ONNX", "Piper"], page.Models.Select(model => model.DisplayName));
        Assert.Equal("KittenTTS Mini 0.8 (80M)", page.SelectedModel.DisplayName);
        Assert.Equal("Kiri", page.WakeWord);
        Assert.False(page.WakeWordEnabled);
    });

    [Fact]
    public void TheVoiceChoicesAreSavedWhenTheyAreMade() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var page = kit.Model.Voice;

        page.SelectedModel = TextToSpeechModels.Piper;
        page.WakeWordEnabled = true;
        kit.Settle();

        Assert.Equal("piper", kit.Saved.Voice.TextToSpeechModelId);
        Assert.True(kit.Saved.Voice.WakeWordEnabled);
    });

    [Fact]
    public void TheAboutPageNamesTheFoldersTheSettingsLiveIn() => RunSta(() =>
    {
        var page = CreateSettingsKit().Model.About;

        Assert.Equal("Assistant", page.Name);
        Assert.Equal(SettingsPaths.RootDirectory, page.DataFolder);
        Assert.Equal(SettingsPaths.SettingsFilePath, page.SettingsFile);
        Assert.NotEmpty(page.Version);
        Assert.Null(typeof(AboutPage).GetProperty("Promise"));
    });

    [Fact]
    public void ThePrivacyPageShowsHistoryRetentionAsTheFourChoices() => RunSta(() =>
    {
        var page = CreateSettingsKit().Model.Privacy;

        Assert.Equal(
            ["Until I delete it", "90 days", "30 days", "7 days"], page.Retentions.Select(choice => choice.Label));
        Assert.Equal(HistoryRetention.UntilDeleted, page.Retention.Value);
        Assert.False(page.HasExcludedFolders);
    });

    [Fact]
    public void WhatTheWindowChangesIsInTheFileAndSurvivesARestart() => RunSta(() => WithCulture("en-US", () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "assistant-settings-ui-tests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "settings.json");
        try
        {
            using (var settings = new Assistant.Data.Settings.JsonSettingsService(
                file, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Assistant.Data.Settings.JsonSettingsService>.Instance))
            {
                var hardware = new FixedHardware(new HardwareInfo(16 * SettingsGiB, 8));
                var catalog = new ModelProfileCatalog();
                var model = new SettingsViewModel(
                    settings, catalog, new ModelProfileResolver(catalog, hardware, SettingsPaths, _ => false), hardware,
                    new SettingsLifecycle(), new Assistant.Core.Events.AppEventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<Assistant.Core.Events.AppEventBus>.Instance),
                    SettingsPaths, settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                SettingsWait(model.LoadAsync());
                Assert.False(model.HasNotice);

                model.Privacy.HistoryEnabled = false;
                model.Model.NormalLimit.Text = "12000";
                model.Model.SelectedProfile = model.Model.Profiles[1];
                model.Hotkeys.SearchOrAsk.RecordCommand.Execute(null);
                model.Hotkeys.SearchOrAsk.TryAccept(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K");
                model.Model.UseCustomWindow = true;
                model.Model.CustomWindow.Text = "300000";
                SettingsWait(model.WhenSavedAsync());
                Assert.False(model.HasNotice);
                model.Dispose();
            }

            // A new start reads the same file.
            using var restarted = new Assistant.Data.Settings.JsonSettingsService(
                file, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Assistant.Data.Settings.JsonSettingsService>.Instance);
            var saved = SettingsResult(restarted.LoadAsync());
            Assert.Equal(SettingsLoadOutcome.Loaded, restarted.Outcome);
            Assert.False(saved.Privacy.HistoryEnabled);
            Assert.Equal(12000, saved.ContextLimits.NormalContextTokens);
            Assert.Equal("chat-9b", saved.Model.ProfileId);
            Assert.Equal(300_000, saved.Model.ContextLength);
            Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K"), saved.Hotkeys.SearchOrAsk);
            var text = File.ReadAllText(file);
            Assert.Contains("\"historyEnabled\": false", text, StringComparison.Ordinal);
            Assert.Contains("\"contextLength\": 300000", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }));

    // ---- The demo model window, which saves the files it loads into the same settings ---------------------------

    [Fact]
    public void TheDemoModelWindowStillLoadsTheModelWhenItsSettingsCannotBeWritten() => RunSta(() =>
    {
        var lifecycle = new FakeModelLifecycle();
        var bus = new Assistant.Core.Events.AppEventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<Assistant.Core.Events.AppEventBus>.Instance);
        var settings = new RecordingSettings { SaveFailure = new IOException("disk full") };
        using var model = new Assistant.UI.ViewModels.ModelStatusViewModel(lifecycle, bus, settings)
        {
            ModelFilePath = @"C:\Models.gguf",
        };

        model.LoadAsync().GetAwaiter().GetResult();

        Assert.Single(lifecycle.Loads);
        Assert.Contains("couldn't be saved", model.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("disk full", model.Notice, StringComparison.Ordinal);
    });

    [Fact]
    public void TheDemoModelWindowExplainsAPathTheSettingsRefuseInsteadOfFailing() => RunSta(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "assistant-settings-ui-tests", Guid.NewGuid().ToString("N"));
        var lifecycle = new FakeModelLifecycle();
        var bus = new Assistant.Core.Events.AppEventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<Assistant.Core.Events.AppEventBus>.Instance);
        using var settings = new Assistant.Data.Settings.JsonSettingsService(
            Path.Combine(folder, "settings.json"), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Assistant.Data.Settings.JsonSettingsService>.Instance);
        try
        {
            using var model = new Assistant.UI.ViewModels.ModelStatusViewModel(lifecycle, bus, settings)
            {
                ModelFilePath = @"C:\Modelsad|name.gguf",
            };

            SettingsWait(model.LoadAsync());

            Assert.Empty(lifecycle.Loads);
            Assert.Contains("can't be used", model.Notice, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(folder, "settings.json")));

            // A path that can be kept is saved in the file, and the model loads.
            model.ModelFilePath = @"C:\Models\good.gguf";
            SettingsWait(model.LoadAsync());
            Assert.Single(lifecycle.Loads);
            Assert.Equal(@"C:\Models\good.gguf", SettingsResult(settings.LoadAsync()).Model.ModelFilePath);
            Assert.Contains("\"modelFilePath\"", File.ReadAllText(Path.Combine(folder, "settings.json")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    });

    // Settings the test controls: it counts what is loaded and saved, can fail or slow a save, and never touches the disk.
    private sealed class RecordingSettings : ISettingsService
    {
        private AppSettings _current = new();
        private int _saving;

        public int Loads { get; private set; }

        public int Saves { get; private set; }

        public int MostSavesAtOnce { get; private set; }

        public TimeSpan SaveDelay { get; init; }

        public Exception? SaveFailure { get; set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.FromResult(_current);
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Saves++;
            MostSavesAtOnce = Math.Max(MostSavesAtOnce, ++_saving);
            try
            {
                if (SaveDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SaveDelay, cancellationToken).ConfigureAwait(true);
                }

                if (SaveFailure is { } failure)
                {
                    throw failure;
                }

                _current = settings;
            }
            finally
            {
                _saving--;
            }
        }
    }

    private sealed class FixedReport(SettingsLoadOutcome outcome) : ISettingsLoadReport
    {
        public SettingsLoadOutcome Outcome => outcome;
    }
}
