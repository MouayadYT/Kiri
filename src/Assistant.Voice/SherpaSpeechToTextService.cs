using System.Collections.Concurrent;
using System.Text;
using Assistant.Core.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace Assistant.Voice;

/// <summary>
/// Speech recognition on this PC (PROJECT_SPEC §4.2, step 125): a small streaming recognizer, read into memory the first time a request is spoken (or
/// sooner, when the Assistant warms it up) and run on the processor, which writes the words as they are said and notices when the speaker stops. Each request
/// is a <see cref="ISpeechRecognitionSession"/> with a thread of its own, so the microphone's thread only ever copies audio into a queue. The audio and
/// the words stay in memory and are never logged; nothing is sent anywhere.
/// </summary>
public sealed class SherpaSpeechToTextService : ISpeechToTextService
{
    // How much audio a session holds while the recognizer is still loading. A recognizer decodes many times faster than speech arrives, so a few
    // seconds of backlog is gone in a moment; more than this is a recognizer that will not load.
    private static readonly TimeSpan MaxBacklog = TimeSpan.FromSeconds(30);

    // Silence added after the last audio so the words in the recognizer's right context come out.
    private static readonly TimeSpan TailPadding = TimeSpan.FromSeconds(0.8);

    private readonly VoiceEngineHolder<LoadedRecognizer> _holder;
    private readonly ILogger _logger;

    /// <summary>Creates the service. Nothing is read until it is first needed.</summary>
    public SherpaSpeechToTextService(VoiceModelFolders folders, int threads = 2, ILogger<SherpaSpeechToTextService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _logger = logger ?? NullLogger<SherpaSpeechToTextService>.Instance;
        _holder = new VoiceEngineHolder<LoadedRecognizer>(() => Load(folders, Math.Max(1, threads)), _logger, "speech-recognition");
        _holder.StatusChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public event EventHandler? StatusChanged;

    /// <inheritdoc/>
    public VoiceEngineStatus Status => _holder.Status;

    /// <inheritdoc/>
    public Task WarmUpAsync(CancellationToken cancellationToken = default) => _holder.WarmUpAsync().WaitAsync(cancellationToken);

    /// <inheritdoc/>
    public ISpeechRecognitionSession StartSession(SpeechRecognitionOptions? options = null) => new Session(_holder, options ?? new SpeechRecognitionOptions(), _logger);

    /// <inheritdoc/>
    public void Unload() => _holder.Unload();

    /// <inheritdoc/>
    public void Dispose() => _holder.Dispose();

    private static LoadedRecognizer Load(VoiceModelFolders folders, int threads)
    {
        var folder = folders.Resolve(VoiceModelFolders.SpeechRecognitionGroup);
        var config = new OnlineRecognizerConfig
        {
            DecodingMethod = "greedy_search",

            // The recognizer's own end-of-sentence rule is not used: it counts silence at a frame rate that is wrong for some models. EndOfSpeechDetector decides.
            EnableEndpoint = 0,
        };
        config.FeatConfig.SampleRate = VoiceAudio.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Tokens = RequiredFile(folder, "tokens.txt");
        var encoder = FirstFile(folder, "encoder*.int8.onnx", "encoder*.onnx");
        var timestampScale = 1f;
        if (encoder is not null)
        {
            // A transducer: an encoder, a decoder and a joiner.
            config.ModelConfig.Transducer.Encoder = NativePath.Prepare(encoder);
            config.ModelConfig.Transducer.Decoder = NativePath.Prepare(
                FirstFile(folder, "decoder*.int8.onnx", "decoder*.onnx") ?? throw Incomplete());
            config.ModelConfig.Transducer.Joiner = NativePath.Prepare(
                FirstFile(folder, "joiner*.int8.onnx", "joiner*.onnx") ?? throw Incomplete());
        }
        else
        {
            // A single CTC network. Its frames are eight steps of the audio's 10 ms apart where the runtime counts four, so the times it reports for
            // each word are half of what they were.
            timestampScale = 2f;
            config.ModelConfig.NemoCtc.Model = NativePath.Prepare(
                FirstFile(folder, "model.int8.onnx", "model.onnx") ?? throw Incomplete());
        }

        try
        {
            return new LoadedRecognizer(new OnlineRecognizer(config), timestampScale);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, "The speech recognizer could not be loaded from its files.", exception);
        }
    }

    private static string RequiredFile(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        return File.Exists(path) ? NativePath.Prepare(path) : throw Incomplete();
    }

    private static string? FirstFile(string folder, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static VoiceEngineException Incomplete() =>
        new(VoiceEngineFailure.NotInstalled, "The speech recognizer's files are incomplete. Put all of its files in the Assistant's voices folder, or reinstall the Assistant with them.");

    // One request being listened to, on a thread of its own.
    private sealed class Session : ISpeechRecognitionSession
    {
        private readonly VoiceEngineHolder<LoadedRecognizer> _holder;
        private readonly SpeechRecognitionOptions _options;
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<short[]> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private long _queuedSamples;
        private volatile bool _finish;
        private volatile bool _ended;
        private int _disposed;
        private int _finalRaised;

        public Session(VoiceEngineHolder<LoadedRecognizer> holder, SpeechRecognitionOptions options, ILogger logger)
        {
            _holder = holder;
            _options = options;
            _logger = logger;
            _thread = new Thread(Run) { IsBackground = true, Name = "Speech recognition", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public event EventHandler<SpeechTranscriptEventArgs>? Transcribed;

        public bool IsEnded => _ended;

        public void Push(ReadOnlySpan<short> samples)
        {
            if (_ended || _finish || samples.IsEmpty)
            {
                return;
            }

            _queue.Enqueue(samples.ToArray());
            var queued = Interlocked.Add(ref _queuedSamples, samples.Length);

            // The recognizer is not loaded yet (or is far behind): keep the newest few seconds, not all of what was said.
            while (queued > VoiceAudio.SamplesIn(MaxBacklog) && _queue.TryDequeue(out var dropped))
            {
                queued = Interlocked.Add(ref _queuedSamples, -dropped.Length);
            }

            _signal.Release();
        }

        public void Finish()
        {
            _finish = true;
            _signal.Release();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ended = true;
            _stop.Cancel();
            _signal.Release();
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join(TimeSpan.FromSeconds(3));
            }

            _queue.Clear();
        }

        private void Run()
        {
            LoadedRecognizer? recognizer = null;
            OnlineStream? stream = null;
            try
            {
                recognizer = _holder.Acquire(_stop.Token);
                if (recognizer is null)
                {
                    End(new SpeechTranscriptEventArgs("", SpeechEndReason.Finished));
                    return;
                }

                stream = recognizer.Recognizer.CreateStream();
                Listen(recognizer, stream);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                VoiceLog.Failed(_logger, "speech-recognition", exception.GetType().Name);
                End(new SpeechTranscriptEventArgs("", SpeechEndReason.Finished));
            }
            finally
            {
                _ended = true;
                stream?.Dispose();
                if (recognizer is not null)
                {
                    _holder.Release();
                }
            }
        }

        private void Listen(LoadedRecognizer loaded, OnlineStream stream)
        {
            var recognizer = loaded.Recognizer;
            var ignoreBefore = (float)_options.IgnoreWordsBefore.TotalSeconds;
            long samples = 0;
            var lastText = "";
            var detector = new EndOfSpeechDetector(_options.TrailingSilence);
            while (!_stop.IsCancellationRequested)
            {
                if (!_queue.TryDequeue(out var chunk))
                {
                    if (_finish)
                    {
                        FinishUtterance(loaded, stream, ignoreBefore, SpeechEndReason.Finished);
                        return;
                    }

                    try
                    {
                        _signal.Wait(_stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                Interlocked.Add(ref _queuedSamples, -chunk.Length);
                var floats = new float[chunk.Length];
                double sumOfSquares = 0;
                for (var i = 0; i < chunk.Length; i++)
                {
                    floats[i] = chunk[i] / 32768f;
                    sumOfSquares += (double)floats[i] * floats[i];
                }

                stream.AcceptWaveform(VoiceAudio.SampleRate, floats);
                samples += chunk.Length;
                while (recognizer.IsReady(stream))
                {
                    recognizer.Decode(stream);
                }

                var (text, lastWord) = TextOf(recognizer.GetResult(stream), ignoreBefore, loaded.TimestampScale);
                if (text != lastText)
                {
                    lastText = text;
                    Transcribed?.Invoke(this, new SpeechTranscriptEventArgs(text, null));
                }

                var seconds = samples / (double)VoiceAudio.SampleRate;
                var hasSpeech = text.Length > 0;
                var level = Math.Sqrt(sumOfSquares / chunk.Length);
                if (detector.Update(level, chunk.Length / (double)VoiceAudio.SampleRate, seconds, hasSpeech ? lastWord : null) && hasSpeech)
                {
                    // The speaker has stopped, but the recognizer writes the last words a little behind them: the rest comes out with the end of the audio.
                    FinishUtterance(loaded, stream, ignoreBefore, SpeechEndReason.Endpoint);
                    return;
                }

                if (!hasSpeech && seconds >= _options.InitialSilence.TotalSeconds)
                {
                    End(new SpeechTranscriptEventArgs("", SpeechEndReason.NoSpeech));
                    return;
                }

                if (seconds >= _options.MaxLength.TotalSeconds)
                {
                    End(new SpeechTranscriptEventArgs(text, SpeechEndReason.TooLong));
                    return;
                }
            }
        }

        // No more audio: some silence so that the last words come out of the recognizer's right context, then everything it has.
        private void FinishUtterance(LoadedRecognizer loaded, OnlineStream stream, float ignoreBefore, SpeechEndReason reason)
        {
            var recognizer = loaded.Recognizer;
            stream.AcceptWaveform(VoiceAudio.SampleRate, new float[VoiceAudio.SamplesIn(TailPadding)]);
            stream.InputFinished();
            while (recognizer.IsReady(stream))
            {
                recognizer.Decode(stream);
            }

            End(new SpeechTranscriptEventArgs(TextOf(recognizer.GetResult(stream), ignoreBefore, loaded.TimestampScale).Text, reason));
        }

        // The session's last word, raised once, and never after the session was disposed.
        private void End(SpeechTranscriptEventArgs last)
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _finalRaised, 1) != 0)
            {
                return;
            }

            _ended = true;
            Transcribed?.Invoke(this, last);
        }

        // The words of a result, without those that begin before <ignoreBefore> seconds, and when the last of them was said.
        private static (string Text, double? LastWord) TextOf(OnlineRecognizerResult result, float ignoreBefore, float scale)
        {
            var tokens = result.Tokens;
            var times = result.Timestamps;
            if (tokens is null || times is null || tokens.Length != times.Length || tokens.Length == 0)
            {
                return ((result.Text ?? "").Trim(), null);
            }

            if (ignoreBefore <= 0)
            {
                return ((result.Text ?? "").Trim(), times[^1] * scale);
            }

            var builder = new StringBuilder();
            double? last = null;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (times[i] * scale >= ignoreBefore)
                {
                    builder.Append(tokens[i]);
                    last = times[i] * scale;
                }
            }

            return (builder.ToString().Trim(), last);
        }
    }
}

/// <summary>A loaded recognizer and how to read the times it reports: a network that steps through the audio more coarsely than the runtime assumes reports every time too small by the same factor.</summary>
internal sealed class LoadedRecognizer(OnlineRecognizer recognizer, float timestampScale) : IDisposable
{
    /// <summary>The recognizer.</summary>
    public OnlineRecognizer Recognizer { get; } = recognizer;

    /// <summary>What a reported time is multiplied by to be seconds of audio.</summary>
    public float TimestampScale { get; } = timestampScale;

    /// <inheritdoc/>
    public void Dispose() => Recognizer.Dispose();
}
