using System.Diagnostics;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Data.Settings;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.Settings;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Credentials;
using Assistant.Windows.Frame;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The settings window ---------------------------------------------------------------------------------------

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of every page, for looking at.
    [Fact]
    public void RenderEverySettingsPageWhenAskedTo() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(
            existingFiles: [ProfileFile("chat-4b", "model.gguf"), ProfileFile("chat-4b", "mmproj.gguf")],
            microphones: new FakeMicrophones(new("{a}", "Microphone (Studio USB)", true), new("{b}", "Microphone (Wireless Headset)", false)));
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            Pump();
            foreach (var section in kit.Model.Sections)
            {
                kit.Model.SelectedSection = section;
                window.UpdateLayout();
                Pump();
                RenderFixture(Named<Grid>(window, "Root"), $"settings-{section.Section.ToString().ToLowerInvariant()}.png", 2);
            }
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    // Opt-in render of the Model page whole, in the states that warn.
    [Fact]
    public void RenderTheModelPageInItsWarningStatesWhenAskedTo() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var files = new[] { ProfileFile("chat-4b", "model.gguf"), ProfileFile("chat-4b", "mmproj.gguf") };
        var busy = new AppSettings
        {
            ContextLimits = new ContextLimitSettings { NormalContextTokens = 8192, HeavyContextTokens = 300, ReservedOutputTokens = 1024 },
            Model = new ModelSettings { ContextLength = 262_144, ProfileId = "chat-9b" },
        };
        var own = new AppSettings
        {
            Model = new ModelSettings { ModelFilePath = @"C:\Models\my-model-q4.gguf", HardwarePresetId = "compact" },
        };
        foreach (var (name, saved) in new[] { ("tall", new AppSettings()), ("warnings", busy), ("ownfile", own) })
        {
            var kit = CreateSettingsKit(saved, existingFiles: files, memory: 16 * SettingsGiB);
            var (window, _, _) = CreateSettingsWindow(kit);
            try
            {
                window.Height = 1500;
                window.Show();
                kit.Model.SelectedSection = kit.Model.Sections.Single(item => item.Section == SettingsSection.Model);
                window.UpdateLayout();
                Pump();
                RenderFixture(Named<Grid>(window, "Root"), $"settings-model-{name}.png", 1);
            }
            finally
            {
                window.CloseForGood();
            }
        }
    })));

    [Fact]
    public void TheSettingsWindowIsBuiltFromTheHistoryWindowsFrameAndButtons() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit();
        var (window, frames, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            Pump();
            var root = Named<Grid>(window, "Root");
            Assert.Equal(new Size(940, 700), new Size(root.ActualWidth, root.ActualHeight));
            Assert.Equal("Settings", window.Title);

            // Windows draws the frame, as it does the History window's: dark Mica with the same neutral hairline.
            var (handle, style) = Assert.Single(frames.Applied);
            Assert.Equal(new System.Windows.Interop.WindowInteropHelper(window).Handle, handle);
            Assert.Equal(new WindowFrameStyle(SystemBackdropKind.Mica, 0x3C3C3C), style);

            // A 240-wide sidebar and the workspace, with the window's own buttons where the History window has them.
            Assert.Equal(new Rect(0, 0, 240, 700), BoundsIn(root, Named<Grid>(window, "Sidebar")));
            Assert.Equal(new Rect(240, 0, 700, 700), BoundsIn(root, Named<Grid>(window, "Workspace")));
            Assert.Equal(new Rect(17.75, 17.5, 13.5, 13.5), BoundsIn(root, Named<Button>(window, "CloseButton")));
            Assert.Equal(new Rect(39.5, 17.5, 13.5, 13.5), BoundsIn(root, Named<Button>(window, "MinimizeButton")));
            Assert.Equal(new Rect(61.25, 17.5, 13.5, 13.5), BoundsIn(root, Named<Button>(window, "MaximizeButton")));
            Assert.Equal("Close", AutomationName(Named<Button>(window, "CloseButton")));
            Assert.Equal("Minimize", AutomationName(Named<Button>(window, "MinimizeButton")));
            Assert.Equal("Maximize", AutomationName(Named<Button>(window, "MaximizeButton")));

            // The sidebar lists the sections, the open one selected.
            var sections = Named<ListBox>(window, "Sections");
            Assert.Equal("Settings sections", AutomationName(sections));
            Assert.Equal(
                ["General", "Model", "Voice", "ASR", "Integrations", "Privacy", "Permissions", "People", "Memory", "Hotkeys", "Activity", "About"],
                sections.Items.Cast<SettingsSectionItem>().Select(item => item.Title));
            Assert.Equal("General", ((SettingsSectionItem)sections.SelectedItem).Title);
            Assert.Equal("General", Named<TextBlock>(window, "PageTitle").Text);

            // Opaque colors while Windows has no backdrop to show, the History window's own, and the backdrop showing
            // through once it has.
            Assert.Equal(ThemeColor("Brush.Surface.SidebarOpaque"), SolidColor(Named<Grid>(window, "Sidebar").Background));
            Assert.Equal(ThemeColor("Brush.Surface.WorkspaceOpaque"), SolidColor(Named<Grid>(window, "Workspace").Background));
            frames.Frame!.SetTranslucent(true);
            Assert.True(Backdrop.GetIsBlurred(window));
            Assert.Equal(ThemeColor("Brush.Surface.Sidebar"), SolidColor(Named<Grid>(window, "Sidebar").Background));
            Assert.Equal(ThemeColor("Brush.Surface.Workspace"), SolidColor(Named<Grid>(window, "Workspace").Background));
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void EverySectionOpensItsPageWithoutABindingError() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
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
                Assert.Equal(section.Title, Named<TextBlock>(window, "PageTitle").Text);
                Assert.NotEmpty(Descendants<SettingsRow>(Named<ContentControl>(window, "PageContent")));
            }

            // The states that show more: a notice, a model that is loaded, a warning under a number, a shortcut's problem.
            kit.Lifecycle.LoadedModel = new ModelInfo("chat-4b", 8192);
            kit.Model.LoadAsync().GetAwaiter().GetResult();
            kit.Model.Model.HeavyLimit.Text = "50";
            kit.Model.Model.CustomWindow.Text = "900000";
            kit.Model.Hotkeys.SearchOrAsk.RecordCommand.Execute(null);
            kit.Model.Hotkeys.SearchOrAsk.TryAccept(HotkeyModifiers.Shift, "Q");
            Pump();
        }
        finally
        {
            window.CloseForGood();
        }

        Assert.Empty(errors.Messages);
    })));

    [Fact]
    public void EveryControlOfTheSettingsWindowWorksAndNoneWaitsForALaterVersion() => RunSta(() => WithTheme(() =>
    {
        // The controls that used to say "Coming later": each one does what it says now.
        (SettingsSection Section, string Control)[] formerlyComingLater =
        [
            (SettingsSection.General, "Results in each group"),
            (SettingsSection.General, "Search files by default"),

            (SettingsSection.Context, "Most files attached to one request"),
            (SettingsSection.Context, "Most files found to ground an answer"),
            (SettingsSection.Context, "Largest file to read in megabytes"),
            (SettingsSection.Privacy, "Keep history for"),
            (SettingsSection.Privacy, "Add a folder to keep out of search"),
        ];
        (SettingsSection Section, string Control)[] working =
        [
            (SettingsSection.General, "Start Assistant on boot"),


            (SettingsSection.Model, "Context limit for ordinary conversations"),
            (SettingsSection.Model, "Context limit for conversations with files"),

            (SettingsSection.Context, "Tokens kept for the answer"),
            (SettingsSection.Privacy, "Save my conversations"),
            (SettingsSection.Hotkeys, "Search or Ask shortcut"),
            (SettingsSection.Hotkeys, "Selected text shortcut"),
            (SettingsSection.Hotkeys, "Visual Intelligence shortcut"),
            (SettingsSection.Voice, "Text-to-speech model"),
            (SettingsSection.Asr, "Wake word Kiri"),
            (SettingsSection.Integrations, "File Explorer menu entry"),
            (SettingsSection.Integrations, "Edge and Chrome extension"),
        ];
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            foreach (var (section, name) in formerlyComingLater)
            {
                var control = SettingsControl(window, kit, section, name);
                Assert.True(control.IsEnabled, $"{name} should work.");
                var row = Ancestor<SettingsRow>(control);
                Assert.NotEqual(SettingStatus.ComingLater, row.Status);
                Assert.NotEqual("Coming later", row.StatusText);
                Assert.True(row.IsControlEnabled);
            }

            foreach (var (section, name) in working)
            {
                Assert.True(SettingsControl(window, kit, section, name).IsEnabled, $"{name} should work.");
            }

            // The three shortcuts and the hardware preset say when they take effect.
            Assert.Equal(SettingStatus.AtNextStart, Ancestor<SettingsRow>(SettingsControl(window, kit, SettingsSection.Hotkeys, "Search or Ask shortcut")).Status);
            Assert.Equal(SettingStatus.AtNextStart, Ancestor<SettingsRow>(SettingsControl(window, kit, SettingsSection.Hotkeys, "Visual Intelligence shortcut")).Status);
            Assert.Equal(SettingStatus.AtNextStart, Ancestor<SettingsRow>(SettingsControl(window, kit, SettingsSection.Hotkeys, "Selected text shortcut")).Status);

        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void TheVoicePageOffersTheThreeModelsAndKiriAndSavesWhatIsChosen() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            var models = Assert.IsType<ComboBox>(SettingsControl(window, kit, SettingsSection.Voice, "Text-to-speech model"));
            Assert.Equal(
                ["KittenTTS Mini 0.8 (80M)", "Kokoro-82M ONNX", "Piper"],
                models.Items.Cast<TextToSpeechModel>().Select(model => model.DisplayName));
            Assert.Equal("KittenTTS Mini 0.8 (80M)", ((TextToSpeechModel)models.SelectedItem).DisplayName);
            Assert.True(models.IsEnabled);

            // Choosing a voice is saved as it is made.
            models.SelectedItem = TextToSpeechModels.Piper;
            kit.Settle();
            Assert.Equal("piper", kit.Saved.Voice.TextToSpeechModelId);

            // Listening is the ASR page's: the Voice page has nothing of the wake word, the microphone or the recognizer any more.
            Assert.DoesNotContain(
                Descendants<Control>(Named<ContentControl>(window, "PageContent")), control => AutomationName(control) is "Wake word Kiri" or "Microphone");
            var wake = Assert.IsType<CheckBox>(SettingsControl(window, kit, SettingsSection.Asr, "Wake word Kiri"));
            Assert.True(wake.IsEnabled);
            Assert.False(wake.IsChecked);
            var row = Ancestor<SettingsRow>(wake);
            Assert.Equal(SettingStatus.Available, row.Status);
            Assert.Equal("Wake word \u201CHey Kiri\u201D", row.Header);
            Assert.Empty(row.Description);

            // The page is live: it does not say that anything is coming later.
            var texts = Descendants<TextBlock>(Named<ContentControl>(window, "PageContent")).Select(text => text.Text).ToArray();
            Assert.DoesNotContain(texts, text => text.Contains("coming later", StringComparison.OrdinalIgnoreCase));

            // Choosing a voice and turning the wake word on are saved as they are made.
            wake.IsChecked = true;
            kit.Settle();
            Assert.Equal("piper", kit.Saved.Voice.TextToSpeechModelId);
            Assert.True(kit.Saved.Voice.WakeWordEnabled);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void TheControlsChangeTheRealSettings() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(existingFiles: [ProfileFile("chat-4b", "model.gguf"), ProfileFile("chat-9b", "model.gguf")]);
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();

            // A switch.
            var history = Assert.IsType<CheckBox>(SettingsControl(window, kit, SettingsSection.Privacy, "Save my conversations"));
            Assert.True(history.IsChecked);
            ((IToggleProvider)new CheckBoxAutomationPeer(history)).Toggle();
            kit.Settle();
            Assert.False(kit.Saved.Privacy.HistoryEnabled);

            // A number typed in a field.
            var normal = Assert.IsType<TextBox>(SettingsControl(window, kit, SettingsSection.Model, "Context limit for ordinary conversations"));
            Assert.Equal("8,000", normal.Text);
            normal.Text = "6000";
            kit.Settle();
            Assert.Equal(6000, kit.Saved.ContextLimits.NormalContextTokens);

            // The limit is the window the model is loaded with for a chat: no window is written down beside it.
            Assert.Null(kit.Saved.Model.ContextLength);
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    [Fact]
    public void AWarningIsAmberAndStillSavedAndAnErrorIsRedAndNot() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var kit = CreateSettingsKit(memory: 16 * SettingsGiB);
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            var field = Assert.IsType<TextBox>(SettingsControl(window, kit, SettingsSection.Context, "Context limit for conversations with files"));
            var content = Named<ContentControl>(window, "PageContent");

            // A window that may need more memory than the PC has: warned about in amber, saved all the same.
            field.Text = "2000";
            kit.Settle();
            Pump();
            var warning = Assert.Single(Descendants<TextBlock>(content), text => text.Text.Contains("smaller than the normal limit", StringComparison.Ordinal));
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.Equal(ThemeColor("Brush.Settings.Warning"), SolidColor(warning.Foreground));
            Assert.False(SettingsField.GetIsInvalid(field));
            Assert.Equal(2000, kit.Saved.ContextLimits.HeavyContextTokens);

            // A number the engine cannot take: red, the field outlined, nothing saved.
            field.Text = "100";
            kit.Settle();
            Pump();
            var error = Assert.Single(Descendants<TextBlock>(content), text => text.Text.StartsWith("Enter 0 for no limit", StringComparison.Ordinal));
            Assert.Equal(ThemeColor("Brush.Settings.Error"), SolidColor(error.Foreground));
            Assert.True(SettingsField.GetIsInvalid(field));
            Assert.Equal(2000, kit.Saved.ContextLimits.HeavyContextTokens);
            Assert.Equal("100", field.Text);

            // Fixing it clears the advice.
            field.Text = "8192";
            kit.Settle();
            Pump();
            Assert.False(SettingsField.GetIsInvalid(field));
            Assert.DoesNotContain(Descendants<TextBlock>(content), text => text.Visibility == Visibility.Visible && text.Text.StartsWith("Enter a number", StringComparison.Ordinal));
            Assert.Equal(8192, kit.Saved.ContextLimits.HeavyContextTokens);
            Assert.Null(kit.Saved.Model.ContextLength);
        }
        finally
        {
            window.CloseForGood();
        }
    })));

    [Fact]
    public void ANewShortcutIsRecordedFromTheKeyboardAndEscGivesUp() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        var held = ModifierKeys.None;
        HotkeyCapture.HeldModifiers = () => held;
        try
        {
            window.Show();
            var keycap = Assert.IsType<Button>(SettingsControl(window, kit, SettingsSection.Hotkeys, "Search or Ask shortcut"));
            var editor = kit.Model.Hotkeys.SearchOrAsk;
            Assert.Equal("Alt + A", ((TextBlock)keycap.Content).Text);

            // Pressing the button waits for keys, and says so.
            Click(keycap);
            Pump();
            Assert.True(editor.IsRecording);
            Assert.Equal("Press the new shortcut…", ((TextBlock)keycap.Content).Text);

            // Keys that cannot work are explained under the row, and recording goes on.
            held = ModifierKeys.Shift;
            PressPreviewKey(keycap, Key.Q);
            Pump();
            Assert.True(editor.IsRecording);
            Assert.Contains(
                Descendants<TextBlock>(Named<ContentControl>(window, "PageContent")),
                text => text.Visibility == Visibility.Visible && text.Text.Contains("Hold Alt, Ctrl", StringComparison.Ordinal));

            // A modifier alone waits for its key.
            held = ModifierKeys.Control | ModifierKeys.Alt;
            PressPreviewKey(keycap, Key.LeftCtrl);
            Assert.True(editor.IsRecording);

            // The keys pressed become the shortcut, and are saved.
            PressPreviewKey(keycap, Key.Q);
            kit.Settle();
            Pump();
            Assert.False(editor.IsRecording);
            Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Q"), kit.Saved.Hotkeys.SearchOrAsk);
            Assert.Equal("Ctrl + Alt + Q", ((TextBlock)keycap.Content).Text);

            // Esc gives recording up and leaves the shortcut as it was, and the window stays.
            Click(keycap);
            Pump();
            Assert.True(editor.IsRecording);
            held = ModifierKeys.None;
            PressPreviewKey(window, Key.Escape);
            Assert.True(window.IsVisible);
            Assert.True(editor.IsRecording);
            PressPreviewKey(keycap, Key.Escape);
            Assert.False(editor.IsRecording);
            Assert.True(window.IsVisible);
            Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Q"), kit.Saved.Hotkeys.SearchOrAsk);
        }
        finally
        {
            HotkeyCapture.HeldModifiers = () => Keyboard.Modifiers;
            window.CloseForGood();
        }
    }));

    [Fact]
    public void EscPutsTheSettingsWindowAwayAndClosingOnlyHidesIt() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit();
        var (window, _, placement) = CreateSettingsWindow(kit);
        try
        {
            window.ShowAndActivate();
            Pump();
            Assert.True(window.IsVisible);
            var (_, width, height, margin) = Assert.Single(placement.CenterCalls);
            Assert.Equal((940.0, 700.0, 24.0), (width, height, margin));

            PressPreviewKey(window, Key.Escape);
            Assert.False(window.IsVisible);

            // It comes back as it was, not placed again, and shows what has been saved since.
            kit.Settings.UpdateAsync(settings => settings with { Privacy = settings.Privacy with { HistoryEnabled = false } })
                .GetAwaiter().GetResult();
            window.ShowAndActivate();
            Pump();
            Assert.True(window.IsVisible);
            Assert.Single(placement.CenterCalls);
            Assert.False(kit.Model.Privacy.HistoryEnabled);

            // Its close button only hides it.
            window.Close();
            Assert.False(window.IsVisible);
            window.ShowAndActivate();
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void NoPageOfTheSettingsWindowSaysAnythingIsComingLaterOrNotBuiltYet() => RunSta(() => WithTheme(() =>
    {
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            var rowsSeen = 0;
            foreach (var section in kit.Model.Sections)
            {
                kit.Model.SelectedSection = section;
                window.UpdateLayout();
                Pump();

                foreach (var row in Descendants<SettingsRow>(window))
                {
                    rowsSeen++;
                    Assert.True(
                        row.Status is not (SettingStatus.ComingLater or SettingStatus.NotInThisVersion),
                        $"A row on the {section.Section} page says it is not here yet ({row.Header}).");
                }

                var texts = string.Join("\n", Descendants<TextBlock>(window).Select(block => block.Text));
                foreach (var phrase in new[] { "Coming later", "Coming soon", "not built yet", "isn't built yet", "not available yet", "isn't available yet" })
                {
                    Assert.False(texts.Contains(phrase, StringComparison.OrdinalIgnoreCase), $"The {section.Section} page says \"{phrase}\".");
                }
            }

            Assert.True(rowsSeen > 20, "The pages were not looked at: only " + rowsSeen + " rows were found.");
        }
        finally
        {
            window.CloseForGood();
        }
    }));

    [Fact]
    public void ASettingsRowDisablesItsControlOnlyWhileTheFeatureIsStillToCome() => RunSta(() =>
    {
        var row = new SettingsRow();
        Assert.True(row.IsControlEnabled);
        Assert.Equal(string.Empty, row.StatusText);

        row.Status = SettingStatus.ComingLater;
        Assert.False(row.IsControlEnabled);
        Assert.Equal("Coming later", row.StatusText);

        row.Status = SettingStatus.AtNextStart;
        Assert.True(row.IsControlEnabled);
        Assert.Equal("Applies at next start", row.StatusText);

        row.Status = SettingStatus.AtNextModelLoad;
        Assert.True(row.IsControlEnabled);

        row.Description = "Something";
        Assert.True(row.HasDescription);
    });

    [Fact]
    public void TheSettingsLauncherCreatesTheWindowOnceAndShowsItOnTheUiThread() => RunSta(() =>
    {
        var created = 0;
        var window = new RecordingSettingsWindow();
        var launcher = new SettingsLauncher(() =>
        {
            created++;
            return window;
        });
        Assert.Equal(0, created);

        launcher.Show();
        launcher.Show();
        Pump();

        Assert.Equal(1, created);
        Assert.Equal(2, window.Shown);
        Assert.All(window.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
    });

    [Fact]
    public void AskingForDemoSettingsOpensTheSettingsWindowAndItIsListedWithTheDemos() => RunSta(() =>
    {
        var launcher = new RecordingSettingsLauncher();
        var demo = new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now), settingsWindow: launcher);
        Assert.Contains("demo settings", demo.Answer("demo")!.Text, StringComparison.Ordinal);

        var answer = demo.Answer("Demo settings!");

        Assert.NotNull(answer);
        Assert.Equal(1, launcher.Shown);
        Assert.Contains("settings window", answer!.Text, StringComparison.Ordinal);
        Assert.Contains("saved as you make it", answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Coming later", answer.Text, StringComparison.Ordinal);
        Assert.Contains(
            "not available",
            new DemoAnswerProvider(new FakeClipboard(), new FixedClock(Now)).Answer("demo settings")!.Text,
            StringComparison.Ordinal);
    });

    [Fact]
    public void TheAppKeepsItsSettingsInAFileAndItsSecretsInWindowsCredentialManager()
    {
        using var host = AppHost.Create();

        var settings = host.Services.GetRequiredService<ISettingsService>();

        Assert.IsType<JsonSettingsService>(settings);
        Assert.Same(settings, host.Services.GetRequiredService<ISettingsLoadReport>());
        Assert.IsType<WindowsCredentialSecretStore>(host.Services.GetRequiredService<ISecretStore>());
        Assert.NotNull(host.Services.GetRequiredService<ISettingsLauncher>());
        Assert.Same(host.Services.GetRequiredService<SettingsViewModel>(), host.Services.GetRequiredService<SettingsViewModel>());
    }

    // The control with an automation name on the page of a section, after opening it.
    private static Control SettingsControl(SettingsWindow window, SettingsKit kit, SettingsSection section, string name)
    {
        // Context is drawn on the Model page, and Cleanup on the Privacy page: they are not sections of their own.
        section = section switch { SettingsSection.Context => SettingsSection.Model, SettingsSection.Cleanup => SettingsSection.Privacy, _ => section };
        kit.Model.SelectedSection = kit.Model.Sections.Single(item => item.Section == section);
        window.UpdateLayout();
        Pump();
        var matches = Descendants<Control>(Named<ContentControl>(window, "PageContent"))
            .Where(control => AutomationName(control) == name).ToArray();
        return Assert.Single(matches);
    }

    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T match)
            {
                return match;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} above the element.");
    }

    // A key pressed on an element, as the keyboard routes it before anything handles it.
    private static void PressPreviewKey(UIElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });

    private sealed class RecordingSettingsWindow : ISettingsWindow
    {
        public int Shown { get; private set; }

        public List<int> Threads { get; } = [];

        public void ShowAndActivate()
        {
            Shown++;
            Threads.Add(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class RecordingSettingsLauncher : ISettingsLauncher
    {
        public int Shown { get; private set; }

        public void Show() => Shown++;
    }

    // Collects the data binding errors WPF traces while it runs, so a page whose template binds to something that is not
    // there fails a test instead of quietly showing nothing.
    private sealed class BindingErrors : TraceListener
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public static BindingErrors Listen()
        {
            var listener = new BindingErrors();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            return listener;
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _messages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Off;
            }

            base.Dispose(disposing);
        }
    }
}
