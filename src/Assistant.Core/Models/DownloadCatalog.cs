namespace Assistant.Core.Models;

public enum DownloadKind { Language, Voice, Runtime, SpeechRecognition, WakeWord }

public sealed record ModelDownloadFile(string Name, string Url, long Bytes, string Sha256);

public sealed record DownloadableModel(
    string Id, DownloadKind Kind, string Name, string Detail, string RamEstimate,
    IReadOnlyList<ModelDownloadFile> Files, bool Recommended = false, string? ArchiveRoot = null, double ParametersBillions = 0)
{
    /// <summary>
    /// About how many bytes of memory a language model takes while it is loaded with a context window of <paramref name="contextTokens"/> tokens: its files
    /// (the weights and, when it has one, the image projector), the context's cache, which grows with the window, and the engine's own buffers. Worked out
    /// from the files' real sizes; an estimate all the same, as <see cref="Assistant.Core.ModelProfiles.ContextAdvisor"/>'s are.
    /// </summary>
    public long EstimateMemoryBytes(int contextTokens) =>
        Assistant.Core.ModelProfiles.ContextAdvisor.EstimateMemoryBytes(DownloadBytes, ParametersBillions, contextTokens);

    /// <summary>
    /// What a language model needs in words, for the window an ordinary conversation is given and for the one a conversation with files is
    /// ("~4.9 GB RAM to 8.0 GB when reading files"); for anything else, <see cref="RamEstimate"/> as it is written.
    /// </summary>
    public string MemoryText => Kind != DownloadKind.Language || ParametersBillions <= 0
        ? RamEstimate
        : $"~{Gigabytes(EstimateMemoryBytes(Assistant.Core.Contracts.ModelFiles.DefaultContextLength))} GB RAM to "
          + $"{Gigabytes(EstimateMemoryBytes(Assistant.Core.Contracts.ModelFiles.DocumentContextLength))} GB when reading files";

    private static string Gigabytes(long bytes) =>
        (bytes / (1024d * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    public long DownloadBytes => Files.Sum(file => file.Bytes);

    /// <summary>The vision projector that makes a language model read pictures, when it has one.</summary>
    public ModelDownloadFile? Projector => Kind == DownloadKind.Language ? Files.FirstOrDefault(file => file.Name == DownloadCatalog.ProjectorFileName) : null;

    /// <summary>
    /// About how many bytes of memory reading pictures adds while the model is loaded: the projector and what it works in. Measured with the 4B
    /// model on a processor, the engine held about twice the projector's own size more with it than without.
    /// </summary>
    public long VisionMemoryBytes => (Projector?.Bytes ?? 0) * 2;
    public string DownloadSize => DownloadBytes >= 1_000_000_000
        ? $"{DownloadBytes / 1_000_000_000d:0.00} GB" : $"{DownloadBytes / 1_000_000d:0} MB";
}

/// <summary>Pinned, checksum-verified files; only the three requested language-model quantizations are offered.</summary>
public static class DownloadCatalog
{
    private const string FourRepo = "mradermacher/Qwen3.5-4B-Claude-4.6-OS-Auto-Variable-HERETIC-UNCENSORED-THINKING-i1-GGUF";
    private const string NineRepo = "mradermacher/Qwen3.5-9B-Claude-4.6-HighIQ-INSTRUCT-HERETIC-UNCENSORED-GGUF";
    private const string FourRevision = "84219c4a042ca64c0cb9cad3931d922a92f6d1c7";
    private const string NineRevision = "c9ee985e9bf0829c4e9ef06e373699dacacb8496";
    private const string FourPrefix = "Qwen3.5-4B-Claude-4.6-OS-Auto-Variable-HERETIC-UNCENSORED-THINKING.i1-";
    private const string NinePrefix = "Qwen3.5-9B-Claude-4.6-HighIQ-INSTRUCT-HERETIC-UNCENSORED.";

    // The 4B model reads pictures like the 9B does, with a vision projector beside it. The repository its weights come from (the "i1" one) does not
    // carry the projector; the same author's repository of the same model's other quantizations does, and one projector serves them all.
    private const string FourProjectorRepo = "mradermacher/Qwen3.5-4B-Claude-4.6-OS-Auto-Variable-HERETIC-UNCENSORED-THINKING-GGUF";
    private const string FourProjectorRevision = "a5b30722f04a63b2cd5ae2d3b1b8c96a240d1dbf";
    private static readonly ModelDownloadFile FourProjector = Hf(
        FourProjectorRepo, FourProjectorRevision, "Qwen3.5-4B-Claude-4.6-OS-Auto-Variable-HERETIC-UNCENSORED-THINKING.mmproj-Q8_0.gguf", ProjectorFileName,
        366895264, "3ae87bf38ec271d3f9f000b4cf6ed1a16d01940d82f71ed63dcbd63a5ed35add");

    /// <summary>What a language model's weights are called in its folder.</summary>
    public const string ModelFileName = "model.gguf";

    /// <summary>What a language model's vision projector is called in its folder: with it beside the weights, the model reads pictures.</summary>
    public const string ProjectorFileName = "projector.gguf";

    public static IReadOnlyList<DownloadableModel> LanguageModels { get; } =
    [
        new("qwen35-4b-q5km", DownloadKind.Language, "Qwen 3.5 · 4B", "Q5_K_M · balanced quality and memory, includes vision projector", "3.5–5 GB RAM",
            [Hf(FourRepo, FourRevision, FourPrefix + "Q5_K_M.gguf", "model.gguf", 3108760000, "ca85d65a4155217d1b96a91212da3eb4dcf216bb9b78d02c8160d01d99a3f45c"), FourProjector], true, ParametersBillions: 4),
        new("qwen35-4b-iq3xs", DownloadKind.Language, "Qwen 3.5 · 4B", "IQ3_XS · smallest download, includes vision projector", "2.5–4 GB RAM",
            [Hf(FourRepo, FourRevision, FourPrefix + "IQ3_XS.gguf", "model.gguf", 2010735040, "4bb24ddf65e67e9bb04b2aa7e15c5f8a8374cb48095fae5cde7d392ef8a0d2be"), FourProjector], ParametersBillions: 4),
        new("qwen35-9b-q4ks", DownloadKind.Language, "Qwen 3.5 · 9B", "Q4_K_S · larger model, includes vision projector", "6.5–8 GB RAM",
            [Hf(NineRepo, NineRevision, NinePrefix + "Q4_K_S.gguf", "model.gguf", 5340620832, "4e548510cb0682229d8b005e52ced93f84713ee4e1c5b0c28e5edb05be51558a"),
             Hf(NineRepo, NineRevision, NinePrefix + "mmproj-Q8_0.gguf", "projector.gguf", 624230368, "bd9248b981181a93bac6867540d2c75596f406f1dec8b7de94da751bf3c927a9")], ParametersBillions: 9),
    ];

    public static IReadOnlyList<DownloadableModel> Voices { get; } =
    [
        Voice("kokoro-82m-onnx", "Kokoro", "82M · American English · af_heart", "0.5–1 GB RAM", "kokoro-int8-multi-lang-v1_0", 132303094, "4c3052abaa60943a341f193888cf6abd68787dae6ab8ae5c925a706caa247e4e"),
        Voice("kitten-tts-mini", "KittenTTS Mini", "80M · compact English voice", "0.3–0.6 GB RAM", "kitten-mini-en-v0_8", 67547594, "518f9b130320f690d5b5476df77bde4215fca67773cda16710318e5081234b9d"),
        Voice("piper", "Piper", "Lessac medium · fast English voice", "0.2–0.4 GB RAM", "vits-piper-en_US-lessac-medium", 67230653, "9e3febfacf0abf4270172d2958bcec246032b7e88efc2720840cc80c93de334e"),
    ];

    public static DownloadableModel Recognizer { get; } = SpeechModel("speech-recognition", DownloadKind.SpeechRecognition,
        "English speech recognizer", "Zipformer · streaming English recognition · CPU", "0.5–1 GB RAM", "asr-models", "sherpa-onnx-streaming-zipformer-en-2023-06-26", 310414022, "639e25b578e9e997131402199419c13a941f8e4e198e2da1ce57dbf5cf401282");
    public static DownloadableModel WakeWord { get; } = SpeechModel("wake-word", DownloadKind.WakeWord,
        "Kiri wake-word listener", "3.3M · listens for Kiri or Hey Kiri · CPU", "50–100 MB RAM", "kws-models", "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01", 17626723, "f170013b4716e41b62b9bfd809687c207cef798ef9bc6534d524e17af9b6561a");
    public static IReadOnlyList<DownloadableModel> SpeechRecognizers { get; } =
    [
        SpeechModel("asr-parakeet-v3", DownloadKind.SpeechRecognition, "Parakeet V3", "0.6B · INT8 · 25 European languages · fast recognition · CPU", "1–2 GB RAM", "asr-models", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", 487170055, "5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf") with { Recommended = true },
        SpeechModel("asr-sensevoice", DownloadKind.SpeechRecognition, "SenseVoice", "INT8 · Chinese, English, Japanese, Korean and Cantonese · CPU", "0.4–0.8 GB RAM", "asr-models", "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17", 163002883, "7d1efa2138a65b0b488df37f8b89e3d91a60676e416f515b952358d83dfd347e"),
        SpeechModel("asr-moonshine-tiny", DownloadKind.SpeechRecognition, "Moonshine Tiny", "INT8 ONNX · compact English recognition · CPU", "0.3–0.6 GB RAM", "asr-models", "sherpa-onnx-moonshine-tiny-en-int8", 107600538, "d5fe6ec4334fef36255b2a4010412cad4c007e33103fec62fb5d17cad88086f2"),
        AsrWhisper("small", "Whisper Small", "99+ languages · faster, lighter recognition", "0.8–1.5 GB RAM", "ggml-small.bin", 487601967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        AsrWhisper("medium", "Whisper Medium", "Q4_1 · 99+ languages · improved accuracy", "1.5–3 GB RAM", "whisper-medium-q4_1.bin", 491852915, "79283fc1f9fe12ca3248543fbd54b73292164d8df5a16e095e2bceeaaabddf57"),
        AsrWhisper("turbo", "Whisper Turbo", "Large V3 Turbo · 99+ languages · accuracy and speed", "2.5–4 GB RAM", "ggml-large-v3-turbo.bin", 1624555275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        AsrWhisper("large", "Whisper Large", "V3 · Q5_0 · 99+ languages · highest accuracy, slower", "2.5–4.5 GB RAM", "ggml-large-v3-q5_0.bin", 1081140203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1"),
        Recognizer,
    ];
    public static IReadOnlyList<DownloadableModel> All { get; } = [.. LanguageModels, .. Voices, .. SpeechRecognizers, WakeWord];
    private static DownloadableModel AsrWhisper(string id, string name, string detail, string ram, string file, long bytes, string hash) =>
        new("asr-whisper-" + id, DownloadKind.SpeechRecognition, name, detail, ram,
            [new("model.bin", "https://blob.handy.computer/" + file, bytes, hash)]);
    private const string CudaRoot = "sherpa-onnx-v1.13.8-cuda-12.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda";
    public static DownloadableModel CudaVoiceRuntime { get; } = new("voice-cuda12", DownloadKind.Runtime, "NVIDIA voice runtime", "CUDA 12 · cuDNN 9", "",
        [new("voice.tar.bz2", $"https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/{CudaRoot}.tar.bz2", 595017373, "066c5b54dbafaa1388001a9c9837ac1374dbba6d6678f193ca06aa0d8e94d8c3")], ArchiveRoot: CudaRoot);
    public static IReadOnlyList<DownloadableModel> CudaDependencies { get; } =
    [
        Nvidia("cuda-cudart", "CUDA runtime", "cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.9.79-archive.zip", 3521238, "179e9c43b0735ffe67207b3da556eb5a0c50f3047961882b7657d3b822d34ef8"),
        Nvidia("cuda-cublas", "CUDA BLAS", "cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.9.1.4-archive.zip", 549755186, "d534d98b0b453a98914dbf3adf47d7e84b55037abf02f87466439e1dcef581ed"),
        Nvidia("cuda-cufft", "CUDA FFT", "cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-11.4.1.4-archive.zip", 198361265, "f26f80bb9abff3269c548e1559e8c2b4ba58ccb8acc6095bbc6404fc962d4b80"),
        Nvidia("cuda-nvrtc", "CUDA compiler", "cuda/redist/cuda_nvrtc/windows-x86_64/cuda_nvrtc-windows-x86_64-12.9.86-archive.zip", 314608748, "1aa0644fa53c8ca34cdc73db17bcc73530557bdd3f582c7bfdbd7916c8b48f65"),
        Nvidia("cuda-cudnn", "cuDNN", "cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.10.2.21_cuda12-archive.zip", 683336095, "c1a4567d822ebda7373fa1f19255dff4942302de741f830160b6c7d1fb31af23"),
    ];
    public static IReadOnlyList<DownloadableModel> CudaPackages { get; } = [CudaVoiceRuntime, .. CudaDependencies];
    private static DownloadableModel Nvidia(string id, string name, string path, long bytes, string hash) => new(id, DownloadKind.Runtime, name, "NVIDIA redistributable", "",
        [new("runtime.zip", "https://developer.download.nvidia.com/compute/" + path, bytes, hash)], ArchiveRoot: "@zip");
    public static DownloadableModel? Find(string id) => All.FirstOrDefault(model => model.Id == id);

    private static ModelDownloadFile Hf(string repo, string revision, string file, string target, long bytes, string hash) =>
        new(target, $"https://huggingface.co/{repo}/resolve/{revision}/{Uri.EscapeDataString(file)}?download=true", bytes, hash);

    private static DownloadableModel Voice(string id, string name, string detail, string ram, string root, long size, string hash) =>
        new(id, DownloadKind.Voice, name, detail, ram,
            [new("voice.tar.bz2", $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{root}.tar.bz2", size, hash)], ArchiveRoot: root);

    private static DownloadableModel SpeechModel(string id, DownloadKind kind, string name, string detail, string ram, string release, string root, long bytes, string hash) =>
        new(id, kind, name, detail, ram, [new("voice.tar.bz2", $"https://github.com/k2-fsa/sherpa-onnx/releases/download/{release}/{root}.tar.bz2", bytes, hash)], ArchiveRoot: root);
}
