using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using Assistant.Core.Voice;

namespace Assistant.UI.Voice;

/// <summary>The installed Windows SAPI recognizer, with a local dictation grammar and in-memory PCM.</summary>
public static class WindowsSpeechRecognizer
{
    public static bool IsAvailable()
    {
        try { return SpeechRecognitionEngine.InstalledRecognizers().Count > 0; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
    public static Task<string> RecognizeAsync(short[] audio, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            var info = installed.FirstOrDefault(recognizer => recognizer.Culture.Equals(System.Globalization.CultureInfo.CurrentUICulture)) ?? installed.FirstOrDefault();
            if (info is null) throw new VoiceEngineException(VoiceEngineFailure.NotInstalled, "Install a Windows speech language in Windows Settings → Time & language → Speech.");
            using var engine = new SpeechRecognitionEngine(info);
            using var stream = new MemoryStream(System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.AsSpan()).ToArray());
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToAudioStream(stream, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            engine.InitialSilenceTimeout = TimeSpan.FromSeconds(8);
            engine.EndSilenceTimeout = TimeSpan.FromSeconds(0.5);
            var text = new List<string>();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.SpeechRecognized += (_, result) => text.Add(result.Result.Text);
            engine.RecognizeCompleted += (_, result) =>
            {
                if (result.Error is not null) completed.TrySetException(new VoiceEngineException(VoiceEngineFailure.Failed, "Windows speech recognition could not process this audio."));
                else completed.TrySetResult();
            };
            using var stop = token.Register(() => { try { engine.RecognizeAsyncCancel(); } catch (InvalidOperationException) { } });
            engine.RecognizeAsync(RecognizeMode.Multiple);
            await completed.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return string.Join(" ", text);
        }, token);
    }
}
