using Assistant.Core.Models;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Assistant.Voice;
using System.Diagnostics;

namespace Assistant.UI.Voice;

/// <summary>Only one ASR provider exists at a time. Selecting Handy releases every Kiri recognizer.</summary>
public sealed class SelectableSpeechToTextService(VoiceModelFolders folders, HandyIntegration handy) : ISpeechToTextService
{
    private ISpeechToTextService _engine = new SherpaSpeechToTextService(folders);
    private string _id = "speech-recognition", _device = "cpu";
    public VoiceEngineStatus Status => _engine.Status;
    public event EventHandler? StatusChanged;
    public Task ConfigureAsync(VoiceSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_id == settings.SpeechRecognitionModelId && _device == settings.SpeechRecognitionDevice) return Task.CompletedTask;
        _engine.StatusChanged -= OnStatus; _engine.Dispose();
        _id = settings.SpeechRecognitionModelId; _device = settings.SpeechRecognitionDevice;
        _engine = _id switch
        {
            "handy" => new HandySpeechToTextService(handy),
            "windows" => new BufferedSpeechToTextService(WindowsSpeechRecognizer.RecognizeAsync, _ => WindowsSpeechRecognizer.IsAvailable() ? Task.CompletedTask : Task.FromException(new VoiceEngineException(VoiceEngineFailure.NotInstalled, "Install a Windows speech language in Windows Settings → Time & language → Speech."))),
            "speech-recognition" => new SherpaSpeechToTextService(folders),
            _ => CreateNative(_id, _device),
        };
        _engine.StatusChanged += OnStatus;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    private ISpeechToTextService CreateNative(string id, string device)
    {
        var model = DownloadCatalog.SpeechRecognizers.Single(model => model.Id == id);
        var folder = folders.UserFolderOf(id);
        if (model.ArchiveRoot is not null)
            return new BufferedSpeechToTextService((audio, token) => SherpaOfflineAsr.RecognizeAsync(model.Id, folder, audio, token),
                token => SherpaOfflineAsr.PrepareAsync(model.Id, folder, token));
        return new BufferedSpeechToTextService((audio, token) => NativeAsrWorker.RecognizeAsync(model, folder, device, audio, token),
            async token => { await NativeAsrWorker.RecognizeAsync(model, folder, device, [], token).ConfigureAwait(false); });
    }
    private void OnStatus(object? sender, EventArgs args) => StatusChanged?.Invoke(this, args);
    public Task WarmUpAsync(CancellationToken cancellationToken = default) => _engine.WarmUpAsync(cancellationToken);
    public ISpeechRecognitionSession StartSession(SpeechRecognitionOptions? options = null) => _engine.StartSession(options);
    public void Unload() => _engine.Unload();
    public async Task<SpeechToTextBenchmarkResult> BenchmarkAsync(ReadOnlyMemory<short> audio, CancellationToken cancellationToken = default)
    {
        var duration = VoiceAudio.DurationOf(audio.Length);
        if (_id == "handy")
            return new(_id, null, null, duration, "", "Handy manages its shared recognizer. Use Handy's own recording test to measure it.");
        if (audio.IsEmpty)
            return new(_id, null, null, duration, "", "The local ASR test sample is empty.");

        var load = Stopwatch.StartNew();
        try
        {
            await _engine.WarmUpAsync(cancellationToken).ConfigureAwait(false);
            load.Stop();
            if (!_engine.Status.IsReady)
                return new(_id, load.Elapsed, null, duration, "", _engine.Status.Message ?? "The recognizer could not be loaded.");

            var transcript = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var session = _engine.StartSession(new SpeechRecognitionOptions
            {
                InitialSilence = TimeSpan.FromSeconds(2),
                TrailingSilence = TimeSpan.FromSeconds(1),
                MaxLength = duration + TimeSpan.FromSeconds(2),
            });
            session.Transcribed += (_, result) =>
            {
                if (result.IsFinal) transcript.TrySetResult(result);
            };

            var recognition = Stopwatch.StartNew();
            var samples = audio.ToArray();
            const int chunk = 1600;
            for (var offset = 0; offset < samples.Length; offset += chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.Push(samples.AsSpan(offset, Math.Min(chunk, samples.Length - offset)));
            }
            session.Finish();
            var final = await transcript.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            recognition.Stop();
            return new(_id, load.Elapsed, recognition.Elapsed, duration, final.Text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            load.Stop();
            return new(_id, load.Elapsed, null, duration, "", ex is VoiceEngineException ? ex.Message : "The recognizer could not complete its speed test.");
        }
        finally
        {
            _engine.Unload();
        }
    }
    public void Dispose() => _engine.Dispose();
}
