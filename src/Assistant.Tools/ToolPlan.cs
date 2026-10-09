using Assistant.Core.Confirmation;
using Assistant.Core.Domain;

namespace Assistant.Tools;

/// <summary>
/// What a tool that changes something has worked out it would do for one call, before it does anything (PROJECT_SPEC §4.8, step 115): the target resolved (the
/// person a name stands for, the file an id stands for, the application a name matches), what the user is to be asked, and the action itself, bound to exactly
/// that target. The executor shows the user <see cref="Confirmation"/> and runs <see cref="RunAsync"/> only if they say yes, so what is approved is what is done:
/// nothing is looked up a second time between the question and the action.
/// </summary>
/// <remarks>Making a plan changes nothing: it may read (the list of people, the list of applications) and nothing more.</remarks>
public sealed class ToolPlan
{
    private ToolPlan(ToolConfirmation? confirmation, Func<CancellationToken, Task<ToolResult>>? run, ToolResult? refusal)
    {
        Confirmation = confirmation;
        Run = run;
        Refusal = refusal;
    }

    /// <summary>
    /// What the user is asked, or <see langword="null"/> to have the executor ask about the call's arguments, every one as it was given. A plan cannot skip the
    /// question: a plan that says nothing is asked about the call itself.
    /// </summary>
    public ToolConfirmation? Confirmation { get; }

    /// <summary>The refusal, when the call cannot be done and the user is not asked: it is the call's result, and nothing runs.</summary>
    public ToolResult? Refusal { get; }

    private Func<CancellationToken, Task<ToolResult>>? Run { get; }

    /// <summary>A plan that does the call, once the user has said yes to <paramref name="confirmation"/> (or to the call's arguments, when it is <see langword="null"/>).</summary>
    public static ToolPlan Do(Func<CancellationToken, Task<ToolResult>> run, ToolConfirmation? confirmation = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new ToolPlan(confirmation, run, null);
    }

    /// <summary>A plan that finds the call cannot be done: <paramref name="result"/> is told to the model, the user is not asked, and nothing runs.</summary>
    public static ToolPlan Refuse(ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ToolPlan(null, null, result);
    }

    /// <summary>Does what was planned. Only the executor calls it, and only after the user approved <see cref="Confirmation"/>.</summary>
    internal Task<ToolResult> RunAsync(CancellationToken cancellationToken) =>
        Run is { } run ? run(cancellationToken) : throw new InvalidOperationException("A refused plan has nothing to run.");
}
