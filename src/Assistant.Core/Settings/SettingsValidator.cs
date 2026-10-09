namespace Assistant.Core.Settings;

/// <summary>
/// Checks settings against <see cref="SettingsLimits"/> and repairs them (PROJECT_SPEC §5.10). It is the one place that
/// knows what a valid value is, so the file the settings are read from, the service that saves them and the Settings
/// window all agree.
/// </summary>
public static class SettingsValidator
{
    /// <summary>
    /// Everything wrong with <paramref name="settings"/>: values outside their range or not allowed, and settings that
    /// conflict. An empty list means they can be saved.
    /// </summary>
    public static IReadOnlyList<SettingsIssue> Validate(AppSettings settings)
    {
        var repaired = Sanitize(settings, out var issues);
        return [.. issues, .. FindConflicts(repaired.Hotkeys)];
    }

    /// <summary>
    /// Returns <paramref name="settings"/> with every value that is out of range or not allowed replaced by its default
    /// (a path that is not usable is cleared, a list loses its bad entries), and lists what was replaced. Conflicts are
    /// not repaired: neither of two conflicting settings is more likely to be the wrong one.
    /// </summary>
    public static AppSettings Sanitize(AppSettings settings, out IReadOnlyList<SettingsIssue> repaired)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<SettingsIssue>();
        var defaults = new AppSettings();

        var ui = settings.Ui ?? Missing(issues, nameof(AppSettings.Ui), defaults.Ui);
        var model = settings.Model ?? Missing(issues, nameof(AppSettings.Model), defaults.Model);
        var privacy = settings.Privacy ?? Missing(issues, nameof(AppSettings.Privacy), defaults.Privacy);
        var permissions = settings.Permissions ?? Missing(issues, nameof(AppSettings.Permissions), defaults.Permissions);
        var hotkeys = settings.Hotkeys ?? Missing(issues, nameof(AppSettings.Hotkeys), defaults.Hotkeys);
        var limits = settings.ContextLimits ?? Missing(issues, nameof(AppSettings.ContextLimits), defaults.ContextLimits);

        var launch = settings.LaunchAtLogin ?? Missing(issues, nameof(AppSettings.LaunchAtLogin), defaults.LaunchAtLogin);
        var integrations = settings.Integrations ?? Missing(issues, nameof(AppSettings.Integrations), defaults.Integrations);
        var voice = settings.Voice ?? Missing(issues, nameof(AppSettings.Voice), defaults.Voice);
        var search = settings.WebSearch ?? Missing(issues, nameof(AppSettings.WebSearch), defaults.WebSearch);
        var gameMode = settings.GameMode ?? Missing(issues, nameof(AppSettings.GameMode), defaults.GameMode);
        var cleanup = settings.Cleanup ?? Missing(issues, nameof(AppSettings.Cleanup), defaults.Cleanup);
        var updates = settings.Updates ?? Missing(issues, nameof(AppSettings.Updates), defaults.Updates);

        var result = settings with
        {
            GameMode = gameMode,

            // A release tag is a few letters, digits and dots ("v0.2.0"); anything else was not written by the update check.
            Updates = updates.DismissedVersion is null
                || (updates.DismissedVersion.Length is > 0 and <= 40 && updates.DismissedVersion.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_'))
                ? updates
                : updates with { DismissedVersion = Repair<string?>(issues, "Updates.DismissedVersion", SettingsIssueKind.Invalid, null) },
            Cleanup = cleanup with
            {
                BarChatHours = InRange(
                    issues, "Cleanup.BarChatHours", cleanup.BarChatHours, SettingsLimits.MinCleanupHours, SettingsLimits.MaxCleanupHours,
                    defaults.Cleanup.BarChatHours),
                WindowChatDays = InRange(
                    issues, "Cleanup.WindowChatDays", cleanup.WindowChatDays, SettingsLimits.MinCleanupDays, SettingsLimits.MaxCleanupDays,
                    defaults.Cleanup.WindowChatDays),
                LogHours = InRange(
                    issues, "Cleanup.LogHours", cleanup.LogHours, SettingsLimits.MinCleanupHours, SettingsLimits.MaxCleanupHours,
                    defaults.Cleanup.LogHours),
            },
            WebSearch = Enum.IsDefined(search.Provider) ? search : Repair(issues, "WebSearch.Provider", SettingsIssueKind.Invalid, defaults.WebSearch),
            SchemaVersion = settings.SchemaVersion == AppSettings.CurrentSchemaVersion
                ? settings.SchemaVersion
                : Repair(issues, nameof(AppSettings.SchemaVersion), SettingsIssueKind.Invalid, AppSettings.CurrentSchemaVersion),
            Ui = ui with
            {
                BarResultsPerGroup = InRange(
                    issues, "Ui.BarResultsPerGroup", ui.BarResultsPerGroup, SettingsLimits.MinBarResultsPerGroup,
                    SettingsLimits.MaxBarResultsPerGroup, defaults.Ui.BarResultsPerGroup),
            },
            Model = model with
            {
                ChatModelId = Identifier(issues, "Model.ChatModelId", model.ChatModelId),
                VisionModelId = Identifier(issues, "Model.VisionModelId", model.VisionModelId),
                ModelsDirectory = PathValue(issues, "Model.ModelsDirectory", model.ModelsDirectory),
                ProfileId = ProfileIdentifier(issues, "Model.ProfileId", model.ProfileId),
                HardwarePresetId = ProfileIdentifier(issues, "Model.HardwarePresetId", model.HardwarePresetId),
                ModelFilePath = PathValue(issues, "Model.ModelFilePath", model.ModelFilePath),
                GpuDeviceId = model.GpuDeviceId is null || System.Text.RegularExpressions.Regex.IsMatch(model.GpuDeviceId, "^[A-Za-z][A-Za-z0-9_]{0,63}$")
                    ? model.GpuDeviceId : Repair<string?>(issues, "Model.GpuDeviceId", SettingsIssueKind.Invalid, null),
                ProjectorFilePath = PathValue(issues, "Model.ProjectorFilePath", model.ProjectorFilePath),
                ChatTemplateFilePath = PathValue(issues, "Model.ChatTemplateFilePath", model.ChatTemplateFilePath),
                ContextLength = model.ContextLength is { } length
                    ? InRange(issues, "Model.ContextLength", length, SettingsLimits.MinContextTokens, SettingsLimits.MaxContextTokens, null)
                    : null,
                IdleUnloadTimeout = model.IdleUnloadTimeout >= SettingsLimits.MinIdleUnloadTimeout
                    && model.IdleUnloadTimeout <= SettingsLimits.MaxIdleUnloadTimeout
                        ? model.IdleUnloadTimeout
                        : Repair(issues, "Model.IdleUnloadTimeout", SettingsIssueKind.OutOfRange, defaults.Model.IdleUnloadTimeout),
            },
            Privacy = privacy with
            {
                HistoryRetention = Enum.IsDefined(privacy.HistoryRetention)
                    ? privacy.HistoryRetention
                    : Repair(issues, "Privacy.HistoryRetention", SettingsIssueKind.Invalid, defaults.Privacy.HistoryRetention),
                ExcludedFolders = Folders(issues, privacy.ExcludedFolders),
            },
            Hotkeys = hotkeys with
            {
                SearchOrAsk = Shortcut(issues, "Hotkeys.SearchOrAsk", hotkeys.SearchOrAsk, defaults.Hotkeys.SearchOrAsk),
                SelectedTextActions = Shortcut(
                    issues, "Hotkeys.SelectedTextActions", hotkeys.SelectedTextActions, defaults.Hotkeys.SelectedTextActions),
                VisualIntelligence = Shortcut(
                    issues, "Hotkeys.VisualIntelligence", hotkeys.VisualIntelligence, defaults.Hotkeys.VisualIntelligence),
                SelectedTextByCopy = Shortcut(
                    issues, "Hotkeys.SelectedTextByCopy", hotkeys.SelectedTextByCopy, defaults.Hotkeys.SelectedTextByCopy),
            },
            ContextLimits = limits with
            {
                MaxAttachedFiles = InRange(
                    issues, "ContextLimits.MaxAttachedFiles", limits.MaxAttachedFiles, SettingsLimits.MinAttachedFiles,
                    SettingsLimits.MaxAttachedFiles, defaults.ContextLimits.MaxAttachedFiles),
                MaxRetrievedFiles = InRange(
                    issues, "ContextLimits.MaxRetrievedFiles", limits.MaxRetrievedFiles, SettingsLimits.MinRetrievedFiles,
                    SettingsLimits.MaxRetrievedFiles, defaults.ContextLimits.MaxRetrievedFiles),
                MaxFileSizeBytes = InRange(
                    issues, "ContextLimits.MaxFileSizeBytes", limits.MaxFileSizeBytes, SettingsLimits.MinFileSizeBytes,
                    SettingsLimits.MaxFileSizeBytes, defaults.ContextLimits.MaxFileSizeBytes),
                NormalContextTokens = ContextLimit(
                    issues, "ContextLimits.NormalContextTokens", limits.NormalContextTokens, defaults.ContextLimits.NormalContextTokens),
                HeavyContextTokens = ContextLimit(
                    issues, "ContextLimits.HeavyContextTokens", limits.HeavyContextTokens, defaults.ContextLimits.HeavyContextTokens),
                ReservedOutputTokens = limits.ReservedOutputTokens == 0
                    ? 0
                    : InRange(
                        issues, "ContextLimits.ReservedOutputTokens", limits.ReservedOutputTokens,
                        SettingsLimits.MinReservedOutputTokens, SettingsLimits.MaxReservedOutputTokens,
                        defaults.ContextLimits.ReservedOutputTokens),
            },
            Permissions = permissions with
            {
                Ask = permissions.Ask ?? Missing(issues, "Permissions.Ask", defaults.Permissions.Ask),
                AlwaysAllowed = AlwaysAllowed(issues, permissions.AlwaysAllowed),
            },
            LaunchAtLogin = launch,
            Integrations = integrations with
            {
                MicrosoftClientId = integrations.MicrosoftClientId is null || Guid.TryParse(integrations.MicrosoftClientId, out _)
                    ? integrations.MicrosoftClientId?.Trim().ToLowerInvariant()
                    : Repair<string?>(issues, "Integrations.MicrosoftClientId", SettingsIssueKind.Invalid, null),
            },
            Voice = voice with
            {
                SpeechRecognitionModelId = voice.SpeechRecognitionModelId is "handy" or "windows" || Assistant.Core.Models.DownloadCatalog.SpeechRecognizers.Any(model => model.Id == voice.SpeechRecognitionModelId)
                    ? voice.SpeechRecognitionModelId : Repair(issues, "Voice.SpeechRecognitionModelId", SettingsIssueKind.Invalid, defaults.Voice.SpeechRecognitionModelId),
                SpeechRecognitionDevice = voice.SpeechRecognitionDevice is "cpu" or "vulkan"
                    ? voice.SpeechRecognitionDevice : Repair(issues, "Voice.SpeechRecognitionDevice", SettingsIssueKind.Invalid, "cpu"),
                TextToSpeechDevice = voice.TextToSpeechDevice == "cpu" || System.Text.RegularExpressions.Regex.IsMatch(voice.TextToSpeechDevice ?? "", "^cuda:GPU-[a-fA-F0-9-]{36}$")
                    ? voice.TextToSpeechDevice! : Repair(issues, "Voice.TextToSpeechDevice", SettingsIssueKind.Invalid, defaults.Voice.TextToSpeechDevice),
                SpeechApiEndpoint = SpeechApiAddress.IsValid(voice.SpeechApiEndpoint) ? voice.SpeechApiEndpoint
                    : Repair(issues, "Voice.SpeechApiEndpoint", SettingsIssueKind.Invalid, defaults.Voice.SpeechApiEndpoint),
                SpeechApiModel = ApiValue(issues, "Voice.SpeechApiModel", voice.SpeechApiModel, defaults.Voice.SpeechApiModel),
                SpeechApiVoice = ApiValue(issues, "Voice.SpeechApiVoice", voice.SpeechApiVoice, defaults.Voice.SpeechApiVoice),
                TextToSpeechModelId = TextToSpeechModels.Find(voice.TextToSpeechModelId) is not null
                    ? voice.TextToSpeechModelId
                    : Repair(issues, "Voice.TextToSpeechModelId", SettingsIssueKind.Invalid, defaults.Voice.TextToSpeechModelId),
                MicrophoneDeviceId = voice.MicrophoneDeviceId is null || voice.MicrophoneDeviceId.Length is > 0 and <= 400 && !voice.MicrophoneDeviceId.Any(char.IsControl)
                    ? voice.MicrophoneDeviceId
                    : Repair<string?>(issues, "Voice.MicrophoneDeviceId", SettingsIssueKind.Invalid, null),
            },
        };

        repaired = issues;
        return result;
    }

    // Two shortcuts that are the same would fight over the key: the later one is named.
    private static string ApiValue(List<SettingsIssue> issues, string name, string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl)
            ? value : Repair(issues, name, SettingsIssueKind.Invalid, fallback);

    private static IEnumerable<SettingsIssue> FindConflicts(HotkeySettings hotkeys)
    {
        (string Name, Hotkey? Key)[] shortcuts =
        [
            ("Hotkeys.SearchOrAsk", hotkeys.SearchOrAsk),
            ("Hotkeys.SelectedTextActions", hotkeys.SelectedTextActions),
            ("Hotkeys.VisualIntelligence", hotkeys.VisualIntelligence),
            ("Hotkeys.SelectedTextByCopy", hotkeys.SelectedTextByCopy),
        ];
        for (var later = 1; later < shortcuts.Length; later++)
        {
            if (shortcuts[later].Key is { } key
                && shortcuts.Take(later).Any(earlier => earlier.Key is { } other && Same(key, other)))
            {
                yield return new SettingsIssue(shortcuts[later].Name, SettingsIssueKind.Conflict);
            }
        }
    }

    /// <summary>Whether two shortcuts are the same keys.</summary>
    public static bool Same(Hotkey first, Hotkey second) =>
        first.Modifiers == second.Modifiers && string.Equals(first.Key, second.Key, StringComparison.OrdinalIgnoreCase);

    private static T Missing<T>(List<SettingsIssue> issues, string name, T fallback)
    {
        issues.Add(new SettingsIssue(name, SettingsIssueKind.Invalid));
        return fallback;
    }

    private static T Repair<T>(List<SettingsIssue> issues, string name, SettingsIssueKind kind, T fallback)
    {
        issues.Add(new SettingsIssue(name, kind));
        return fallback;
    }

    private static int InRange(List<SettingsIssue> issues, string name, int value, int min, int max, int fallback) =>
        value >= min && value <= max ? value : Repair(issues, name, SettingsIssueKind.OutOfRange, fallback);

    private static int? InRange(List<SettingsIssue> issues, string name, int value, int min, int max, int? fallback) =>
        value >= min && value <= max ? value : Repair(issues, name, SettingsIssueKind.OutOfRange, fallback);

    private static long InRange(List<SettingsIssue> issues, string name, long value, long min, long max, long fallback) =>
        value >= min && value <= max ? value : Repair(issues, name, SettingsIssueKind.OutOfRange, fallback);

    // Zero leaves a limit to the model's own window; anything else is a number of tokens the engine can be given.
    private static int ContextLimit(List<SettingsIssue> issues, string name, int value, int fallback) =>
        value == 0 ? 0 : InRange(issues, name, value, SettingsLimits.MinContextTokens, SettingsLimits.MaxContextTokens, fallback);

    private static string? PathValue(List<SettingsIssue> issues, string name, string? value) =>
        value is null || SettingsLimits.IsValidPath(value) ? value : Repair<string?>(issues, name, SettingsIssueKind.Invalid, null);

    private static string? Identifier(List<SettingsIssue> issues, string name, string? value) =>
        value is null || (!string.IsNullOrWhiteSpace(value) && value.Length <= 200)
            ? value
            : Repair<string?>(issues, name, SettingsIssueKind.Invalid, null);

    private static string? ProfileIdentifier(List<SettingsIssue> issues, string name, string? value) =>
        value is null || SettingsLimits.IsValidIdentifier(value) ? value : Repair<string?>(issues, name, SettingsIssueKind.Invalid, null);

    private static Hotkey? Shortcut(List<SettingsIssue> issues, string name, Hotkey? value, Hotkey? fallback) =>
        value is null || SettingsLimits.IsValidHotkey(value) ? value : Repair(issues, name, SettingsIssueKind.Invalid, fallback);

    // An entry that is not a tool's name and a fingerprint is dropped, one listed twice is listed once, and no more are kept than can be.
    private static IReadOnlyList<string> AlwaysAllowed(List<SettingsIssue> issues, IReadOnlyList<string>? entries)
    {
        if (entries is null)
        {
            return Repair<IReadOnlyList<string>>(issues, "Permissions.AlwaysAllowed", SettingsIssueKind.Invalid, []);
        }

        var kept = entries
            .Where(Assistant.Core.Confirmation.StandingApprovals.IsWellFormed)
            .Distinct(StringComparer.Ordinal)
            .Take(Assistant.Core.Confirmation.StandingApprovals.MaxKept)
            .ToList();
        if (kept.Count == entries.Count)
        {
            return entries;
        }

        issues.Add(new SettingsIssue("Permissions.AlwaysAllowed", SettingsIssueKind.Invalid));
        return kept;
    }

    // Folders that are not complete paths are dropped, and a folder listed twice is listed once.
    private static IReadOnlyList<string> Folders(List<SettingsIssue> issues, IReadOnlyList<string>? folders)
    {
        if (folders is null)
        {
            return Repair<IReadOnlyList<string>>(issues, "Privacy.ExcludedFolders", SettingsIssueKind.Invalid, []);
        }

        var kept = new List<string>(folders.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = false;
        foreach (var folder in folders)
        {
            if (!SettingsLimits.IsValidPath(folder) || kept.Count >= SettingsLimits.MaxExcludedFolders
                || !seen.Add(System.IO.Path.TrimEndingDirectorySeparator(folder)))
            {
                dropped = true;
                continue;
            }

            kept.Add(folder);
        }

        if (!dropped)
        {
            return folders;
        }

        issues.Add(new SettingsIssue("Privacy.ExcludedFolders", SettingsIssueKind.Invalid));
        return kept;
    }
}
