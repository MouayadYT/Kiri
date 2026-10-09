using System.Collections.Concurrent;
using System.Text;
using Assistant.Core.Storage;
using Assistant.Core.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace Assistant.Voice;

/// <summary>How the wake-word listener decides that it heard "Kiri".</summary>
public sealed record WakeWordTuning
{
    /// <summary>
    /// How the word is written for the listener's model, which reads word pieces: a line for each way of saying it, "pieces @name". "Kiri" is one word the
    /// model has never seen, so it is spelled in the pieces it knows.
    /// </summary>
    public IReadOnlyList<string> Phrases { get; init; } = ["▁K I RI @KIRI", "▁HE Y ▁K I RI @HEYKIRI"];

    /// <summary>How many ways of reading the audio the listener keeps while it listens: more finds the word more often, and costs a little more.</summary>
    public int MaxActivePaths { get; init; } = 4;

    /// <summary>The model's score for a word before it counts, from 0 to 1: lower hears more, and hears words that are not the word more often.</summary>
    public float Threshold { get; init; } = 0.03f;

    /// <summary>How much the word's pieces are favoured while the model listens: higher hears the word more readily.</summary>
    public float Boost { get; init; } = 3.0f;

    /// <summary>How long after the word was heard another one is ignored: one utterance is one wake-up.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Whether the word must begin a request: spoken after a pause, not in the middle of other speech. A name that sounds like "Kiri" comes up in
    /// ordinary talk ("and then Kerry said"), and a request to the Assistant starts after a breath.
    /// </summary>
    public bool RequireQuietBefore { get; init; } = true;

    /// <summary>How much louder the word must be than the half second of audio before it (the word's level over the level before), when <see cref="RequireQuietBefore"/>.</summary>
    public double QuietRatio { get; init; } = 2.0;

    /// <summary>
    /// How long the speech that ends with the word may be, when <see cref="RequireQuietBefore"/>: a request is called by "Kiri" or "Hey Kiri" or "Okay Kiri", which is a short burst of
    /// speech after a pause, so a word that ends such a burst begins a request even though it is not louder than the "hey" before it. A word at the end of a longer stretch of speech is
    /// part of ordinary talk.
    /// </summary>
    public TimeSpan ShortLeadIn { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long after it starts the listener first says how it is doing (see <see cref="ReportEvery"/>).</summary>
    public TimeSpan FirstReport { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often, once it has said so the first time, the listener says how it is doing: how long it listened, how much of that was louder than the
    /// room, and the loudest it heard. It is how a listener that never hears its word is told from a microphone that hears nothing. It says nothing about what was said.
    /// </summary>
    public TimeSpan ReportEvery { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The "Kiri" listener (PROJECT_SPEC §4.2, step 125): a tiny keyword-spotting model, a few million parameters, that is far cheaper than a speech recognizer
/// and runs all the while the wake word is on. It hears the word locally; its audio is a queue of a few seconds that is thrown away as it is used, and
/// nothing it hears is recorded or logged. A room that is silent costs next to nothing, because audio below the noise floor is not decoded.
/// </summary>
public sealed class SherpaWakeWordService : IWakeWordService
{
    private static readonly TimeSpan MaxBacklog = TimeSpan.FromSeconds(5);

    private readonly VoiceEngineHolder<KeywordSpotter> _holder;
    private readonly WakeWordTuning _tuning;
    private readonly ILogger _logger;

    /// <summary>Creates the service. Nothing is read until the wake word is turned on.</summary>
    /// <param name="folders">Where the model's files are.</param>
    /// <param name="paths">The app's folders; the keywords file is made in the cache folder.</param>
    /// <param name="tuning">How the word is heard, or the defaults.</param>
    /// <param name="logger">Where to log, or <see langword="null"/>.</param>
    public SherpaWakeWordService(VoiceModelFolders folders, AppPaths paths, WakeWordTuning? tuning = null, ILogger<SherpaWakeWordService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(paths);
        _tuning = tuning ?? new WakeWordTuning();
        _logger = logger ?? NullLogger<SherpaWakeWordService>.Instance;
        _holder = new VoiceEngineHolder<KeywordSpotter>(() => Load(folders, paths, _tuning), _logger, "wake-word");
        _holder.StatusChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public event EventHandler? StatusChanged;

    /// <inheritdoc/>
    public VoiceEngineStatus Status => _holder.Status;

    /// <inheritdoc/>
    public Task WarmUpAsync(CancellationToken cancellationToken = default) => _holder.WarmUpAsync().WaitAsync(cancellationToken);

    /// <inheritdoc/>
    public IWakeWordSession StartSession() => new Session(_holder, _tuning, _logger);

    /// <inheritdoc/>
    public void Unload() => _holder.Unload();

    /// <inheritdoc/>
    public void Dispose() => _holder.Dispose();

    private static KeywordSpotter Load(VoiceModelFolders folders, AppPaths paths, WakeWordTuning tuning)
    {
        var folder = folders.Resolve(VoiceModelFolders.WakeWordGroup);
        var encoder = First(folder, "encoder*.int8.onnx", "encoder*.onnx");
        var decoder = First(folder, "decoder*.int8.onnx", "decoder*.onnx");
        var joiner = First(folder, "joiner*.int8.onnx", "joiner*.onnx");
        var tokens = Path.Combine(folder, "tokens.txt");
        if (encoder is null || decoder is null || joiner is null || !File.Exists(tokens))
        {
            throw new VoiceEngineException(
                VoiceEngineFailure.NotInstalled,
                "The wake-word model's files are incomplete. Put all of its files in the Assistant's voices folder, or reinstall the Assistant with them.");
        }

        // The listener reads the words it should hear from a file, which is made here, in the cache, from what the tuning says.
        Directory.CreateDirectory(paths.CacheDirectory);
        var keywords = Path.Combine(paths.CacheDirectory, "wake-word-keywords.txt");
        File.WriteAllText(keywords, string.Join('\n', tuning.Phrases) + "\n", new UTF8Encoding(false));

        var config = new KeywordSpotterConfig
        {
            MaxActivePaths = tuning.MaxActivePaths,
            NumTrailingBlanks = 1,
            KeywordsScore = tuning.Boost,
            KeywordsThreshold = tuning.Threshold,
            KeywordsFile = NativePath.Prepare(keywords),
        };
        config.FeatConfig.SampleRate = VoiceAudio.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = NativePath.Prepare(encoder);
        config.ModelConfig.Transducer.Decoder = NativePath.Prepare(decoder);
        config.ModelConfig.Transducer.Joiner = NativePath.Prepare(joiner);
        config.ModelConfig.Tokens = NativePath.Prepare(tokens);
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        try
        {
            return new KeywordSpotter(config);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, "The wake-word model could not be loaded from its files.", exception);
        }
    }

    private static string? First(string folder, params string[] patterns)
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

    // One listener, on a thread of its own.
    private sealed class Session : IWakeWordSession
    {
        // The audio's level is kept in blocks of about 20 ms: enough to tell a pause from speech.
        private const int LevelBlock = VoiceAudio.SampleRate / 50;

        private readonly VoiceEngineHolder<KeywordSpotter> _holder;
        private readonly WakeWordTuning _tuning;
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<short[]> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private long _queuedSamples;
        private int _resetRequested;
        private int _disposed;

        public Session(VoiceEngineHolder<KeywordSpotter> holder, WakeWordTuning tuning, ILogger logger)
        {
            _holder = holder;
            _tuning = tuning;
            _logger = logger;
            _thread = new Thread(Run) { IsBackground = true, Name = "Wake word", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public event EventHandler<WakeWordDetectedEventArgs>? Detected;

        public void Push(ReadOnlySpan<short> samples)
        {
            if (_disposed != 0 || samples.IsEmpty)
            {
                return;
            }

            _queue.Enqueue(samples.ToArray());
            var queued = Interlocked.Add(ref _queuedSamples, samples.Length);
            while (queued > VoiceAudio.SamplesIn(MaxBacklog) && _queue.TryDequeue(out var dropped))
            {
                queued = Interlocked.Add(ref _queuedSamples, -dropped.Length);
            }

            _signal.Release();
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _resetRequested, 1);
            _signal.Release();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

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
            KeywordSpotter? spotter = null;
            OnlineStream? stream = null;
            try
            {
                spotter = _holder.Acquire(_stop.Token);
                if (spotter is null)
                {
                    return;
                }

                stream = spotter.CreateStream();
                Listen(spotter, stream);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                VoiceLog.Failed(_logger, "wake-word", exception.GetType().Name);
            }
            finally
            {
                stream?.Dispose();
                if (spotter is not null)
                {
                    _holder.Release();
                }
            }
        }

        private void Listen(KeywordSpotter spotter, OnlineStream stream)
        {
            var levels = new LevelHistory(LevelBlock);
            var noise = new NoiseFloor();
            long samples = 0;
            long mutedUntil = 0;

            // What the listener says of itself now and then: how much it heard, how much of it was above the room's noise, and the loudest.
            long loud = 0;
            double loudest = 0;
            long nextReport = VoiceAudio.SamplesIn(_tuning.FirstReport);
            var quietRun = 0;
            var cooldown = VoiceAudio.SamplesIn(_tuning.Cooldown);
            var preRoll = new AudioRingBuffer(VoiceAudio.SamplesIn(TimeSpan.FromMilliseconds(500)));
            while (!_stop.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
                {
                    spotter.Reset(stream);
                    levels.Clear();
                    quietRun = 0;
                }

                if (!_queue.TryDequeue(out var chunk))
                {
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
                var level = Rms(chunk);
                levels.Add(chunk);
                samples += chunk.Length;
                var floor = noise.Update(level);

                // Below the noise floor nothing is being said: nothing is decoded, after a little silence has gone by to let a word end.
                var gate = (floor * 3) + 0.002;
                loudest = Math.Max(loudest, level);
                if (level >= gate)
                {
                    loud += chunk.Length;
                }

                if (samples >= nextReport)
                {
                    VoiceLog.WakeWordListening(
                        _logger, (long)VoiceAudio.DurationOf(samples).TotalSeconds, (long)VoiceAudio.DurationOf(loud).TotalSeconds, (long)Math.Round(loudest * 100));
                    nextReport = samples + VoiceAudio.SamplesIn(_tuning.ReportEvery);
                    (loud, loudest) = (0, 0);
                }

                if (level < gate)
                {
                    quietRun += chunk.Length;
                    if (quietRun > VoiceAudio.SamplesIn(TimeSpan.FromMilliseconds(700)))
                    {
                        preRoll.Write(chunk);
                        continue;
                    }
                }
                else
                {
                    if (quietRun > VoiceAudio.SamplesIn(TimeSpan.FromMilliseconds(700)))
                    {
                        // Speech begins after a gap: the half second just before it is heard too, for the sound that rises into the first word.
                        Feed(stream, preRoll.Last(preRoll.Count));
                        preRoll.Clear();
                    }

                    quietRun = 0;
                }

                Feed(stream, chunk);
                while (spotter.IsReady(stream))
                {
                    spotter.Decode(stream);
                    var keyword = spotter.GetResult(stream).Keyword;
                    if (keyword.Length == 0)
                    {
                        continue;
                    }

                    spotter.Reset(stream);
                    if (samples < mutedUntil)
                    {
                        VoiceLog.WakeWordIgnored(_logger, "again too soon");
                        continue;
                    }

                    if (_tuning.RequireQuietBefore && !levels.WordBeginsAfterAPause(_tuning.QuietRatio) && levels.SpeechRun(gate) > _tuning.ShortLeadIn)
                    {
                        VoiceLog.WakeWordIgnored(_logger, "in the middle of speech");
                        continue;
                    }

                    mutedUntil = samples + cooldown;
                    VoiceLog.WakeWordHeard(_logger);
                    Detected?.Invoke(this, new WakeWordDetectedEventArgs(VoiceAudio.DurationOf(samples)));
                }
            }
        }

        private static void Feed(OnlineStream stream, short[] samples)
        {
            var floats = new float[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                floats[i] = samples[i] / 32768f;
            }

            stream.AcceptWaveform(VoiceAudio.SampleRate, floats);
        }

        private static double Rms(short[] samples)
        {
            double sum = 0;
            foreach (var sample in samples)
            {
                sum += (double)sample * sample;
            }

            return Math.Sqrt(sum / samples.Length) / 32768;
        }
    }

    /// <summary>The level of the last few seconds in blocks of a fixed 20 ms, however the audio arrives, to tell whether a word came out of a pause.</summary>
    internal sealed class LevelHistory
    {
        // About 3 seconds of 20 ms blocks.
        private const int MaxBlocks = 150;

        private readonly int _block;
        private readonly Queue<double> _levels = new();
        private double _sumOfSquares;
        private int _inBlock;

        public LevelHistory(int block) => _block = block;

        public void Clear()
        {
            _levels.Clear();
            _sumOfSquares = 0;
            _inBlock = 0;
        }

        public void Add(short[] samples)
        {
            foreach (var sample in samples)
            {
                _sumOfSquares += (double)sample * sample;
                if (++_inBlock < _block)
                {
                    continue;
                }

                _levels.Enqueue(Math.Sqrt(_sumOfSquares / _block) / 32768);
                if (_levels.Count > MaxBlocks)
                {
                    _levels.Dequeue();
                }

                _sumOfSquares = 0;
                _inBlock = 0;
            }
        }

        /// <summary>
        /// Whether the word that has just been heard began after a pause: the level of the last 0.8 seconds, which holds the word, is at least
        /// <paramref name="ratio"/> times that of the 0.6 seconds before it. With less history than that, the word is taken as the first thing said.
        /// </summary>
        public bool WordBeginsAfterAPause(double ratio)
        {
            var levels = _levels.ToArray();
            const int wordBlocks = 40;
            const int beforeBlocks = 30;
            if (levels.Length <= wordBlocks + 5)
            {
                return true;
            }

            var word = Peak(levels, levels.Length - wordBlocks, levels.Length);
            var beforeStart = Math.Max(0, levels.Length - wordBlocks - beforeBlocks);
            var before = Peak(levels, beforeStart, levels.Length - wordBlocks);
            return before <= 0.0 || word >= before * ratio;
        }

        /// <summary>
        /// How long the speech that ends the history has lasted: from the last block that is at or above <paramref name="gate"/> back to the last pause of a quarter of a second or more
        /// (a dip of less than that, between two words, is still speech). The speech reaching back to the start of the history is as long as the history.
        /// </summary>
        public TimeSpan SpeechRun(double gate)
        {
            const int pauseBlocks = 12;
            var levels = _levels.ToArray();
            var end = levels.Length - 1;
            while (end >= 0 && levels[end] < gate)
            {
                end--;
            }

            if (end < 0)
            {
                return TimeSpan.Zero;
            }

            var start = end;
            var quiet = 0;
            for (var i = end; i >= 0; i--)
            {
                if (levels[i] >= gate)
                {
                    start = i;
                    quiet = 0;
                }
                else if (++quiet >= pauseBlocks)
                {
                    break;
                }
            }

            // Reaching the start of the history means that the speech began before it: it is at least as long as the history, and counted so.
            var length = start == 0 && levels.Length >= MaxBlocks ? levels.Length : end - start + 1;
            return TimeSpan.FromMilliseconds(length * 20);
        }

        // The level that most of the loud blocks in the span reach: the mean of the top fifth, which a click or a breath does not move much.
        private static double Peak(double[] levels, int from, int to)
        {
            var span = levels[from..to];
            if (span.Length == 0)
            {
                return 0;
            }

            Array.Sort(span);
            var top = span[Math.Max(0, span.Length - Math.Max(1, span.Length / 5))..];
            return top.Average();
        }
    }
}
