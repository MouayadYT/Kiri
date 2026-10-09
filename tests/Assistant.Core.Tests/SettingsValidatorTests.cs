using System.Reflection;
using System.Text.RegularExpressions;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What a setting may hold (PROJECT_SPEC §5.10): the same rules for the file, the service and the window.</summary>
public sealed class SettingsValidatorTests
{
    [Fact]
    public void SpeechRecognitionSelectionIsValidatedWithoutChangingVoiceDefaults()
    {
        foreach (var id in new[] { "handy", "windows", "asr-parakeet-v3", "asr-whisper-small" })
            Assert.Empty(SettingsValidator.Validate(new AppSettings { Voice = new() { SpeechRecognitionModelId = id } }));
        var invalid = new AppSettings { Voice = new() { SpeechRecognitionModelId = "unknown", SpeechRecognitionDevice = "cpu --other-argument" } };
        var repaired = SettingsValidator.Sanitize(invalid, out var issues);
        Assert.Equal("speech-recognition", repaired.Voice.SpeechRecognitionModelId); Assert.Equal("cpu", repaired.Voice.SpeechRecognitionDevice);
        Assert.Equal(2, issues.Count);
    }
    [Fact]
    public void WebSearchStartsOffAndInvalidProvidersAreRepairedWithoutEnablingIt()
    {
        Assert.False(new AppSettings().WebSearch.Enabled);
        var invalid = new AppSettings { WebSearch = new() { Enabled = true, Provider = (WebSearchProvider)999 } };
        var repaired = SettingsValidator.Sanitize(invalid, out var issues);
        Assert.False(repaired.WebSearch.Enabled);
        Assert.Equal(WebSearchProvider.Exa, repaired.WebSearch.Provider);
        Assert.Contains(issues, issue => issue.Setting == "WebSearch.Provider");
        Assert.False(SettingsValidator.Sanitize(new AppSettings { WebSearch = null! }, out _).WebSearch.Enabled);
    }
    [Fact]
    public void GameModeStartsOffAndAMissingSectionIsReplacedWithoutTurningItOn()
    {
        var defaults = new AppSettings().GameMode;
        Assert.False(defaults.Games);
        Assert.False(defaults.CreativeApps);
        Assert.True(defaults.ReleaseSharedRecognizer);
        Assert.Empty(SettingsValidator.Validate(new AppSettings { GameMode = new() { Games = true, CreativeApps = true, ReleaseSharedRecognizer = false } }));

        // A settings file from before game mode has no such section.
        var repaired = SettingsValidator.Sanitize(new AppSettings { GameMode = null! }, out var issues);
        Assert.False(repaired.GameMode.Games);
        Assert.False(repaired.GameMode.CreativeApps);
        Assert.True(repaired.GameMode.ReleaseSharedRecognizer);
        Assert.Contains(issues, issue => issue.Setting == "GameMode");
    }
    [Fact]
    public void HardwareSelectionsRoundTripAndMalformedDeviceArgumentsAreRejected()
    {
        var settings = new AppSettings { Model = new ModelSettings { GpuDeviceId = "Vulkan1" },
            Voice = new VoiceSettings { TextToSpeechDevice = "cuda:GPU-2159ba6f-c85b-83b7-b209-fe7b2cf7fc06" } };
        Assert.Empty(SettingsValidator.Validate(settings));
        Assert.Empty(SettingsValidator.Validate(SettingsValidator.Sanitize(settings, out _)));
        var bad = settings with { Model = settings.Model with { GpuDeviceId = "Vulkan0 --host external" }, Voice = settings.Voice with { TextToSpeechDevice = "cuda:0" } };
        Assert.Equal(new[] { "Model.GpuDeviceId", "Voice.TextToSpeechDevice" }, SettingsValidator.Validate(bad).Select(issue => issue.Setting));
        var sanitized = SettingsValidator.Sanitize(bad, out _);
        Assert.Null(sanitized.Model.GpuDeviceId);
        Assert.Equal("cpu", sanitized.Voice.TextToSpeechDevice);
    }

    [Fact]
    public void TheDefaultsAreAllValid()
    {
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
        Assert.Empty(SettingsValidator.Validate(new AppSettings
        {
            Hotkeys = new HotkeySettings { SearchOrAsk = null, SelectedTextActions = null, VisualIntelligence = null },
        }));
    }

    [Fact]
    public void ValidSettingsAreReturnedUnchanged()
    {
        var settings = new AppSettings
        {
            Model = new ModelSettings { ContextLength = 65_536, ModelFilePath = @"C:\Models\a.gguf", ProfileId = "chat-9b" },
            ContextLimits = new ContextLimitSettings { NormalContextTokens = 0, HeavyContextTokens = 1_048_576, ReservedOutputTokens = 0 },
            Privacy = new PrivacySettings { ExcludedFolders = [@"C:\Private"] },
        };

        var sanitized = SettingsValidator.Sanitize(settings, out var repaired);

        Assert.Empty(repaired);
        Assert.Equal(settings.Model, sanitized.Model);
        Assert.Equal(settings.ContextLimits, sanitized.ContextLimits);
        Assert.Same(settings.Privacy.ExcludedFolders, sanitized.Privacy.ExcludedFolders);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void ResultsPerGroupStayWithinOneAndTen(int value, bool allowed) =>
        AssertRange(settings => settings with { Ui = settings.Ui with { BarResultsPerGroup = value } }, "Ui.BarResultsPerGroup", allowed);

    [Theory]
    [InlineData(255, false)]
    [InlineData(256, true)]
    [InlineData(1_048_576, true)]
    [InlineData(1_048_577, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void ACustomContextWindowStaysWithinWhatTheEngineTakes(int value, bool allowed) =>
        AssertRange(settings => settings with { Model = settings.Model with { ContextLength = value } }, "Model.ContextLength", allowed);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(255, false)]
    [InlineData(20_000, true)]
    [InlineData(1_048_577, false)]
    public void NormalAndHeavyLimitsAreZeroForNoLimitOrWithinTheWindowRange(int value, bool allowed)
    {
        AssertRange(settings => settings with { ContextLimits = settings.ContextLimits with { NormalContextTokens = value } }, "ContextLimits.NormalContextTokens", allowed);
        AssertRange(settings => settings with { ContextLimits = settings.ContextLimits with { HeavyContextTokens = value } }, "ContextLimits.HeavyContextTokens", allowed);
    }

    [Theory]
    [InlineData(-5, false)]
    [InlineData(0, true)]
    [InlineData(31, false)]
    [InlineData(32, true)]
    [InlineData(65_536, true)]
    [InlineData(65_537, false)]
    public void TheReserveForTheAnswerIsZeroForTheDefaultOrWithinItsRange(int value, bool allowed) =>
        AssertRange(settings => settings with { ContextLimits = settings.ContextLimits with { ReservedOutputTokens = value } }, "ContextLimits.ReservedOutputTokens", allowed);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void AttachedFilesStayWithinOneAndAHundred(int value, bool allowed) =>
        AssertRange(settings => settings with { ContextLimits = settings.ContextLimits with { MaxAttachedFiles = value } }, "ContextLimits.MaxAttachedFiles", allowed);

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1024L * 1024, true)]
    [InlineData(1024L * 1024 * 1024, true)]
    [InlineData(1024L * 1024 * 1024 + 1, false)]
    public void TheLargestFileSizeStaysWithinAMegabyteAndAGigabyte(long value, bool allowed) =>
        AssertRange(settings => settings with { ContextLimits = settings.ContextLimits with { MaxFileSizeBytes = value } }, "ContextLimits.MaxFileSizeBytes", allowed);

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(24 * 3600, true)]
    [InlineData((24 * 3600) + 1, false)]
    public void TheIdleTimeStaysWithinHalfAMinuteAndADay(int seconds, bool allowed) =>
        AssertRange(settings => settings with { Model = settings.Model with { IdleUnloadTimeout = TimeSpan.FromSeconds(seconds) } }, "Model.IdleUnloadTimeout", allowed);

    [Fact]
    public void ARetentionThatIsNoChoiceIsReplacedByKeepingUntilDeleted()
    {
        var settings = new AppSettings { Privacy = new PrivacySettings { HistoryRetention = (HistoryRetention)12 } };

        var sanitized = SettingsValidator.Sanitize(settings, out var repaired);

        Assert.Equal(HistoryRetention.UntilDeleted, sanitized.Privacy.HistoryRetention);
        Assert.Equal(new SettingsIssue("Privacy.HistoryRetention", SettingsIssueKind.Invalid), Assert.Single(repaired));
    }

    [Theory]
    [InlineData(@"C:\Models\a.gguf", true)]
    [InlineData(@"\\server\share\a.gguf", true)]
    [InlineData(@"models\a.gguf", false)]
    [InlineData(@"C:model.gguf", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("C:\\Models\\a\0.gguf", false)]
    public void APathHasToBeCompleteAndWithoutForbiddenCharacters(string path, bool allowed)
    {
        var settings = new AppSettings { Model = new ModelSettings { ModelFilePath = path } };

        var sanitized = SettingsValidator.Sanitize(settings, out var repaired);

        Assert.Equal(allowed, repaired.Count == 0);
        Assert.Equal(allowed ? path : null, sanitized.Model.ModelFilePath);
    }

    [Fact]
    public void APathThatIsAbsentIsFine()
    {
        Assert.Empty(SettingsValidator.Validate(new AppSettings { Model = new ModelSettings { ModelFilePath = null, ModelsDirectory = null } }));
    }

    [Fact]
    public void ExcludedFoldersLoseTheBadOnesAndTheDuplicatesAndAreCapped()
    {
        var folders = new List<string> { @"C:\A", "relative", @"c:\a\", @"D:\B" };
        folders.AddRange(Enumerable.Range(0, 250).Select(number => $@"E:\{number}"));
        var settings = new AppSettings { Privacy = new PrivacySettings { ExcludedFolders = folders } };

        var sanitized = SettingsValidator.Sanitize(settings, out var repaired);

        Assert.Equal(SettingsLimits.MaxExcludedFolders, sanitized.Privacy.ExcludedFolders.Count);
        Assert.Equal([@"C:\A", @"D:\B"], sanitized.Privacy.ExcludedFolders.Take(2));
        Assert.Equal("Privacy.ExcludedFolders", Assert.Single(repaired).Setting);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Alt, "A", true)]
    [InlineData(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "W", true)]
    [InlineData(HotkeyModifiers.Control, "Space", true)]
    [InlineData(HotkeyModifiers.Windows, "F12", true)]
    [InlineData(HotkeyModifiers.Alt, "f24", true)]
    [InlineData(HotkeyModifiers.Shift, "A", false)]
    [InlineData(HotkeyModifiers.None, "A", false)]
    [InlineData(HotkeyModifiers.Alt, "F25", false)]
    [InlineData(HotkeyModifiers.Alt, "", false)]
    [InlineData(HotkeyModifiers.Alt, "AB", false)]
    [InlineData((HotkeyModifiers)16, "A", false)]
    public void AShortcutNeedsAKnownKeyAndAModifierOtherThanShiftAlone(HotkeyModifiers modifiers, string key, bool allowed) =>
        Assert.Equal(allowed, SettingsLimits.IsValidHotkey(new Hotkey(modifiers, key)));

    [Fact]
    public void ABadShortcutFallsBackToItsDefaultButOffStaysOff()
    {
        var settings = new AppSettings
        {
            Hotkeys = new HotkeySettings
            {
                SearchOrAsk = new Hotkey(HotkeyModifiers.Shift, "A"),
                SelectedTextActions = null,
                VisualIntelligence = new Hotkey(HotkeyModifiers.Alt, "Nope"),
            },
        };

        var sanitized = SettingsValidator.Sanitize(settings, out var repaired);

        Assert.Equal(new HotkeySettings().SearchOrAsk, sanitized.Hotkeys.SearchOrAsk);
        Assert.Null(sanitized.Hotkeys.SelectedTextActions);
        Assert.Equal(new HotkeySettings().VisualIntelligence, sanitized.Hotkeys.VisualIntelligence);
        Assert.Equal(["Hotkeys.SearchOrAsk", "Hotkeys.VisualIntelligence"], repaired.Select(issue => issue.Setting));
    }

    [Fact]
    public void TwoShortcutsThatAreTheSameConflictButAreNotChangedBehindTheUsersBack()
    {
        var settings = new AppSettings
        {
            Hotkeys = new HotkeySettings
            {
                SearchOrAsk = new Hotkey(HotkeyModifiers.Alt, "A"),
                SelectedTextActions = new Hotkey(HotkeyModifiers.Alt, "a"),
                VisualIntelligence = new Hotkey(HotkeyModifiers.Alt, "V"),
            },
        };

        var issues = SettingsValidator.Validate(settings);
        var sanitized = SettingsValidator.Sanitize(settings, out var repaired);

        Assert.Equal(new SettingsIssue("Hotkeys.SelectedTextActions", SettingsIssueKind.Conflict), Assert.Single(issues));
        Assert.Empty(repaired);
        Assert.Equal(settings.Hotkeys, sanitized.Hotkeys);
    }

    [Fact]
    public void TheCopyShortcutIsCheckedLikeTheOthers_AndItsDefaultDoesNotCollideWithThem()
    {
        var defaults = new HotkeySettings();
        Assert.Equal(new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "C"), defaults.SelectedTextByCopy);
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));

        // A bad one falls back to its default; off stays off.
        var bad = new AppSettings { Hotkeys = new HotkeySettings { SelectedTextByCopy = new Hotkey(HotkeyModifiers.Alt, "Nope") } };
        var sanitized = SettingsValidator.Sanitize(bad, out var repaired);
        Assert.Equal(defaults.SelectedTextByCopy, sanitized.Hotkeys.SelectedTextByCopy);
        Assert.Equal(["Hotkeys.SelectedTextByCopy"], repaired.Select(issue => issue.Setting));
        Assert.Null(SettingsValidator.Sanitize(new AppSettings { Hotkeys = new HotkeySettings { SelectedTextByCopy = null } }, out _).Hotkeys.SelectedTextByCopy);

        // The same keys as another shortcut are a conflict, named, and not changed behind the user's back.
        var same = new AppSettings { Hotkeys = new HotkeySettings { SelectedTextByCopy = new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "w") } };
        Assert.Equal(new SettingsIssue("Hotkeys.SelectedTextByCopy", SettingsIssueKind.Conflict), Assert.Single(SettingsValidator.Validate(same)));
    }

    [Fact]
    public void ATextToSpeechModelThatDoesNotExistFallsBackToTheDefault()
    {
        var settings = new AppSettings { Voice = new VoiceSettings { TextToSpeechModelId = "nope", WakeWordEnabled = true } };

        var sanitized = SettingsValidator.Sanitize(settings, out _);

        Assert.Equal(TextToSpeechModels.DefaultId, sanitized.Voice.TextToSpeechModelId);
        Assert.True(sanitized.Voice.WakeWordEnabled);
    }

    [Fact]
    public void ProfileAndPresetIdsMustBeWellFormedButNeedNotExistInTheCatalog()
    {
        var good = new AppSettings { Model = new ModelSettings { ProfileId = "my-custom-7b", HardwarePresetId = "performance" } };
        var bad = new AppSettings { Model = new ModelSettings { ProfileId = "Not Valid!", HardwarePresetId = "-x" } };

        Assert.Empty(SettingsValidator.Validate(good));
        var sanitized = SettingsValidator.Sanitize(bad, out var repaired);

        Assert.Null(sanitized.Model.ProfileId);
        Assert.Null(sanitized.Model.HardwarePresetId);
        Assert.Equal(2, repaired.Count);
    }

    [Fact]
    public void ASectionOrListThatIsMissingIsReplacedRatherThanFailing()
    {
        var settings = new AppSettings { Ui = null!, Privacy = new PrivacySettings { ExcludedFolders = null! }, Voice = null! };

        var sanitized = SettingsValidator.Sanitize(settings, out var repaired);

        Assert.NotNull(sanitized.Ui);
        Assert.Empty(sanitized.Privacy.ExcludedFolders);
        Assert.NotNull(sanitized.Voice);
        Assert.Equal(["Privacy.ExcludedFolders", "Ui", "Voice"], repaired.Select(issue => issue.Setting).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASchemaVersionThatIsNotTheCurrentOneIsCorrected()
    {
        var sanitized = SettingsValidator.Sanitize(new AppSettings { SchemaVersion = 7 }, out var repaired);

        Assert.Equal(AppSettings.CurrentSchemaVersion, sanitized.SchemaVersion);
        Assert.Equal("SchemaVersion", Assert.Single(repaired).Setting);
    }

    [Fact]
    public void IssuesNameTheSettingAndNeverItsValue()
    {
        var settings = new AppSettings { Model = new ModelSettings { ModelFilePath = "secret-relative-path\\a.gguf" } };

        var issue = Assert.Single(SettingsValidator.Validate(settings));

        Assert.DoesNotContain("secret", issue.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", new SettingsValidationException([issue]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSettingHoldsASecret()
    {
        // Tokens, passwords and keys belong in ISecretStore (Windows credential protection), never in a settings file:
        // a property that looks like one fails here until it is moved.
        var secretLike = new Regex("token|secret|password|passphrase|credential|apikey|api_key|privatekey|bearer", RegexOptions.IgnoreCase);
        var models = typeof(AppSettings).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(AppSettings).Namespace && type.IsClass && !type.IsAbstract);

        foreach (var type in models)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = property.Name;
                if (secretLike.IsMatch(name))
                {
                    // The context limits are counts of tokens of text, which are no secret.
                    Assert.True(
                        type == typeof(ContextLimitSettings) && name.EndsWith("Tokens", StringComparison.Ordinal),
                        $"{type.Name}.{name} looks like a secret; keep it in ISecretStore.");
                }
            }
        }
    }

    [Fact]
    public void TheSettingsFileLivesDirectlyInTheAppFolderAndIsNoDirectoryToCreate()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "assistant-paths-test"));

        Assert.Equal(Path.Combine(paths.RootDirectory, "settings.json"), paths.SettingsFilePath);
        Assert.DoesNotContain(paths.SettingsFilePath, paths.Directories);
    }

    private static void AssertRange(Func<AppSettings, AppSettings> change, string setting, bool allowed)
    {
        var settings = change(new AppSettings());

        var issues = SettingsValidator.Validate(settings);

        if (allowed)
        {
            Assert.Empty(issues);
        }
        else
        {
            var issue = Assert.Single(issues);
            Assert.Equal(setting, issue.Setting);
            Assert.Equal(SettingsIssueKind.OutOfRange, issue.Kind);
            var sanitized = SettingsValidator.Sanitize(settings, out _);
            Assert.Empty(SettingsValidator.Validate(sanitized));
        }
    }
}
