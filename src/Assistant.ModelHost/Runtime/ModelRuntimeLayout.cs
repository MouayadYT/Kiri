namespace Assistant.ModelHost.Runtime;

/// <summary>
/// Where the llama.cpp runtime lives in the installed app: the <c>llama.cpp</c> folder next to the host executable,
/// copied there from <c>third_party/llama.cpp</c> by the build.
/// </summary>
internal static class ModelRuntimeLayout
{
    /// <summary>The runtime's folder, in the host's own directory.</summary>
    public const string DirectoryName = "llama.cpp";

    /// <summary>The llama.cpp server, the executable the host runs as its engine.</summary>
    public const string ServerFileName = "llama-server.exe";

    /// <summary>
    /// ggml's CPU backends. ggml loads the one that fits the processor at run time, so they are in no import table, and
    /// at least one must be there.
    /// </summary>
    public const string CpuBackendPattern = "ggml-cpu*.dll";

    /// <summary>The runtime's folder in the installed app.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(AppContext.BaseDirectory, DirectoryName);
}
