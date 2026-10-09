using System.Text;

namespace Assistant.ModelHost.Runtime;

/// <summary>The bundled llama.cpp runtime, found and checked by an <see cref="IModelRuntimeLocator"/>.</summary>
/// <param name="Directory">The runtime's folder, which holds the server and every native file it loads.</param>
/// <param name="ServerPath">The server executable, <see cref="ModelRuntimeLayout.ServerFileName"/>.</param>
internal sealed record ModelRuntime(string Directory, string ServerPath)
{
    // Keeps the install path (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder) => false;
}
