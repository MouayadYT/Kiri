namespace Assistant.Core.Domain;

/// <summary>
/// What one running operation reports about itself: its kind and a short status text that is shown, and read out by
/// screen readers, while it runs.
/// </summary>
/// <param name="Id">Identifies the operation.</param>
/// <param name="Kind">What the operation is doing.</param>
/// <param name="Text">
/// The status, such as "Searching". It is on screen and in the accessibility tree, and it is never private content
/// (PROJECT_SPEC §3.2): it must not hold the query, a file name, a document's text or a tool's arguments.
/// </param>
public sealed record ActivityStatus(Guid Id, ActivityKind Kind, string Text)
{
    /// <summary>What the Working pill says while the model works on an answer, as the reference does.</summary>
    public const string WorkingText = "Working";

    /// <summary>What it says while the web is being looked up for an answer.</summary>
    public const string LookingText = "Looking into it";

    /// <summary>The status text an operation of <paramref name="kind"/> shows unless it says something else.</summary>
    public static string DefaultText(ActivityKind kind) => kind switch
    {
        ActivityKind.WindowsSearch or ActivityKind.FileSearch => "Searching",
        ActivityKind.WebSearch => LookingText,
        _ => WorkingText,
    };
}
