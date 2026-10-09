using Assistant.Core.Settings;
using Assistant.Data.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Data.Tests;

/// <summary>A folder of its own for a settings file, deleted with it, and the settings service over it.</summary>
internal sealed class SettingsFolder : IDisposable
{
    private readonly List<IDisposable> _services = [];

    public SettingsFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "assistant-settings-tests", Guid.NewGuid().ToString("N"));
        FilePath = Path.Combine(Root, "settings.json");
    }

    public string Root { get; }

    public string FilePath { get; }

    public string BackupPath => FilePath + ".bak";

    /// <summary>The clock the names of set-aside copies come from. It stands still until a test moves it.</summary>
    public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 30, 10, 15, 0, TimeSpan.Zero));

    /// <summary>A new service over the file, as the app makes one at each start.</summary>
    public JsonSettingsService CreateService(ILoggerFactory? loggers = null, IReadOnlyList<SettingsMigration>? migrations = null)
    {
        var service = new JsonSettingsService(
            FilePath, Time, (loggers ?? NullLoggerFactory.Instance).CreateLogger<JsonSettingsService>(), migrations);
        _services.Add(service);
        return service;
    }

    /// <summary>The names of the files in the folder, in order.</summary>
    public string[] Files() =>
        Directory.Exists(Root) ? [.. Directory.EnumerateFiles(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)!] : [];

    public void WriteFile(string text)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(FilePath, text);
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Settings in which every value differs from its default, so a round trip proves each one is kept.</summary>
    public static AppSettings Customized() => new()
    {
        Ui = new UiSettings { FirstRunCompleted = true, BarResultsPerGroup = 5, FilesScopeOnByDefault = true },
        Model = new ModelSettings
        {
            ChatModelId = "chat-model",
            VisionModelId = "vision-model",
            ModelsDirectory = @"C:\Models",
            ProfileId = "chat-9b",
            HardwarePresetId = "performance",
            ModelFilePath = @"C:\Models\a.gguf",
            GpuDeviceId = "Vulkan1",
            ProjectorFilePath = @"C:\Models\a-mmproj.gguf",
            VisionOnDemand = true,
            ChatTemplateFilePath = @"C:\Models\template.jinja",
            ContextLength = 12288,
            IdleUnloadTimeout = TimeSpan.FromMinutes(45),
            UseGpuAcceleration = false,
        },
        Privacy = new PrivacySettings
        {
            HistoryEnabled = false,
            LocalOnly = false,
            HistoryRetention = HistoryRetention.NinetyDays,
            ExcludedFolders = [@"C:\Private", @"D:\Taxes"],
        },
        // Every switch is the opposite of its default. What is saved for a capability the Assistant cannot do yet is kept as it
        // is, and has no effect (SettingsPermissionPolicy).
        Permissions = new PermissionSettings
        {
            Files = false,
            ScreenCapture = false,
            SelectedText = false,
            SelectedTextByCopy = true,
            ClipboardHistory = true,
            Calendar = true,
            Messaging = true,
            ExternalSearch = true,
            DestructiveActions = true,
            Ask = new PermissionAskSettings
            {
                ScreenCapture = true, SelectedText = true, SelectedTextByCopy = true, Calendar = true, Messaging = true, ExternalSearch = true,
            },
            AlwaysAllowed = ["start_timer:0123456789abcdef", "set_volume:fedcba9876543210"],
        },
        Hotkeys = new HotkeySettings
        {
            SearchOrAsk = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Space"),
            SelectedTextActions = null,
            VisualIntelligence = new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Windows, "F5"),
            SelectedTextByCopy = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, "F7"),
        },
        ContextLimits = new ContextLimitSettings
        {
            MaxAttachedFiles = 20,
            MaxRetrievedFiles = 8,
            MaxFileSizeBytes = 10L * 1024 * 1024,
            NormalContextTokens = 12000,
            HeavyContextTokens = 30000,
            ReservedOutputTokens = 2048,
        },
        LaunchAtLogin = new LaunchAtLoginSettings { Enabled = true },
        Integrations = new IntegrationSettings
        {
            ExplorerContextMenuEnabled = true, BrowserBridgeEnabled = true, CheckForIntegrationUpdates = true, MicrosoftClientId = "11111111-2222-3333-4444-555555555555",
        },
        WebSearch = new() { Enabled = true, Provider = WebSearchProvider.Tavily },
        Voice = new VoiceSettings { SpeechRecognitionModelId = "asr-whisper-small", SpeechRecognitionDevice = "vulkan", TextToSpeechModelId = "piper", WakeWordEnabled = true, VoiceInputEnabled = false, MicrophoneDeviceId = "{0.0.1.00000000}.{abc}",
            SpeechApiEndpoint = "http://localhost:8880/v1/audio/speech", SpeechApiModel = "kokoro", SpeechApiVoice = "af_heart", TextToSpeechDevice = "cuda:GPU-12345678-1234-1234-1234-123456789abc" },
        GameMode = new GameModeSettings { Games = true, CreativeApps = true, ReleaseSharedRecognizer = false },
        Cleanup = new CleanupSettings { DeleteBarChats = true, BarChatHours = 4, DeleteWindowChats = true, WindowChatDays = 30, DeleteLogs = false, LogHours = 12 },
        Updates = new UpdateSettings { LastCheckedAt = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero), DismissedVersion = "v0.2.0" },
    };
}
