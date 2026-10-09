using Assistant.Core.Voice;

namespace Assistant.Voice;

/// <summary>Bounded, in-memory utterances for local batch recognizers. No audio files are written.</summary>
public sealed class BufferedSpeechToTextService(Func<short[], CancellationToken, Task<string>> recognize,
    Func<CancellationToken, Task>? prepare = null) : ISpeechToTextService
{
    private readonly Func<short[], CancellationToken, Task<string>> _recognize = recognize;
    private readonly object _gate = new();
    private readonly HashSet<Session> _sessions = [];
    private VoiceEngineStatus _status = VoiceEngineStatus.Unloaded;
    public VoiceEngineStatus Status => _status;
    public event EventHandler? StatusChanged;
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(new(VoiceEngineState.Loading));
        try
        {
            if (prepare is not null) await prepare(cancellationToken).ConfigureAwait(false);
            SetStatus(new(VoiceEngineState.Ready));
        }
        catch (OperationCanceledException) { SetStatus(VoiceEngineStatus.Unloaded); throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(new(ex is VoiceEngineException { Failure: VoiceEngineFailure.NotInstalled } ? VoiceEngineState.NotInstalled : VoiceEngineState.Failed,
                ex is VoiceEngineException ? ex.Message : "Speech recognition could not start. Choose another model or device."));
        }
    }
    public ISpeechRecognitionSession StartSession(SpeechRecognitionOptions? options = null)
    {
        var session = new Session(this, options ?? new());
        lock (_gate) _sessions.Add(session);
        return session;
    }
    public void Unload()
    {
        Session[] sessions; lock (_gate) sessions = [.. _sessions];
        foreach (var session in sessions) session.Dispose();
        SetStatus(VoiceEngineStatus.Unloaded);
    }
    public void Dispose() => Unload();
    private void SetStatus(VoiceEngineStatus status) { _status = status; StatusChanged?.Invoke(this, EventArgs.Empty); }

    private sealed class Session(BufferedSpeechToTextService owner, SpeechRecognitionOptions options) : ISpeechRecognitionSession
    {
        private readonly object _gate = new();
        private readonly List<short> _audio = [];
        private readonly CancellationTokenSource _stop = new();
        private double _silence;
        private bool _speech, _finishing;
        private int _ended;
        public bool IsEnded => Volatile.Read(ref _ended) != 0;
        public event EventHandler<SpeechTranscriptEventArgs>? Transcribed;
        public void Push(ReadOnlySpan<short> samples)
        {
            SpeechEndReason? end = null;
            lock (_gate)
            {
                if (IsEnded || _finishing || samples.IsEmpty) return;
                var count = Math.Min(samples.Length, Math.Max(0, VoiceAudio.SamplesIn(options.MaxLength) - _audio.Count));
                double energy = 0;
                foreach (var sample in samples[..count]) { _audio.Add(sample); energy += (double)sample * sample; }
                var voiced = count > 0 && Math.Sqrt(energy / count) / 32768 > 0.012;
                _speech |= voiced; _silence = voiced ? 0 : _silence + count / (double)VoiceAudio.SampleRate;
                if (_audio.Count >= VoiceAudio.SamplesIn(options.MaxLength)) end = SpeechEndReason.TooLong;
                else if (_speech && _silence >= options.TrailingSilence.TotalSeconds) end = SpeechEndReason.Endpoint;
                else if (!_speech && _audio.Count >= VoiceAudio.SamplesIn(options.InitialSilence)) end = SpeechEndReason.NoSpeech;
            }
            if (end is { } reason) Complete(reason);
        }
        public void Finish() => Complete(SpeechEndReason.Finished);
        private void Complete(SpeechEndReason reason)
        {
            short[] audio;
            lock (_gate)
            {
                if (IsEnded || _finishing) return;
                _finishing = true;
                var skip = Math.Min(_audio.Count, VoiceAudio.SamplesIn(options.IgnoreWordsBefore));
                audio = _speech ? _audio.Skip(skip).ToArray() : [];
                _audio.Clear();
            }
            _ = Task.Run(async () =>
            {
                var text = "";
                try
                {
                    if (audio.Length > 0)
                    {
                        if (!owner.Status.IsReady) await owner.WarmUpAsync(_stop.Token).ConfigureAwait(false);
                        if (owner.Status.IsReady) text = await owner._recognize(audio, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    owner.SetStatus(new(VoiceEngineState.Failed, ex is VoiceEngineException ? ex.Message : "Speech recognition failed. Try another model or device."));
                    reason = SpeechEndReason.Finished;
                }
                finally { Array.Clear(audio); }
                if (Interlocked.Exchange(ref _ended, 1) == 0) Transcribed?.Invoke(this, new(text.Trim(), reason));
                lock (owner._gate) owner._sessions.Remove(this);
            });
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _ended, 1); _stop.Cancel();
            lock (_gate) _audio.Clear();
            lock (owner._gate) owner._sessions.Remove(this);
        }
    }
}
