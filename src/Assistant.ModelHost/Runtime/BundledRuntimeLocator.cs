using System.Reflection.PortableExecutable;
using Assistant.Core.ModelHosting;
using Microsoft.Extensions.Logging;

namespace Assistant.ModelHost.Runtime;

/// <summary>
/// Finds the llama.cpp runtime in the installed app (<see cref="ModelRuntimeLayout"/>) and checks that Windows can
/// load it: the server executable, every DLL it imports, and theirs in turn, each built for x64, plus at least one of
/// ggml's CPU backends.
/// </summary>
/// <remarks>
/// A DLL resolves from the runtime's folder first and from the system folder second, as the Windows loader resolves it
/// for an executable in that folder; API sets always resolve. Only the runtime's own DLLs are followed further. The
/// check reads headers only, so it takes milliseconds and can run on every start. The status is logged when it changes,
/// by state and count, never by path.
/// </remarks>
internal sealed class BundledRuntimeLocator(ModelRuntimeOptions options, ILogger<BundledRuntimeLocator> logger)
    : IModelRuntimeLocator
{
    // The Visual C++ runtime comes with its redistributable, not with Windows or the runtime package.
    private static readonly string[] VisualCppRuntimePrefixes = ["msvcp140", "vcruntime140", "concrt140", "vccorlib140"];

    private int _loggedState = -1;

    public ModelRuntimeStatus Locate()
    {
        var status = Check();
        if (Interlocked.Exchange(ref _loggedState, (int)status.State) != (int)status.State)
        {
            if (status.IsReady)
            {
                RuntimeLog.Ready(logger);
            }
            else
            {
                RuntimeLog.Unavailable(logger, status.State, status.MissingFiles.Count);
            }
        }

        return status;
    }

    private ModelRuntimeStatus Check()
    {
        var directory = options.RuntimeDirectory;
        var server = Path.Combine(directory, ModelRuntimeLayout.ServerFileName);
        if (!File.Exists(server))
        {
            return ModelRuntimeStatus.Unavailable(ModelRuntimeState.NotInstalled);
        }

        try
        {
            var backends = Directory.GetFiles(directory, ModelRuntimeLayout.CpuBackendPattern);
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (backends.Length == 0)
            {
                missing.Add(ModelRuntimeLayout.CpuBackendPattern);
            }

            var pending = new Queue<string>([server, .. backends]);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in pending)
            {
                seen.Add(Path.GetFileName(file));
            }

            while (pending.TryDequeue(out var file))
            {
                var image = NativeImage.Read(file);
                if (image.Machine != Machine.Amd64)
                {
                    return ModelRuntimeStatus.Unavailable(ModelRuntimeState.Incompatible);
                }

                foreach (var import in image.Imports)
                {
                    if (IsApiSet(import))
                    {
                        continue;
                    }

                    var bundled = Path.Combine(directory, import);
                    if (File.Exists(bundled))
                    {
                        if (seen.Add(import))
                        {
                            pending.Enqueue(bundled);
                        }
                    }
                    else if (!File.Exists(Path.Combine(options.SystemDirectory, import)))
                    {
                        missing.Add(import);
                    }
                }
            }

            if (missing.Any(name => !IsVisualCppRuntime(name)))
            {
                return ModelRuntimeStatus.Unavailable(
                    ModelRuntimeState.Incomplete, missing.Where(name => !IsVisualCppRuntime(name)).ToArray());
            }

            return missing.Count > 0
                ? ModelRuntimeStatus.Unavailable(ModelRuntimeState.MissingSystemComponent, missing.ToArray())
                : ModelRuntimeStatus.Ready(new ModelRuntime(directory, server));
        }
        catch (BadImageFormatException)
        {
            return ModelRuntimeStatus.Unavailable(ModelRuntimeState.Incompatible);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Also a file that went away while it was checked.
            return ModelRuntimeStatus.Unavailable(ModelRuntimeState.Inaccessible);
        }
    }

    // api-ms-win-* and ext-ms-* name API sets, which Windows maps to its own DLLs; no file has those names.
    private static bool IsApiSet(string name) =>
        name.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);

    private static bool IsVisualCppRuntime(string name) =>
        VisualCppRuntimePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
