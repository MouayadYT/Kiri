using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Data.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>The settings file (PROJECT_SPEC §5.10): what is saved comes back, and what is damaged never costs the app or the rest of the settings.</summary>
public sealed class JsonSettingsServiceTests : IDisposable
{
    [Fact]
    public async Task SearchChoiceSurvivesRestartAndOldSettingsDoNotEnableSearch()
    {
        _folder.WriteFile("""{"schemaVersion":3,"ui":{"firstRunCompleted":true}}""");
        var old = await _folder.CreateService().LoadAsync();
        Assert.False(old.WebSearch.Enabled);
        await _folder.CreateService().SaveAsync(old with { WebSearch = new() { Enabled = true, Provider = WebSearchProvider.Tavily } });
        var saved = await _folder.CreateService().LoadAsync();
        Assert.True(saved.WebSearch.Enabled);
        Assert.Equal(WebSearchProvider.Tavily, saved.WebSearch.Provider);
        Assert.True(saved.Ui.FirstRunCompleted);
    }
    private readonly SettingsFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(1, 8192)]
    [InlineData(2, 8192)]
    [InlineData(2, null)]
    [InlineData(3, 4096)]
    [InlineData(3, null)]
    public async Task ThePreviousDefaultsMigrateToTheTwoWindowsOnceAndLaterChoicesSurviveARestart(int version, int? window)
    {
        // What each build shipped: 32,768 for everything, then 8,192, then 4,096, each with its window written down beside it.
        var shipped = version switch { 1 => 32768, 2 => 8192, _ => 4096 };
        _folder.WriteFile(JsonSerializer.Serialize(new
        {
            schemaVersion = version,
            model = new { contextLength = window },
            contextLimits = new { normalContextTokens = shipped, heavyContextTokens = shipped, reservedOutputTokens = version == 1 ? 4096 : 1024 },
        }));
        var service = _folder.CreateService();
        var migrated = await service.LoadAsync();

        // Now: 8,000 tokens for a chat and 32,000 for a conversation with files, and no window written down, so that the model is loaded with
        // whichever of the two the conversation needs.
        Assert.Equal(8000, migrated.ContextLimits.NormalContextTokens);
        Assert.Equal(32000, migrated.ContextLimits.HeavyContextTokens);
        Assert.Null(migrated.Model.ContextLength);
        Assert.Equal(1024, migrated.ContextLimits.ReservedOutputTokens);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrated.SchemaVersion);
        await service.SaveAsync(migrated with
        {
            Model = migrated.Model with { ContextLength = 8192 },
            ContextLimits = migrated.ContextLimits with { NormalContextTokens = 8192, HeavyContextTokens = 8192 },
        });
        var restarted = await _folder.CreateService().LoadAsync();
        Assert.Equal(8192, restarted.ContextLimits.NormalContextTokens);
        Assert.Equal(8192, restarted.ContextLimits.HeavyContextTokens);
        Assert.Equal(8192, restarted.Model.ContextLength);
    }

    [Theory]
    [InlineData(16384, 4096, 4096)]
    [InlineData(4096, 6000, 4096)]
    [InlineData(4096, 4096, 12000)]
    public async Task CustomContextSettingsArePreservedWhenTheTwoWindowsCome(int window, int normal, int heavy)
    {
        _folder.WriteFile($$"""{ "schemaVersion": 3, "model": { "contextLength": {{window}} }, "contextLimits": { "normalContextTokens": {{normal}}, "heavyContextTokens": {{heavy}} } }""");
        var saved = await _folder.CreateService().LoadAsync();
        Assert.Equal(window, saved.Model.ContextLength);
        Assert.Equal(normal, saved.ContextLimits.NormalContextTokens);
        Assert.Equal(heavy, saved.ContextLimits.HeavyContextTokens);
    }

    [Theory]
    [InlineData(16384, 8192, 8192)]
    [InlineData(8192, 6000, 8192)]
    [InlineData(8192, 8192, 12000)]
    public async Task CustomContextSettingsArePreservedDuringThe4KMigration(int window, int normal, int heavy)
    {
        _folder.WriteFile($$"""{ "schemaVersion": 2, "model": { "contextLength": {{window}} }, "contextLimits": { "normalContextTokens": {{normal}}, "heavyContextTokens": {{heavy}} } }""");
        var saved = await _folder.CreateService().LoadAsync();
        Assert.Equal(window, saved.Model.ContextLength);
        Assert.Equal(normal, saved.ContextLimits.NormalContextTokens);
        Assert.Equal(heavy, saved.ContextLimits.HeavyContextTokens);
    }

    [Fact]
    public async Task ACustomWindowKeepsLargerLegacyContextLimits()
    {
        _folder.WriteFile("""{ "schemaVersion": 1, "model": { "contextLength": 65536 }, "contextLimits": { "normalContextTokens": 32768, "heavyContextTokens": 32768 } }""");
        var saved = await _folder.CreateService().LoadAsync();
        Assert.Equal(65536, saved.Model.ContextLength);
        Assert.Equal(32768, saved.ContextLimits.NormalContextTokens);
        Assert.Equal(32768, saved.ContextLimits.HeavyContextTokens);
    }

    [Fact]
    public async Task WithoutAFileTheDefaultsAreUsedAndNothingIsCreated()
    {
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.NoFile, service.Outcome);
        AssertSame(new AppSettings(), settings);
        Assert.False(Directory.Exists(_folder.Root));
    }

    [Fact]
    public void ItReportsThatNothingWasReadBeforeTheFirstRead()
    {
        Assert.Equal(SettingsLoadOutcome.NotLoaded, _folder.CreateService().Outcome);
    }

    [Fact]
    public async Task EverySettingIsKeptAcrossARestart()
    {
        var saved = SettingsFolder.Customized();
        await _folder.CreateService().SaveAsync(saved);

        var restarted = _folder.CreateService();
        var loaded = await restarted.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, restarted.Outcome);
        AssertSame(saved, loaded);
        Assert.Null(loaded.Hotkeys.SelectedTextActions);
        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Space"), loaded.Hotkeys.SearchOrAsk);
        Assert.Equal([@"C:\Private", @"D:\Taxes"], loaded.Privacy.ExcludedFolders);
        Assert.Equal(TimeSpan.FromMinutes(45), loaded.Model.IdleUnloadTimeout);
    }

    [Fact]
    public void TheCustomizedSettingsChangeEverySettingSoTheRoundTripCoversEachOne()
    {
        // A new setting that is not added to SettingsFolder.Customized would go untested: this fails until it is.
        var customized = SettingsFolder.Customized();
        var defaults = new AppSettings();
        var sections = typeof(AppSettings).GetProperties()
            .Where(property => property.PropertyType.Namespace == typeof(AppSettings).Namespace);
        foreach (var section in sections)
        {
            foreach (var setting in section.PropertyType.GetProperties())
            {
                var changed = JsonSerializer.Serialize(setting.GetValue(section.GetValue(customized)));
                var normal = JsonSerializer.Serialize(setting.GetValue(section.GetValue(defaults)));
                Assert.True(changed != normal, $"{section.Name}.{setting.Name} is not changed by Customized().");
            }
        }
    }

    [Fact]
    public async Task TheFileIsReadableVersionedJsonWithShortcutsThatAreOffWrittenAsNull()
    {
        await _folder.CreateService().SaveAsync(SettingsFolder.Customized());

        var text = await File.ReadAllTextAsync(_folder.FilePath);
        var bytes = await File.ReadAllBytesAsync(_folder.FilePath);

        Assert.Contains("\"schemaVersion\": 4", text, StringComparison.Ordinal);
        Assert.Contains("\"selectedTextActions\": null", text, StringComparison.Ordinal);
        Assert.Contains("\n", text, StringComparison.Ordinal);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(["settings.json"], _folder.Files());
    }

    [Fact]
    public async Task AShortcutThatIsOffStaysOffButOneThatIsMissingGetsItsDefault()
    {
        _folder.WriteFile("""{ "schemaVersion": 1, "hotkeys": { "searchOrAsk": null } }""");

        var settings = await _folder.CreateService().LoadAsync();

        Assert.Null(settings.Hotkeys.SearchOrAsk);
        Assert.Equal(new HotkeySettings().SelectedTextActions, settings.Hotkeys.SelectedTextActions);
        Assert.Equal(new HotkeySettings().VisualIntelligence, settings.Hotkeys.VisualIntelligence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "schemaVersion": "one" }""")]
    [InlineData("""{ "schemaVersion": 0 }""")]
    [InlineData("""{ "schemaVersion": 1, """)]
    public async Task AFileThatIsNotASettingsDocumentIsSetAsideAndTheDefaultsAreUsed(string content)
    {
        _folder.WriteFile(content);
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.ResetToDefaults, service.Outcome);
        AssertSame(new AppSettings(), settings);
        var copy = Assert.Single(_folder.Files());
        Assert.StartsWith("settings.corrupt-20260930T101500000", copy, StringComparison.Ordinal);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(_folder.Root, copy)));
        Assert.False(File.Exists(_folder.FilePath));
    }

    [Fact]
    public async Task AFileOverAMegabyteIsNotReadAtAll()
    {
        _folder.WriteFile("{ \"schemaVersion\": 1, \"padding\": \"" + new string('x', 1024 * 1024) + "\" }");
        var service = _folder.CreateService();

        await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.ResetToDefaults, service.Outcome);
    }

    [Fact]
    public async Task ADamagedFileIsReplacedByTheLastGoodCopy()
    {
        var first = SettingsFolder.Customized();
        var second = first with { Ui = first.Ui with { BarResultsPerGroup = 7 } };
        var writer = _folder.CreateService();
        await writer.SaveAsync(first);
        await writer.SaveAsync(second);
        await File.WriteAllTextAsync(_folder.FilePath, "{ damaged");

        var service = _folder.CreateService();
        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.RestoredFromBackup, service.Outcome);
        AssertSame(first, settings);
        Assert.Contains(_folder.Files(), name => name.StartsWith("settings.corrupt-", StringComparison.Ordinal));

        // The good copy is the settings file again, so the next start reads it directly.
        var next = _folder.CreateService();
        AssertSame(first, await next.LoadAsync());
        Assert.Equal(SettingsLoadOutcome.Loaded, next.Outcome);
    }

    [Fact]
    public async Task AGoodCopyThatIsAlsoDamagedLeavesTheDefaults()
    {
        _folder.WriteFile("{ damaged");
        await File.WriteAllTextAsync(_folder.BackupPath, "{ also damaged");
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.ResetToDefaults, service.Outcome);
        AssertSame(new AppSettings(), settings);
    }

    [Fact]
    public async Task OnlyTheValueThatCannotBeReadIsReplaced()
    {
        _folder.WriteFile("""
            {
              "schemaVersion": 1,
              "privacy": { "historyEnabled": false, "historyRetention": "SomeFutureValue" },
              "contextLimits": { "normalContextTokens": "lots", "heavyContextTokens": 20000 },
              "model": { "useGpuAcceleration": false }
            }
            """);
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Repaired, service.Outcome);
        Assert.False(settings.Privacy.HistoryEnabled);
        Assert.Equal(HistoryRetention.UntilDeleted, settings.Privacy.HistoryRetention);
        Assert.Equal(ContextLimitSettings.Roomy.NormalContextTokens, settings.ContextLimits.NormalContextTokens);
        Assert.Equal(20000, settings.ContextLimits.HeavyContextTokens);
        Assert.False(settings.Model.UseGpuAcceleration);
    }

    [Fact]
    public async Task ValuesOutsideTheirRangeAreReplacedAndTheRestKept()
    {
        _folder.WriteFile("""
            {
              "schemaVersion": 1,
              "ui": { "barResultsPerGroup": 400, "filesScopeOnByDefault": true },
              "model": { "contextLength": 12, "modelFilePath": "relative\\model.gguf", "idleUnloadTimeout": "00:00:02" },
              "hotkeys": { "searchOrAsk": { "modifiers": "Shift", "key": "A" }, "visualIntelligence": null },
              "privacy": { "excludedFolders": [ "C:\\Keep", "relative", "c:\\keep\\" ] },
              "voice": { "textToSpeechModelId": "no-such-model" }
            }
            """);
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Repaired, service.Outcome);
        Assert.Equal(new UiSettings().BarResultsPerGroup, settings.Ui.BarResultsPerGroup);
        Assert.True(settings.Ui.FilesScopeOnByDefault);
        Assert.Null(settings.Model.ContextLength);
        Assert.Null(settings.Model.ModelFilePath);
        Assert.Equal(new ModelSettings().IdleUnloadTimeout, settings.Model.IdleUnloadTimeout);
        Assert.Equal(new HotkeySettings().SearchOrAsk, settings.Hotkeys.SearchOrAsk);
        Assert.Null(settings.Hotkeys.VisualIntelligence);
        Assert.Equal([@"C:\Keep"], settings.Privacy.ExcludedFolders);
        Assert.Equal(TextToSpeechModels.DefaultId, settings.Voice.TextToSpeechModelId);
    }

    [Theory]
    [InlineData(4096, 16384, 1024, 4096, 16384, 1024)]
    [InlineData(4096, 16384, 5000, 4096, 16384, 5000)]
    [InlineData(5000, 16384, 5000, 5000, 16384, 5000)]
    [InlineData(12000, 20000, 1024, 12000, 20000, 1024)]
    [InlineData(16000, 24000, 3000, 16000, 24000, 3000)]
    public async Task ContextLimitsTheUserChoseAreKept(
        int normal, int heavy, int reserved, int expectedNormal, int expectedHeavy, int expectedReserved)
    {
        _folder.WriteFile($$"""
            {
              "schemaVersion": 1,
              "contextLimits": { "normalContextTokens": {{normal}}, "heavyContextTokens": {{heavy}}, "reservedOutputTokens": {{reserved}} }
            }
            """);

        var settings = await _folder.CreateService().LoadAsync();

        Assert.Equal(expectedNormal, settings.ContextLimits.NormalContextTokens);
        Assert.Equal(expectedHeavy, settings.ContextLimits.HeavyContextTokens);
        Assert.Equal(expectedReserved, settings.ContextLimits.ReservedOutputTokens);
    }

    [Fact]
    public void ANewSettingsFileGivesAChatTheWholeWindowAndALongAnswer()
    {
        var limits = new AppSettings().ContextLimits;

        Assert.Equal(8000, limits.NormalContextTokens);
        Assert.Equal(32000, limits.HeavyContextTokens);
        Assert.Equal(1024, limits.ReservedOutputTokens);
        Assert.Null(new AppSettings().Model.ContextLength);
    }

    [Fact]
    public async Task ASectionThatIsNotAnObjectIsReplacedAndTheOtherSectionsKept()
    {
        _folder.WriteFile("""{ "schemaVersion": 1, "ui": 5, "privacy": { "historyEnabled": false } }""");
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Repaired, service.Outcome);
        AssertSame(new UiSettings(), settings.Ui);
        Assert.False(settings.Privacy.HistoryEnabled);
    }

    [Fact]
    public async Task UnknownMembersCommentsAndTrailingCommasAreTolerated()
    {
        _folder.WriteFile("""
            {
              // written by hand
              "schemaVersion": 1,
              "Privacy": { "HistoryEnabled": false, "somethingNew": 1, },
              "aFutureSection": { "a": 1 },
            }
            """);
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();

        Assert.Equal(SettingsLoadOutcome.Loaded, service.Outcome);
        Assert.False(settings.Privacy.HistoryEnabled);
    }

    [Fact]
    public async Task AFileFromANewerBuildIsReadAndCopiedAsideBeforeItIsReplaced()
    {
        var original = """{ "schemaVersion": 99, "privacy": { "historyEnabled": false }, "aFutureSection": { "a": 1 } }""";
        _folder.WriteFile(original);
        var service = _folder.CreateService();

        var settings = await service.LoadAsync();
        Assert.False(settings.Privacy.HistoryEnabled);
        await service.SaveAsync(settings with { Ui = settings.Ui with { BarResultsPerGroup = 4 } });
        await service.SaveAsync(settings with { Ui = settings.Ui with { BarResultsPerGroup = 5 } });

        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_folder.Root, "settings.from-v99.json")));
        Assert.Contains("\"schemaVersion\": 4", await File.ReadAllTextAsync(_folder.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderFileIsBroughtUpOneVersionAtATime()
    {
        var order = new List<int>();
        SettingsMigration[] migrations =
        [
            new(1, document =>
            {
                order.Add(1);
                document["ui"] = new JsonObject { ["barResultsPerGroup"] = document["oldName"]!.GetValue<int>() };
                document.Remove("oldName");
            }),
            new(2, _ => order.Add(2)),
        ];

        var read = SettingsFileFormat.TryParse(Bytes("""{ "schemaVersion": 1, "oldName": 6 }"""), migrations, out var parsed, currentVersion: 3);

        Assert.True(read);
        Assert.Equal([1, 2], order);
        Assert.Equal(6, parsed!.Settings.Ui.BarResultsPerGroup);
        Assert.Equal(1, parsed.FileVersion);
    }

    [Fact]
    public void AnOlderFileWithAMissingStepIsRefusedRatherThanGuessedAt()
    {
        SettingsMigration[] migrations = [new(2, _ => { })];

        Assert.False(SettingsFileFormat.TryParse(Bytes("""{ "schemaVersion": 1 }"""), migrations, out var parsed, currentVersion: 3));
        Assert.Null(parsed);
    }

    [Fact]
    public void AStepThatFailsRefusesTheFile()
    {
        SettingsMigration[] migrations = [new(1, _ => throw new InvalidOperationException())];

        Assert.False(SettingsFileFormat.TryParse(Bytes("""{ "schemaVersion": 1 }"""), migrations, out _, currentVersion: 2));
    }

    [Fact]
    public async Task SettingsThatAreNotAllowedAreRefusedBeforeAnythingIsWritten()
    {
        var service = _folder.CreateService();
        var good = SettingsFolder.Customized();
        await service.SaveAsync(good);
        var before = await File.ReadAllBytesAsync(_folder.FilePath);

        var refused = await Assert.ThrowsAsync<SettingsValidationException>(() => service.SaveAsync(
            good with { Model = good.Model with { ContextLength = 5 }, Ui = good.Ui with { BarResultsPerGroup = 0 } }));

        Assert.Equal(
            ["Model.ContextLength", "Ui.BarResultsPerGroup"], refused.Issues.Select(issue => issue.Setting).Order().ToArray());
        Assert.Equal(before, await File.ReadAllBytesAsync(_folder.FilePath));
        AssertSame(good, await service.LoadAsync());
        Assert.Equal(["settings.json"], _folder.Files());
    }

    [Fact]
    public async Task SavingIsSeenByTheNextReadWithoutTouchingTheDisk()
    {
        var service = _folder.CreateService();
        var changed = new AppSettings { Ui = new UiSettings { BarResultsPerGroup = 8 } };

        await service.SaveAsync(changed);
        File.Delete(_folder.FilePath);

        Assert.Equal(8, (await service.LoadAsync()).Ui.BarResultsPerGroup);
    }

    [Fact]
    public async Task SavingLeavesTheFileAndItsBackupAndNothingElse()
    {
        var service = _folder.CreateService();

        await service.SaveAsync(new AppSettings());
        Assert.Equal(["settings.json"], _folder.Files());
        await service.SaveAsync(SettingsFolder.Customized());

        Assert.Equal(["settings.json", "settings.json.bak"], _folder.Files());
    }

    [Fact]
    public async Task ConcurrentUpdatesNeverUndoEachOther()
    {
        var service = _folder.CreateService();
        var folders = Enumerable.Range(0, 40).Select(number => $@"C:\Excluded\{number}").ToArray();

        await Task.WhenAll(folders.Select(folder => service.UpdateAsync(settings => settings with
        {
            Privacy = settings.Privacy with { ExcludedFolders = [.. settings.Privacy.ExcludedFolders, folder] },
        })));

        var reloaded = await _folder.CreateService().LoadAsync();
        Assert.Equal(folders.Order(), reloaded.Privacy.ExcludedFolders.Order());
    }

    [Fact]
    public async Task AFailedWriteLeavesThePreviousFileWholeAndTheSettingsInMemoryUnchanged()
    {
        var service = _folder.CreateService();
        var good = SettingsFolder.Customized();
        await service.SaveAsync(good);
        var before = await File.ReadAllBytesAsync(_folder.FilePath);

        // Another program holds the file, so it cannot be replaced.
        await using (new FileStream(_folder.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(good with { Ui = good.Ui with { BarResultsPerGroup = 9 } }));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(_folder.FilePath));
        Assert.Equal(good.Ui.BarResultsPerGroup, (await service.LoadAsync()).Ui.BarResultsPerGroup);
        Assert.DoesNotContain(_folder.Files(), name => name.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileThatCannotBeReadRightNowIsNeitherSetAsideNorRemembered()
    {
        var good = SettingsFolder.Customized();
        await _folder.CreateService().SaveAsync(good);
        var service = _folder.CreateService();

        AppSettings duringLock;
        await using (new FileStream(_folder.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            duringLock = await service.LoadAsync();
        }

        AssertSame(new AppSettings(), duringLock);
        Assert.True(File.Exists(_folder.FilePath));
        Assert.DoesNotContain(_folder.Files(), name => name.Contains("corrupt", StringComparison.Ordinal));

        // Once the file is free it is read after all.
        AssertSame(good, await service.LoadAsync());
        Assert.Equal(SettingsLoadOutcome.Loaded, service.Outcome);
    }

    [Fact]
    public async Task DeletingTheFileStartsOverAndForgetsTheOldBackup()
    {
        var writer = _folder.CreateService();
        await writer.SaveAsync(SettingsFolder.Customized());
        await writer.SaveAsync(new AppSettings());
        File.Delete(_folder.FilePath);

        var settings = await _folder.CreateService().LoadAsync();

        AssertSame(new AppSettings(), settings);
        Assert.False(File.Exists(_folder.BackupPath));
    }

    [Fact]
    public async Task OnlyOldTemporaryFilesAreCleanedUp()
    {
        Directory.CreateDirectory(_folder.Root);
        var stale = Path.Combine(_folder.Root, "settings.json.aaaa.tmp");
        var fresh = Path.Combine(_folder.Root, "settings.json.bbbb.tmp");
        await File.WriteAllTextAsync(stale, "x");
        await File.WriteAllTextAsync(fresh, "x");
        File.SetLastWriteTimeUtc(stale, _folder.Time.GetUtcNow().UtcDateTime.AddDays(-2));
        File.SetLastWriteTimeUtc(fresh, _folder.Time.GetUtcNow().UtcDateTime.AddMinutes(-5));

        await _folder.CreateService().LoadAsync();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task OnlyTheThreeNewestDamagedCopiesAreKept()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            _folder.WriteFile($"damaged {attempt}");
            _folder.Time.Advance(TimeSpan.FromSeconds(1));
            await _folder.CreateService().LoadAsync();
        }

        var copies = _folder.Files().Where(name => name.StartsWith("settings.corrupt-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, copies.Length);
        Assert.Equal(
            ["damaged 2", "damaged 3", "damaged 4"],
            copies.Select(copy => File.ReadAllText(Path.Combine(_folder.Root, copy))).ToArray());
    }

    [Fact]
    public async Task LogsSayWhatHappenedWithoutAnyValueOrPath()
    {
        const string Secret = "Private-Taxes-2026";
        var logs = new CapturingLoggerProvider();
        using var loggers = logs.CreateFactory();
        var service = _folder.CreateService(loggers);
        var settings = SettingsFolder.Customized() with
        {
            Privacy = new PrivacySettings { ExcludedFolders = [$@"C:\{Secret}"] },
            Model = new ModelSettings { ModelFilePath = $@"C:\{Secret}\model.gguf" },
        };
        await service.SaveAsync(settings);
        await service.SaveAsync(settings with { Ui = new UiSettings { BarResultsPerGroup = 6 } });
        await Assert.ThrowsAsync<SettingsValidationException>(
            () => service.SaveAsync(settings with { Model = new ModelSettings { ContextLength = 3 } }));
        await File.WriteAllTextAsync(_folder.FilePath, $"{{ {Secret}");
        await _folder.CreateService(loggers).LoadAsync();

        var text = logs.AllText;
        Assert.Contains("Settings saved", text, StringComparison.Ordinal);
        Assert.Contains("Settings were refused because 1 values are not allowed", text, StringComparison.Ordinal);
        Assert.Contains("Settings read (RestoredFromBackup)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_folder.Root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ContextLength", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeSettingsPathIsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => new JsonSettingsService("settings.json", TimeProvider.System, NullLogger<JsonSettingsService>.Instance));
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    // Two settings are the same when they are written as the same JSON, which is every value of every section.
    private static void AssertSame(object expected, object actual)
    {
        static string Write(object value) => JsonSerializer.Serialize(value);
        Assert.Equal(Write(expected), Write(actual));
    }
}
