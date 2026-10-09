using Assistant.Core.Voice;

namespace Assistant.UI.Voice;

/// <summary>Handy owns the only ASR model. Its normal paste is accepted only by Kiri's focused prompt during our request.</summary>
public sealed class HandySpeechToTextService(HandyIntegration handy) : ISpeechToTextService
{
    private readonly HandyIntegration _handy = handy;
    private readonly object _gate = new();
    private Session? _session;
    private VoiceEngineStatus _status = VoiceEngineStatus.Unloaded;
    internal static HandySpeechToTextService? Active { get; private set; }
    public VoiceEngineStatus Status => _status;
    public event EventHandler? StatusChanged;
    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = _handy.Detect();
        SetStatus(found.Installed && found.ModelInstalled ? found.PasteCompatible ? new(VoiceEngineState.Ready)
            : new(VoiceEngineState.Failed, "In Handy, choose Ctrl+V paste and turn off automatic submission, then click Refresh.") : new(VoiceEngineState.NotInstalled,
            found.Installed ? "Open Handy, download and select a model, then click Refresh." : "Install Handy, select a model in it, then click Refresh."));
        return Task.CompletedTask;
    }
    public ISpeechRecognitionSession StartSession(SpeechRecognitionOptions? options = null)
    {
        Unload();
        var session = new Session(this, options ?? new());
        lock (_gate) { _session = session; Active = this; }
        session.Start(); return session;
    }
    /// <summary>
    /// Takes what Handy has just put on the clipboard as the transcript of the request that is waiting for it. Handy hands its words over by pasting
    /// them (Ctrl+V) into whatever has the keyboard; a window of the Assistant that hears that key while it listens calls this, so that the words arrive
    /// whether or not a text field has the keyboard at that moment (the floating conversation's composer is away while the microphone is on). It is
    /// <see langword="false"/>, and nothing is read, unless a request is waiting for Handy's words.
    /// </summary>
    internal static bool TryAcceptClipboard()
    {
        if (Active is not { } handy || !handy.IsWaitingForPaste)
        {
            return false;
        }

        try
        {
            return System.Windows.Clipboard.ContainsText() && handy.AcceptPaste(System.Windows.Clipboard.GetText());
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            // Another program has the clipboard open at this moment: Handy's paste is then lost, as it would be in any application.
            return false;
        }
    }

    private bool IsWaitingForPaste
    {
        get
        {
            Session? session; lock (_gate) session = _session;
            return session?.IsWaiting == true;
        }
    }

    internal bool AcceptPaste(string text)
    {
        Session? session; lock (_gate) session = _session;
        return session?.Accept(text) == true;
    }
    public void Unload()
    {
        Session? previous; lock (_gate) { previous = _session; _session = null; if (ReferenceEquals(Active, this)) Active = null; }
        previous?.Dispose();
        SetStatus(VoiceEngineStatus.Unloaded);
    }
    public void Dispose() => Unload();
    private void SetStatus(VoiceEngineStatus status) { _status = status; StatusChanged?.Invoke(this, EventArgs.Empty); }
    private sealed class Session(HandySpeechToTextService owner, SpeechRecognitionOptions options) : ISpeechRecognitionSession
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _command = new(1);
        private double _duration, _silence;
        private bool _heard;
        private volatile bool _started, _waiting;
        private int _ended, _finish;
        public event EventHandler<SpeechTranscriptEventArgs>? Transcribed;
        public bool IsEnded => Volatile.Read(ref _ended) != 0;
        internal bool IsWaiting => _waiting && !IsEnded;
        internal void Start() => _ = Task.Run(async () =>
        {
            await _command.WaitAsync().ConfigureAwait(false);
            try
            {
                await owner.WarmUpAsync(_stop.Token).ConfigureAwait(false);
                if (!owner.Status.IsReady) { End("", SpeechEndReason.Finished); return; }
                // Started hidden only if it is not running. A running Handy is sent the toggle alone: any other command line makes it show its window,
                // which would take the keyboard from the prompt its paste is meant for.
                await owner._handy.EnsureRunningAsync(_stop.Token).ConfigureAwait(false);
                _started = true;
                await owner._handy.CommandAsync("--toggle-transcription", _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) { owner.SetStatus(new(VoiceEngineState.Failed, "Handy could not start dictation. Open Handy and try again.")); End("", SpeechEndReason.Finished); }
            finally { _command.Release(); }
        });
        public void Push(ReadOnlySpan<short> samples)
        {
            if (IsEnded || Volatile.Read(ref _finish) != 0 || !_started || samples.IsEmpty) return;
            double sum = 0; foreach (var sample in samples) sum += (double)sample * sample;
            var voiced = Math.Sqrt(sum / samples.Length) / 32768 > 0.012;
            _heard |= voiced; _duration += samples.Length / 16000d; _silence = voiced ? 0 : _silence + samples.Length / 16000d;
            if (_duration >= options.MaxLength.TotalSeconds || _heard && _silence >= options.TrailingSilence.TotalSeconds) Finish();
            else if (!_heard && _duration >= options.InitialSilence.TotalSeconds) { _ = CancelAsync(); End("", SpeechEndReason.NoSpeech); }
        }
        public void Finish()
        {
            if (IsEnded || Interlocked.Exchange(ref _finish, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                await _command.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_started || IsEnded) return;
                    _waiting = true;
                    await owner._handy.CommandAsync("--toggle-transcription", _stop.Token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(45), _stop.Token).ConfigureAwait(false);
                    if (!IsEnded) { owner.SetStatus(new(VoiceEngineState.Failed, "Handy did not paste a transcript. In Handy, choose Ctrl+V paste and disable automatic submission.")); End("", SpeechEndReason.Finished); }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is not OutOfMemoryException) { owner.SetStatus(new(VoiceEngineState.Failed, "Handy dictation stopped. Open Handy and try again.")); End("", SpeechEndReason.Finished); }
                finally { _command.Release(); }
            });
        }
        internal bool Accept(string text)
        {
            if (!_waiting || IsEnded || string.IsNullOrWhiteSpace(text) || text.Length > 65536) return false;
            End(text, SpeechEndReason.Finished); return true;
        }
        private void End(string text, SpeechEndReason reason)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            _stop.Cancel(); Transcribed?.Invoke(this, new(text, reason));
        }
        public void Dispose()
        {
            var cancel = Interlocked.Exchange(ref _ended, 1) == 0 && _started;
            _stop.Cancel();
            if (cancel) _ = CancelAsync();
        }
        private async Task CancelAsync()
        {
            await _command.WaitAsync().ConfigureAwait(false);
            // Only a running Handy has anything to cancel; one that is gone would be started by the command, window and all.
            try { if (owner._handy.IsRunning()) await owner._handy.CommandAsync("--cancel", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            finally { _command.Release(); }
        }
    }
}
