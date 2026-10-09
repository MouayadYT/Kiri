using System.Runtime.InteropServices;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using SherpaOnnx;

namespace Assistant.Voice;

/// <summary>
/// The adapter for one of the three text-to-speech engines: KittenTTS Mini 0.8, Kokoro-82M and Piper (PROJECT_SPEC §4.2, step 125). The three are different
/// networks with different files. CPU is the default; the isolated voice host can run the same adapter with the CUDA provider. Which files an engine needs, and how it is configured, is
/// <see cref="Layout"/>; nothing else in the app knows.
/// </summary>
public sealed class SherpaTextToSpeechEngine : ITextToSpeechEngine
{
    private readonly TextToSpeechModel _model;
    private readonly VoiceModelFolders _folders;
    private readonly int _threads;
    private OfflineTts? _tts;
    private int _speaker;
    private readonly string _provider;
    private readonly string? _modelFolder;

    /// <summary>Creates the adapter for <paramref name="model"/>; nothing is read until <see cref="Load"/>.</summary>
    /// <param name="model">The engine.</param>
    /// <param name="folders">Where its files are.</param>
    /// <param name="threads">How many threads the engine may use.</param>
    public SherpaTextToSpeechEngine(TextToSpeechModel model, VoiceModelFolders folders, int threads, string provider = "cpu", string? modelFolder = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(folders);
        _model = model;
        _folders = folders;
        _threads = Math.Max(1, threads);
        _provider = provider is "cpu" or "cuda" ? provider : throw new ArgumentException("Unsupported speech provider.", nameof(provider));
        _modelFolder = modelFolder;
    }

    /// <inheritdoc/>
    public TextToSpeechModel Model => _model;

    /// <inheritdoc/>
    public int SampleRate { get; private set; }

    /// <inheritdoc/>
    public void Load()
    {
        if (_tts is not null)
        {
            return;
        }

        var folder = _modelFolder ?? _folders.Resolve(_model.Id);
        var (config, speaker) = Layout.Create(_model.Id, folder, _threads);
        config.Model.Provider = _provider;
        try
        {
            _tts = new OfflineTts(config);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, "The voice could not be loaded from its files.", exception);
        }

        // The Chinese v1.1 archive assigns speaker 3 to zf_001, not af_heart. Never silently speak with that voice.
        if (_model.Id == "kokoro-82m-onnx" && _tts.NumSpeakers is not (53 or 54))
        {
            _tts.Dispose();
            _tts = null;
            throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, "Update Kokoro in Settings → Voice to download the af_heart voice.");
        }

        _speaker = speaker;
        SampleRate = _tts.SampleRate;

        // The first sentence an engine makes is slower than the ones after it, as the runtime sets up its buffers; it is made here, silently, so the
        // first answer is not.
        try
        {
            Synthesize("Hello.", static _ => true, CancellationToken.None);
        }
        catch (VoiceEngineException)
        {
            // A voice that cannot say "Hello" is found out when it is asked to say something.
        }
    }

    /// <inheritdoc/>
    public void Synthesize(string text, SpeechAudioHandler onAudio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onAudio);
        var tts = _tts ?? throw new InvalidOperationException("The voice is not loaded.");
        cancellationToken.ThrowIfCancellationRequested();
        var stopped = false;
        var buffer = new float[16_384];
        var callback = new OfflineTtsCallback((samples, count) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                return 0;
            }

            if (count > buffer.Length)
            {
                buffer = new float[count];
            }

            Marshal.Copy(samples, buffer, 0, count);
            if (!onAudio(buffer.AsSpan(0, count)))
            {
                stopped = true;
                return 0;
            }

            return 1;
        });

        OfflineTtsGeneratedAudio? audio = null;
        try
        {
            audio = tts.GenerateWithCallback(text, 1.0f, _speaker, callback);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            throw new VoiceEngineException(VoiceEngineFailure.Failed, "The voice stopped while it was speaking.", exception);
        }
        finally
        {
            audio?.Dispose();
            GC.KeepAlive(callback);
        }

        if (stopped || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        var tts = _tts;
        _tts = null;
        tts?.Dispose();
    }

    /// <summary>How each of the three engines is set up from its folder.</summary>
    internal static class Layout
    {
        /// <summary>The runtime's configuration for engine <paramref name="engineId"/> from the files in <paramref name="folder"/>, and the voice to speak with.</summary>
        public static (OfflineTtsConfig Config, int Speaker) Create(string engineId, string folder, int threads)
        {
            var config = new OfflineTtsConfig { MaxNumSentences = 1 };
            config.Model.NumThreads = threads;

            // Never the graphics card: the language model needs it, and a voice this small is quick on the processor.
            config.Model.Provider = "cpu";
            var speaker = 0;
            switch (engineId)
            {
                case "kitten-tts-mini":
                    config.Model.Kitten.Model = Required(folder, "model.onnx");
                    config.Model.Kitten.Voices = Required(folder, "voices.bin");
                    config.Model.Kitten.Tokens = Required(folder, "tokens.txt");
                    config.Model.Kitten.DataDir = RequiredDirectory(folder, "espeak-ng-data");
                    break;

                case "kokoro-82m-onnx":
                    config.Model.Kokoro.Model = Required(folder, "model.onnx", "model.int8.onnx");
                    config.Model.Kokoro.Voices = Required(folder, "voices.bin");
                    config.Model.Kokoro.Tokens = Required(folder, "tokens.txt");
                    config.Model.Kokoro.DataDir = RequiredDirectory(folder, "espeak-ng-data");
                    config.Model.Kokoro.Lexicon = Required(folder, "lexicon-us-en.txt");
                    config.Model.Kokoro.Lang = "en-us";
                    if (Optional(folder, "dict") is { } dictionary)
                    {
                        config.Model.Kokoro.DictDir = dictionary;
                    }

                    // af_heart, the voice the model's own page recommends.
                    speaker = 3;
                    break;

                case "piper":
                    config.Model.Vits.Model = OnlyModel(folder);
                    config.Model.Vits.Tokens = Required(folder, "tokens.txt");
                    config.Model.Vits.DataDir = RequiredDirectory(folder, "espeak-ng-data");
                    break;

                default:
                    throw new VoiceEngineException(VoiceEngineFailure.NotInstalled, "There is no such voice.");
            }

            return (config, speaker);
        }

        // The first of <names> that is in the folder, ready for the runtime.
        private static string Required(string folder, params string[] names)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path))
                {
                    return NativePath.Prepare(path);
                }
            }

            throw Incomplete();
        }

        private static string RequiredDirectory(string folder, string name)
        {
            var path = Path.Combine(folder, name);
            return Directory.Exists(path) ? NativePath.Prepare(path) : throw Incomplete();
        }

        private static string? Optional(string folder, string name)
        {
            var path = Path.Combine(folder, name);
            return Directory.Exists(path) || File.Exists(path) ? NativePath.Prepare(path) : null;
        }

        // Piper's voices are named for the voice (en_US-lessac-medium.onnx), so the model is the one ONNX file in the folder.
        private static string OnlyModel(string folder)
        {
            var models = Directory.EnumerateFiles(folder, "*.onnx", SearchOption.TopDirectoryOnly).Take(2).ToArray();
            return models.Length == 1 ? NativePath.Prepare(models[0]) : throw Incomplete();
        }

        private static VoiceEngineException Incomplete() =>
            new(VoiceEngineFailure.NotInstalled, "This voice's files are incomplete. Put all of its files in the Assistant's voices folder, or reinstall the Assistant with it.");
    }
}
