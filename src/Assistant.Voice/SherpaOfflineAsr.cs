using Assistant.Core.Voice;
using SherpaOnnx;

namespace Assistant.Voice;

/// <summary>CPU recognition with the same Parakeet/SenseVoice/Moonshine model families offered by Handy.</summary>
public static class SherpaOfflineAsr
{
    public static Task PrepareAsync(string id, string folder, CancellationToken token) => Task.Run(() => { token.ThrowIfCancellationRequested(); using var engine = Load(id, folder); }, token);
    public static Task<string> RecognizeAsync(string id, string folder, short[] audio, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var engine = Load(id, folder); using var stream = engine.CreateStream();
        var samples = new float[audio.Length]; for (var i = 0; i < samples.Length; i++) samples[i] = audio[i] / 32768f;
        try { stream.AcceptWaveform(16000, samples); engine.Decode(stream); token.ThrowIfCancellationRequested(); return stream.Result.Text; }
        finally { Array.Clear(samples); }
    }, token);
    private static OfflineRecognizer Load(string id, string folder)
    {
        string FileOf(string pattern) => NativePath.Prepare(Directory.EnumerateFiles(folder, pattern).FirstOrDefault() ?? throw new VoiceEngineException(VoiceEngineFailure.NotInstalled, "Download this speech model first."));
        var config = new OfflineRecognizerConfig { DecodingMethod = "greedy_search" };
        config.FeatConfig.SampleRate = 16000; config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Provider = "cpu"; config.ModelConfig.Tokens = FileOf("tokens.txt");
        if (id == "asr-parakeet-v3")
        {
            config.ModelConfig.ModelType = "nemo_transducer";
            config.ModelConfig.Transducer.Encoder = FileOf("encoder.int8.onnx");
            config.ModelConfig.Transducer.Decoder = FileOf("decoder.int8.onnx");
            config.ModelConfig.Transducer.Joiner = FileOf("joiner.int8.onnx");
        }
        else if (id == "asr-sensevoice")
        {
            config.ModelConfig.SenseVoice.Model = FileOf("model.int8.onnx");
            config.ModelConfig.SenseVoice.Language = "auto"; config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        }
        else
        {
            config.ModelConfig.Moonshine.Preprocessor = FileOf("preprocess.onnx");
            config.ModelConfig.Moonshine.Encoder = FileOf("encode.int8.onnx");
            config.ModelConfig.Moonshine.UncachedDecoder = FileOf("uncached_decode.int8.onnx");
            config.ModelConfig.Moonshine.CachedDecoder = FileOf("cached_decode.int8.onnx");
        }
        return new OfflineRecognizer(config);
    }
}
