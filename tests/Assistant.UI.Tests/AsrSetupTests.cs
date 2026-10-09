using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Assistant.UI.Onboarding;
using Assistant.UI.Voice;
using Assistant.Windows.Frame;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void AsrSetupShowsSharedHandyWindowsAndLocalChoicesWithEstimates() => RunSta(() => WithTheme(() =>
    {
        using var kit = new SetupKit();
        kit.Setup.EnableVoiceControl = true;
        using var errors = BindingErrors.Listen();
        var window = new OnboardingWindow(kit.Setup, new FakeFrameFactory(), new FakePlacement(), initialize: false) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.ShowStep(2); window.Show(); Pump();
            Assert.Contains(kit.Setup.AsrModels, option => option.Id == "handy");
            Assert.Contains(kit.Setup.AsrModels, option => option.Id == "windows");
            Assert.All(kit.Setup.AsrModels, option => { Assert.NotEmpty(option.Size); Assert.NotEmpty(option.Ram); });
            foreach (var id in new[] { "handy", "windows", "asr-whisper-small", "asr-parakeet-v3", "asr-moonshine-tiny" })
            {
                kit.Setup.SelectedAsr = kit.Setup.AsrModels.Single(option => option.Id == id);
                window.UpdateLayout(); Pump(); RenderFixture((FrameworkElement)window.Content, "asr-" + id + ".png");
                Assert.Contains(Descendants<CheckBox>(window), checkbox => checkbox.IsVisible && checkbox.Content.ToString()!.Contains("Wake Kiri"));
            }
            Assert.Empty(errors.Messages);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void AsrChoiceSavesModelDeviceAndWakeWordTogether() => RunSta(() =>
    {
        using var kit = new SetupKit();
        kit.Setup.SelectedAsr = kit.Setup.AsrModels.Single(option => option.Id == "asr-whisper-small");
        kit.Library.Downloaded.Add(kit.Setup.SelectedAsr.Id);
        kit.Library.Downloaded.Add(kit.Setup.WakeWord.Id);
        kit.Setup.EnableVoiceControl = true; kit.Setup.EnableWakeWord = true;
        SettingsWait(kit.Setup.SaveVoiceControlAsync());
        var voice = kit.Settings.LoadAsync().Result.Voice;
        Assert.Equal("asr-whisper-small", voice.SpeechRecognitionModelId);
        Assert.Equal("cpu", voice.SpeechRecognitionDevice);
        Assert.True(voice.WakeWordEnabled);
        kit.Setup.SelectedAsr = kit.Setup.AsrModels.Single(option => option.Id == "asr-parakeet-v3");
        Assert.Single(kit.Setup.AsrDevices); // Bundled ONNX recognizers support CPU, not Vulkan.
        Assert.False(kit.Setup.CanContinueVoiceControl);
    });

    [Fact]
    public async Task HandySharingForwardsCommandsAndAcceptsOnlyOurPendingPaste()
    {
        var integration = new AsrTestHandy();
        using var service = new HandySpeechToTextService(integration);
        await service.WarmUpAsync(); Assert.True(service.Status.IsReady);
        Assert.False(service.AcceptPaste("unrelated paste"));
        using var session = service.StartSession();
        var result = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Transcribed += (_, transcript) => result.TrySetResult(transcript);
        await WaitAsr(() => integration.Commands.Contains("--toggle-transcription"));
        Assert.False(service.AcceptPaste("paste during recording"));
        session.Finish();
        await WaitAsr(() => integration.Commands.Count(command => command == "--toggle-transcription") == 2);
        Assert.True(service.AcceptPaste("a test question"));
        Assert.Equal("a test question", (await result.Task.WaitAsync(TimeSpan.FromSeconds(3))).Text);
        Assert.False(service.AcceptPaste("another paste"));
        Assert.DoesNotContain("--transcribe-file", integration.Commands);
        Assert.DoesNotContain("--toggle-post-process", integration.Commands);
    }

    [Fact]
    public async Task ARunningHandyIsOnlySentTheToggle_SoItNeverOpensItsWindowOverThePrompt()
    {
        // Handy shows its main window for any command line that is not one of its remote controls, "--start-hidden" among them once it is running.
        var integration = new AsrTestHandy { Running = true };
        using var service = new HandySpeechToTextService(integration);
        using var session = service.StartSession();
        await WaitAsr(() => integration.Commands.Contains("--toggle-transcription"));
        session.Finish();
        await WaitAsr(() => integration.Commands.Count(command => command == "--toggle-transcription") == 2);

        Assert.Equal(["--toggle-transcription", "--toggle-transcription"], integration.Commands.ToArray());
        Assert.Equal(0, integration.Starts);
    }

    [Fact]
    public async Task AHandyThatIsNotRunningIsStartedHiddenFirst_AndThenSentTheToggle()
    {
        var integration = new AsrTestHandy { Running = false };
        using var service = new HandySpeechToTextService(integration);
        using var session = service.StartSession();
        await WaitAsr(() => integration.Commands.Contains("--toggle-transcription"));

        Assert.Equal(1, integration.Starts);
        Assert.True(integration.Running);
        Assert.Equal(["--toggle-transcription"], integration.Commands.ToArray());

        // The next request finds it running and leaves it alone.
        using var again = service.StartSession();
        await WaitAsr(() => integration.Commands.Count(command => command == "--toggle-transcription") == 2);
        Assert.Equal(1, integration.Starts);
        Assert.DoesNotContain("--start-hidden", integration.Commands);
    }

    [Fact]
    public async Task HandyWithoutAModelOrCompatiblePasteCannotBeSelectedAsReady()
    {
        var integration = new AsrTestHandy { Installation = new("handy.exe", "Not selected", false, "CPU") };
        using var service = new HandySpeechToTextService(integration);
        await service.WarmUpAsync(); Assert.False(service.Status.IsReady);
        integration.Installation = new("handy.exe", "Parakeet", true, "CPU", PasteCompatible: false);
        await service.WarmUpAsync(); Assert.False(service.Status.IsReady);
        Assert.Contains("Ctrl+V", service.Status.Message);
    }

    [Fact]
    public async Task WindowsRecognizerTranscribesSynthesizedLocalAudioWhenInstalled()
    {
        if (!WindowsSpeechRecognizer.IsAvailable()) return;
        using var audio = new System.IO.MemoryStream();
        using (var voice = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            voice.SetOutputToAudioStream(audio, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            voice.Speak("Hello, this is a test of speech recognition.");
        }
        var bytes = audio.ToArray(); var pcm = new short[bytes.Length / 2];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(bytes, i * 2);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await WindowsSpeechRecognizer.RecognizeAsync(pcm, limit.Token).WaitAsync(limit.Token);
        Assert.True(result.Length > 5, "Windows recognition did not return words from the local test sample.");
    }

    private static async Task WaitAsr(Func<bool> ready)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, limit.Token);
    }
    private sealed class AsrTestHandy : HandyIntegration
    {
        public HandyInstallation Installation { get; set; } = new("handy.exe", "Parakeet", true, "CPU");
        public ConcurrentQueue<string> Commands { get; } = new();
        public bool Running { get; set; } = true;
        public int Starts { get; private set; }
        public override HandyInstallation Detect() => Installation;
        public override bool IsRunning() => Running;
        // Nothing is started: only that it was asked for is kept. The real one waits here for Handy to be ready.
        public override bool StartHidden() { Starts++; Running = true; return true; }
        public override Task<bool> EnsureRunningAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Running) return Task.FromResult(false);
            StartHidden();
            return Task.FromResult(true);
        }
        public override Task CommandAsync(string argument, CancellationToken token) { token.ThrowIfCancellationRequested(); Commands.Enqueue(argument); return Task.CompletedTask; }
    }
}
