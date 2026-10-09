using System.Runtime.InteropServices;
using System.Text;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.Voice;
using Microsoft.ML.OnnxRuntime;
using SherpaOnnx;

if (args.FirstOrDefault() == "--asr") return await AsrWorker.RunAsync(args);

// The CUDA native runtime stays in this process; the UI's CPU recognizer and wake-word engine keep their own runtime.
using var output = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8, leaveOpen: true);
var stage = "native-runtime";
try
{
    if (args.Length != 4 || TextToSpeechModels.All.All(model => model.Id != args[0]) || args.Skip(1).Any(path => !Path.IsPathFullyQualified(path))) return 2;
    var native = Path.Combine(args[2], "bin");
    var ort = NativeLibrary.Load(Path.Combine(native, "onnxruntime.dll"));
    NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) => name == "onnxruntime" ? ort : 0);
    NativeLibrary.SetDllImportResolver(typeof(OfflineTts).Assembly, (name, _, _) =>
    {
        var file = Path.Combine(native, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
        if (File.Exists(file)) return NativeLibrary.Load(file);
        var bundled = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(file));
        if (!File.Exists(bundled)) bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", Path.GetFileName(file));
        return NativeLibrary.Load(bundled);
    });
    stage = "providers";
    if (!OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider")) throw new InvalidOperationException("CUDA is unavailable.");
    // Refuse a broken/missing CUDA provider instead of silently measuring CPU performance.
    stage = "cuda-provider";
    using var probe = new SessionOptions(); probe.AppendExecutionProvider_CUDA(0);
    stage = "voice-model";
    var paths = new AppPaths(args[1]);
    using var engine = new SherpaTextToSpeechEngine(TextToSpeechModels.Find(args[0])!, new VoiceModelFolders(paths), 2, "cuda", args[3]);
    engine.Load(); output.Write(engine.SampleRate); output.Flush();
    stage = "synthesis";
    using var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8, leaveOpen: true);
    while (true)
    {
        int length;
        try { length = input.ReadInt32(); } catch (EndOfStreamException) { break; }
        if (length is <= 0 or > 65536) break;
        var bytes = input.ReadBytes(length); if (bytes.Length != length) break;
        var text = Encoding.UTF8.GetString(bytes);
        engine.Synthesize(text, samples =>
        {
            output.Write(samples.Length);
            output.Write(MemoryMarshal.AsBytes(samples));
            output.Flush(); return true;
        }, CancellationToken.None);
        output.Write(0); output.Flush();
    }
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"{stage}: {ex.GetType().Name}, code {ex.HResult}, inner {ex.InnerException?.GetType().Name}, inner code {ex.InnerException?.HResult}");
    // No text, paths or driver exception details cross the failure channel.
    try { output.Write(-1); output.Flush(); } catch (IOException) { }
    return 1;
}
