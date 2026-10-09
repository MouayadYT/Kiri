using System.Runtime.InteropServices;
using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

internal static class AsrWorker
{
    internal static async Task<int> RunAsync(string[] args)
    {
        using var output = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8, true);
        try
        {
            if (args.Length != 4 || !Path.IsPathFullyQualified(args[1]) || args[2] != "whisper" || args[3] is not ("cpu" or "vulkan")) return 2;
            var gpu = args[3] == "vulkan";
            // Each request has its own process: switching devices cannot reuse a previously loaded CPU DLL.
            RuntimeOptions.RuntimeLibraryOrder = [gpu ? RuntimeLibrary.Vulkan : RuntimeLibrary.Cpu];
            using var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8, true);
            var length = input.ReadInt32();
            if (length is < 0 or > 720000) return 2;
            var bytes = input.ReadBytes(length * 2); if (bytes.Length != length * 2) return 2;
            using var factory = WhisperFactory.FromPath(args[1], new()
            {
                UseGpu = gpu,
            });
            if (gpu && !HasVulkanDevice()) throw new InvalidOperationException("No Vulkan compute device.");
            var builder = factory.CreateBuilder().WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
            builder.WithLanguageDetection();
            using var processor = builder.Build();
            var samples = new float[length];
            for (var i = 0; i < length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            var text = new StringBuilder();
            if (length > 0) await foreach (var segment in processor.ProcessAsync(samples)) text.Append(segment.Text);
            Array.Clear(bytes); Array.Clear(samples);
            var result = Encoding.UTF8.GetBytes(text.ToString().Trim());
            output.Write(result.Length); output.Write(result); output.Flush();
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("asr: " + ex.GetType().Name);
            output.Write(-1); output.Flush(); return 1;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceCount();
    private static bool HasVulkanDevice()
    {
        // Use the loaded backend's real device list, rather than accepting a CPU fallback after a GPU choice.
        var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", "vulkan", "win-x64", "ggml-vulkan-whisper.dll"));
        try { return Marshal.GetDelegateForFunctionPointer<DeviceCount>(NativeLibrary.GetExport(library, "ggml_backend_vk_get_device_count"))() > 0; }
        finally { NativeLibrary.Free(library); }
    }
}
