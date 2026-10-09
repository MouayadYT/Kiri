using System.Diagnostics.CodeAnalysis;
using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Runtime;

/// <summary>What an <see cref="IModelRuntimeLocator"/> found: the runtime when it can run, or what is wrong with it.</summary>
internal sealed class ModelRuntimeStatus
{
    private ModelRuntimeStatus(ModelRuntimeState state, ModelRuntime? runtime, IReadOnlyList<string> missingFiles)
    {
        State = state;
        Runtime = runtime;
        MissingFiles = missingFiles;
    }

    /// <summary>Whether the runtime can run, and if not, why.</summary>
    public ModelRuntimeState State { get; }

    /// <summary>The runtime, when <see cref="IsReady"/>.</summary>
    public ModelRuntime? Runtime { get; }

    /// <summary>
    /// The file names (never paths) of the native files that are missing, for
    /// <see cref="ModelRuntimeState.Incomplete"/> and <see cref="ModelRuntimeState.MissingSystemComponent"/>.
    /// </summary>
    public IReadOnlyList<string> MissingFiles { get; }

    /// <summary>What to tell the user, in one sentence.</summary>
    public string Message => ModelRuntimeStateText.Describe(State);

    /// <summary>Whether the runtime can run.</summary>
    [MemberNotNullWhen(true, nameof(Runtime))]
    public bool IsReady => State == ModelRuntimeState.Ready;

    /// <summary>The runtime can run.</summary>
    public static ModelRuntimeStatus Ready(ModelRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return new(ModelRuntimeState.Ready, runtime, []);
    }

    /// <summary>The runtime cannot run, for the reason <paramref name="state"/> gives.</summary>
    public static ModelRuntimeStatus Unavailable(ModelRuntimeState state, IReadOnlyList<string>? missingFiles = null)
    {
        if (state == ModelRuntimeState.Ready)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "A ready runtime needs its files.");
        }

        return new(state, null, missingFiles ?? []);
    }

    public override string ToString() => $"{State} ({MissingFiles.Count} files missing)";
}
