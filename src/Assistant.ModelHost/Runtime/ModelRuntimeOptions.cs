using System.Text;

namespace Assistant.ModelHost.Runtime;

/// <summary>Where <see cref="BundledRuntimeLocator"/> looks for the runtime, and for the Windows files it needs.</summary>
/// <param name="RuntimeDirectory">The runtime's folder, <see cref="ModelRuntimeLayout.DefaultDirectory"/> in the app.</param>
internal sealed record ModelRuntimeOptions(string RuntimeDirectory)
{
    /// <summary>
    /// Where the loader finds the DLLs that come with Windows, or with its redistributables, rather than with the runtime.
    /// </summary>
    public string SystemDirectory { get; init; } = Environment.SystemDirectory;

    // Keeps the install path (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder) => false;
}
