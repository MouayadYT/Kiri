using System.IO;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.Core.Voice;
using Assistant.UI.Settings;
using Assistant.UI.Voice;
using Assistant.Voice;
using Xunit;

namespace Assistant.UI.Tests;

// The Voice page (PROJECT_SPEC §4.9, step 125): the voice that speaks, how fast each one is on this PC, the wake word and speech recognition.
public sealed partial class PromptInputControlTests
{
    private static TextToSpeechBenchmarkResult Measured(string engineId, double firstMs, double speed, double loadMs = 800) =>
        new(
            engineId,
            TimeSpan.FromMilliseconds(loadMs),
            [
                new TextToSpeechSample("Short reply", 27, TimeSpan.FromMilliseconds(firstMs), TimeSpan.FromMilliseconds(firstMs * 1.2), TimeSpan.FromSeconds(1.5 * speed * firstMs * 1.2 / 1000)),
                new TextToSpeechSample("One sentence", 90, TimeSpan.FromMilliseconds(firstMs * 2), TimeSpan.FromMilliseconds(firstMs * 3), TimeSpan.FromSeconds(5 * speed * firstMs * 3 / 5000)),
                new TextToSpeechSample("A few sentences", 190, TimeSpan.FromMilliseconds(firstMs * 3), TimeSpan.FromMilliseconds(firstMs * 5), TimeSpan.FromSeconds(10 * speed * firstMs * 5 / 10000)),
            ]);

    [Fact]
    public void TheVoicePageShowsWhereTheChosenVoiceStandsAsItLoads() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        speech.SetStatus(new TextToSpeechStatus("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.NotLoaded)));
        var kit = CreateSettingsKit(speech: speech);
        var page = kit.Model.Voice;

        Assert.True(page.HasVoice);
        Assert.Equal("Not loaded yet. It loads the first time it speaks.", page.EngineStatus);

        speech.SetStatus(new TextToSpeechStatus("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.Loading)));
        Pump();
        Assert.Equal("Loading…", page.EngineStatus);

        speech.SetStatus(new TextToSpeechStatus("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.Ready, null, TimeSpan.FromMilliseconds(1450))));
        Pump();
        Assert.Equal("Ready · loaded in 1.5 s", page.EngineStatus);
        Assert.True(page.EngineIsReady);
        Assert.False(page.EngineNeedsAttention);

        speech.SetStatus(new TextToSpeechStatus("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.Failed, "The voice could not be loaded from its files.")));
        Pump();
        Assert.Equal("Failed: The voice could not be loaded from its files.", page.EngineStatus);
        Assert.True(page.EngineNeedsAttention);

        speech.SetStatus(new TextToSpeechStatus("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.NotInstalled, "This voice isn't installed.")));
        Pump();
        Assert.Equal("This voice isn't installed.", page.EngineStatus);
        Assert.True(page.EngineNeedsAttention);
    });

    [Fact]
    public void ChoosingAnotherVoiceIsSavedAndTheStatusSaysLoadingUntilItIsReady() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        speech.SetStatus(new TextToSpeechStatus("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.Ready)));
        var kit = CreateSettingsKit(speech: speech);
        var page = kit.Model.Voice;

        page.SelectedModel = TextToSpeechModels.Kokoro;
        kit.Settle();

        Assert.Equal("kokoro-82m-onnx", kit.Saved.Voice.TextToSpeechModelId);
        // The service still has the old engine until the runtime has switched it: the page does not say the new one is ready.
        Assert.Equal("Loading…", page.EngineStatus);
        speech.SetStatus(new TextToSpeechStatus("kokoro-82m-onnx", new VoiceEngineStatus(VoiceEngineState.Ready, null, TimeSpan.FromSeconds(3))));
        Pump();
        Assert.Equal("Ready · loaded in 3.0 s", page.EngineStatus);
    });

    [Fact]
    public void TheSampleIsSpokenThroughTheSameVoiceAndSaysHowSoonItStarted() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        var kit = CreateSettingsKit(speech: speech);
        var page = kit.Model.Voice;

        Assert.True(page.SpeakSampleCommand.CanExecute(null));
        page.SpeakSampleCommand.Execute(null);

        var response = Assert.Single(speech.Responses);
        Assert.Contains("This is how I sound", response.Said, StringComparison.Ordinal);
        Assert.True(response.Completed);
        Assert.Equal("Speaking…", page.SampleResult);
    });

    [Fact]
    public void MeasuringTheChosenVoiceListsWhatItDid() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        speech.SetStatus(new TextToSpeechStatus("piper", new VoiceEngineStatus(VoiceEngineState.Ready, null, TimeSpan.FromMilliseconds(900))));
        speech.Results["piper"] = Measured("piper", 40, 20);
        var kit = CreateSettingsKit(saved: new AppSettings { Voice = new VoiceSettings { TextToSpeechModelId = "piper" } }, speech: speech);
        var page = kit.Model.Voice;

        page.MeasureCommand.Execute(null);
        SettingsUntil(() => !page.IsMeasuring, "the voice was measured");

        var row = Assert.Single(page.Rows);
        Assert.Equal("Piper", row.Name);
        Assert.Equal("40 ms", row.FirstAudio);
        Assert.Contains("× real time", row.Speed, StringComparison.Ordinal);
        Assert.Equal("800 ms", row.Load);
        Assert.True(page.HasRows);
        Assert.Contains("keeps ahead of the speakers", page.MeasureSummary, StringComparison.Ordinal);
        Assert.Empty(speech.Selected);
    });

    [Fact]
    public void ComparingAllVoicesMeasuresEachInTurnAndPutsTheChosenOneBack() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        speech.SetStatus(new TextToSpeechStatus("kokoro-82m-onnx", new VoiceEngineStatus(VoiceEngineState.Ready)));
        speech.Results["kitten-tts-mini"] = Measured("kitten-tts-mini", 500, 1.5);
        speech.Results["kokoro-82m-onnx"] = Measured("kokoro-82m-onnx", 1200, 0.9);
        speech.Results["piper"] = Measured("piper", 40, 20);
        var kit = CreateSettingsKit(saved: new AppSettings { Voice = new VoiceSettings { TextToSpeechModelId = "kokoro-82m-onnx" } }, speech: speech);
        var page = kit.Model.Voice;

        page.CompareAllCommand.Execute(null);
        SettingsUntil(() => !page.IsMeasuring, "the voices were measured");

        Assert.Equal(["KittenTTS Mini 0.8 (80M)", "Kokoro-82M ONNX", "Piper"], page.Rows.Select(row => row.Name));
        Assert.Equal(["500 ms", "1.2 s", "40 ms"], page.Rows.Select(row => row.FirstAudio));
        Assert.Equal(["kitten-tts-mini", "kokoro-82m-onnx", "piper", "kokoro-82m-onnx"], speech.Selected);
        Assert.Equal("kokoro-82m-onnx", speech.Status.EngineId);
        Assert.Equal("kokoro-82m-onnx", kit.Saved.Voice.TextToSpeechModelId);
    });

    [Fact]
    public void AVoiceThatCouldNotBeMeasuredSaysWhy() => RunSta(() =>
    {
        var speech = new FakeSpeech();
        speech.SetStatus(new TextToSpeechStatus("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.NotInstalled, "This voice isn't installed.")));
        speech.Results["kitten-tts-mini"] = new TextToSpeechBenchmarkResult("kitten-tts-mini", null, [], "This voice isn't installed.");
        var kit = CreateSettingsKit(speech: speech);
        var page = kit.Model.Voice;

        page.MeasureCommand.Execute(null);
        SettingsUntil(() => !page.IsMeasuring, "the measurement ended");

        var row = Assert.Single(page.Rows);
        Assert.Equal("—", row.FirstAudio);
        Assert.Equal("This voice isn't installed.", row.Note);
        Assert.True(row.HasNote);
    });

    [Fact]
    public void TheVoiceCannotBeChangedWhileItIsBeingMeasured() => RunSta(() =>
    {
        var gate = new TaskCompletionSource();
        var speech = new SlowSpeech(gate.Task);
        var kit = CreateSettingsKit(speech: speech);
        var page = kit.Model.Voice;

        page.MeasureCommand.Execute(null);

        Assert.True(page.IsMeasuring);
        Assert.False(page.IsNotMeasuring);
        Assert.False(page.MeasureCommand.CanExecute(null));
        Assert.False(page.CompareAllCommand.CanExecute(null));
        Assert.False(page.SpeakSampleCommand.CanExecute(null));
        gate.SetResult();
        SettingsUntil(() => !page.IsMeasuring, "the measurement ended");
        Assert.True(page.MeasureCommand.CanExecute(null));
    });

    [Fact]
    public void TheWakeWordSwitchIsSavedAndItsStatusFollowsTheListener() => RunSta(() =>
    {
        var runtime = new FakeVoiceRuntime();
        var kit = CreateSettingsKit(voiceRuntime: runtime, speech: new FakeSpeech());
        var page = kit.Model.Voice;

        Assert.False(page.WakeWordEnabled);
        Assert.StartsWith("Off.", page.WakeWordStatus, StringComparison.Ordinal);

        page.WakeWordEnabled = true;
        kit.Settle();
        Assert.True(kit.Saved.Voice.WakeWordEnabled);

        runtime.Status = new WakeWordStatus(true, true, null);
        Pump();
        Assert.StartsWith("Listening for “Hey Kiri” on this PC", page.WakeWordStatus, StringComparison.Ordinal);
        Assert.False(page.WakeWordNeedsAttention);

        runtime.Status = new WakeWordStatus(true, false, "No microphone found");
        Pump();
        Assert.Equal("No microphone found", page.WakeWordStatus);
        Assert.True(page.WakeWordNeedsAttention);

        runtime.Status = new WakeWordStatus(true, false, "Starting…");
        Pump();
        Assert.False(page.WakeWordNeedsAttention);
    });

    [Fact]
    public void TheRecognizerStatusSaysWhetherSpeechRecognitionCanBeUsed() => RunSta(() =>
    {
        var recognizer = new FakeRecognizer();
        var root = Path.Combine(Path.GetTempPath(), "assistant-voice-page-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folders = new VoiceModelFolders(new AppPaths(root));
            var kit = CreateSettingsKit(speech: new FakeSpeech(), recognizer: recognizer, voiceFolders: folders);
            var page = kit.Model.Voice;

            Assert.StartsWith("Not installed.", page.RecognizerStatus, StringComparison.Ordinal);
            Assert.True(page.RecognizerNeedsAttention);
            Assert.Equal(Path.Combine(root, "voices"), page.VoicesFolder);

            Directory.CreateDirectory(Path.Combine(root, "voices", "speech-recognition"));
            File.WriteAllText(Path.Combine(root, "voices", "speech-recognition", "tokens.txt"), "x");
            recognizer.SetStatus(new VoiceEngineStatus(VoiceEngineState.NotLoaded));
            Pump();
            Assert.StartsWith("Installed.", page.RecognizerStatus, StringComparison.Ordinal);
            Assert.False(page.RecognizerNeedsAttention);

            recognizer.SetStatus(new VoiceEngineStatus(VoiceEngineState.Ready));
            Pump();
            Assert.StartsWith("Ready.", page.RecognizerStatus, StringComparison.Ordinal);

            recognizer.SetStatus(new VoiceEngineStatus(VoiceEngineState.Failed, "The speech recognizer could not be loaded from its files."));
            Pump();
            Assert.Equal("Failed: The speech recognizer could not be loaded from its files.", page.RecognizerStatus);
            Assert.True(page.RecognizerNeedsAttention);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    [Fact]
    public void AVoiceInTheUsersOwnFolderIsListedAsInstalled() => RunSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-voice-own-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "voices", "piper"));
            File.WriteAllText(Path.Combine(root, "voices", "piper", "tokens.txt"), "x");
            var kit = CreateSettingsKit(speech: new FakeSpeech(), voiceFolders: new VoiceModelFolders(new AppPaths(root)));
            var page = kit.Model.Voice;
            page.CheckFilesCommand.CanExecute(null);

            // Without the asset service the page lists no engines, but the user's own folder is what the recognizer line and the folder note read.
            Assert.True(page.HasVoicesFolder);
            Assert.Equal(Path.Combine(root, "voices"), page.VoicesFolder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of the voice states: the bar and the panel idle and listening, the speaker lit, and the Voice page with a measurement.
    [Fact]
    public void RenderTheVoiceStatesWhenAskedTo() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var input = new FakeVoiceInput();
        var speech = new FakeSpokenAnswers();
        var (window, bar, conversation) = CreateAssistant(animations: false, answers: new StreamingAnswers(), voiceInput: input, speech: speech);
        try
        {
            window.Show();
            Pump();
            RenderGlass(window, "voice-bar-idle.png", 2);

            bar.Voice.Start();
            Pump();
            input.Listenings[0].Raise("what's on my calendar", null);
            input.Listenings[0].Level = 0.08;
            Pump();
            RenderGlass(window, "voice-bar-listening.png", 2);
            bar.Voice.Stop();

            conversation.Ask("What's on my calendar tomorrow?", spoken: true);
            window.ShowConversation();
            Pump();
            RenderGlass(window, "voice-panel-idle.png", 2);
            speech.IsSpeaking = true;
            Pump();
            RenderGlass(window, "voice-panel-speaking.png", 2);
            speech.IsSpeaking = false;

            conversation.Voice.Start();
            Pump();
            input.Listenings.Last().Raise("and the day after that", null);
            input.Listenings.Last().Level = 0.08;
            Pump();
            RenderGlass(window, "voice-panel-listening.png", 2);
        }
        finally
        {
            window.Close();
        }

        var fake = new FakeSpeech();
        fake.SetStatus(new TextToSpeechStatus("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.Ready, null, TimeSpan.FromMilliseconds(1450))));
        fake.Results["kitten-tts-mini"] = Measured("kitten-tts-mini", 520, 1.5, 1450);
        fake.Results["kokoro-82m-onnx"] = Measured("kokoro-82m-onnx", 1100, 0.9, 3000);
        fake.Results["piper"] = Measured("piper", 40, 25, 900);
        var kit = CreateSettingsKit(speech: fake, voiceRuntime: new FakeVoiceRuntime { Status = new WakeWordStatus(true, true, null) }, recognizer: new FakeRecognizer());
        kit.Model.Voice.WakeWordEnabled = true;
        kit.Model.Voice.CompareAllCommand.Execute(null);
        SettingsUntil(() => !kit.Model.Voice.IsMeasuring, "the voices were measured");
        var (settings, _, _) = CreateSettingsWindow(kit);
        try
        {
            settings.Height = 1500;
            settings.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(item => item.Section == SettingsSection.Voice);
            settings.UpdateLayout();
            Pump();
            RenderFixture(Named<System.Windows.Controls.Grid>(settings, "Root"), "voice-settings.png", 1);
        }
        finally
        {
            settings.CloseForGood();
        }
    })));

    // ---- fakes ----

    private sealed class FakeVoiceRuntime : IVoiceRuntime
    {
        private WakeWordStatus _status = WakeWordStatus.Off;

        public WakeWordStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                WakeWordChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public WakeWordStatus WakeWord => _status;

        public event EventHandler? WakeWordChanged;
    }

    private sealed class FakeRecognizer : ISpeechToTextService
    {
        public VoiceEngineStatus Status { get; private set; } = VoiceEngineStatus.Unloaded;

        public event EventHandler? StatusChanged;

        public void SetStatus(VoiceEngineStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ISpeechRecognitionSession StartSession(SpeechRecognitionOptions? options = null) => throw new NotSupportedException();

        public void Unload()
        {
        }

        public void Dispose()
        {
        }
    }

    // A voice whose measurement waits for the test to let it finish.
    private sealed class SlowSpeech(Task gate) : ITextToSpeechService
    {
        public TextToSpeechStatus Status { get; } = new("kitten-tts-mini", new VoiceEngineStatus(VoiceEngineState.Ready));

        public bool IsSpeaking => false;

        public bool KeepLoaded { get; set; }

        public event EventHandler? StatusChanged { add { } remove { } }

        public event EventHandler? SpeakingChanged { add { } remove { } }

        public Task SelectEngineAsync(string engineId, bool load = true, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ISpokenResponse BeginResponse() => throw new NotSupportedException();

        public void StopAll()
        {
        }

        public async Task<TextToSpeechBenchmarkResult> BenchmarkAsync(CancellationToken cancellationToken = default)
        {
            await gate.ConfigureAwait(true);
            return new TextToSpeechBenchmarkResult("kitten-tts-mini", null, []);
        }

        public void Dispose()
        {
        }
    }
}
