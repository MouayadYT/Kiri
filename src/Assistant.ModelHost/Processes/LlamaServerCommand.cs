using System.Diagnostics;
using System.Globalization;
using System.Text;
using Assistant.Core.Contracts;
using Assistant.ModelHost.Runtime;

namespace Assistant.ModelHost.Processes;

/// <summary>
/// How the llama.cpp server is launched: hidden, with no console window; offline; listening only on a UNIX socket in
/// the user's own profile, never on TCP (PROJECT_SPEC §3.4); and logging at its usual level, which never includes
/// prompts or output. Its settings come only from this command line.
/// </summary>
internal static class LlamaServerCommand
{
    // llama.cpp reads a setting for nearly every option from LLAMA_ARG_* variables, a Hugging Face token and cache from
    // HF_*, and backend switches from GGML_*. None of them may reach the engine from the user's environment: one of them
    // could make it listen on the network, download, or log prompts.
    private static readonly string[] ScrubbedVariablePrefixes = ["LLAMA_", "HF_", "GGML_"];

    /// <summary>The server's arguments, in order.</summary>
    public static IReadOnlyList<string> Arguments(ModelProcessLaunch launch, string socketPath)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        List<string> arguments =
        [
            "--model", launch.ModelPath,

            // A host ending in .sock is a UNIX socket; the server then opens no TCP port.
            "--host", socketPath,
            "--offline",
            "--no-ui",

            // The slots endpoint would show the prompts being processed.
            "--no-slots",

            // Level 3 (info) is what LlamaServerOutput reads; higher levels would log prompts.
            "--log-verbosity", "3",
            "--log-colors", "off",

            // One slot: the host answers one generation at a time, so the slot has the whole context, which is
            // what the engine then reports as the model's context length.
            "--parallel", "1",
            "--ctx-size", (launch.ContextLength ?? ModelProcessLaunch.DefaultContextLength).ToString(CultureInfo.InvariantCulture),
        ];

        if (launch.Devices is { } devices)
        {
            // An empty list keeps the model off every device: the CPU alone runs it.
            arguments.AddRange(["--device", devices.Count == 0 ? "none" : string.Join(',', devices)]);
        }

        if (launch.ProjectorPath is not null)
        {
            arguments.AddRange(["--mmproj", launch.ProjectorPath]);
        }
        else
        {
            // Only a projector the launch names is loaded, never one the engine finds beside the model.
            arguments.Add("--no-mmproj-auto");
        }

        if (launch.ChatTemplatePath is not null)
        {
            arguments.AddRange(["--chat-template-file", launch.ChatTemplatePath]);
        }

        // The profile's options come last and only from the allow-list, which has none of the options set above.
        if (EngineArguments.Validate(launch.RuntimeArguments) is { } problem)
        {
            throw new ArgumentException(problem, nameof(launch));
        }

        arguments.AddRange(launch.RuntimeArguments);
        return arguments;
    }

    /// <summary>The start info for one launch of <paramref name="runtime"/>'s server.</summary>
    public static ProcessStartInfo Create(ModelRuntime runtime, ModelProcessLaunch launch, string socketPath)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var startInfo = new ProcessStartInfo(runtime.ServerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // ggml also looks for its backends in the working directory.
            WorkingDirectory = runtime.Directory,
        };

        foreach (var argument in Arguments(launch, socketPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        ScrubEnvironment(startInfo);
        return startInfo;
    }

    /// <summary>Removes the variables that would change how the engine behaves from <paramref name="startInfo"/>.</summary>
    public static void ScrubEnvironment(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (var name in startInfo.Environment.Keys.Where(IsScrubbed).ToList())
        {
            startInfo.Environment.Remove(name);
        }
    }

    private static bool IsScrubbed(string name) =>
        ScrubbedVariablePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
