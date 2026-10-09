namespace Assistant.Core.Agent;

/// <summary>How an agent run ended. Abandoned: the caller stopped reading the answer without cancelling or failing it.</summary>
public enum AgentOutcome
{
    /// <summary>The run came to its answer, in words, whether by itself or because a bound was reached.</summary>
    Completed,

    /// <summary>The caller stopped it.</summary>
    Stopped,

    /// <summary>The model could not answer.</summary>
    Failed,

    /// <summary>The caller stopped reading the answer without cancelling or failing it.</summary>
    Abandoned,
}

/// <summary>Why an agent run gave its final answer when it did.</summary>
public enum AgentStopReason
{
    /// <summary>The model answered in words without asking for tools: nothing stopped it.</summary>
    Answered,

    /// <summary>The model used all the rounds of tool calls it is allowed (<see cref="AgentLimits.MaxToolRounds"/>).</summary>
    RoundLimit,

    /// <summary>The model used all the calls it is allowed (<see cref="AgentLimits.MaxToolCalls"/>).</summary>
    ToolCallLimit,

    /// <summary>The run took as long as it may (<see cref="AgentLimits.TotalTime"/>).</summary>
    TimeLimit,

    /// <summary>The model kept repeating a call, or its calls kept coming to nothing (<see cref="AgentLoopGuard"/>).</summary>
    Looping,

    /// <summary>The caller cancelled the run.</summary>
    Cancelled,

    /// <summary>The model could not answer.</summary>
    Failed,
}

/// <summary>
/// How a run is going, for whoever started it: counts and the way it ended, never any content. The runner fills it in as the answer streams, so it
/// is right whether the answer was read to the end, stopped or failed.
/// </summary>
public sealed class AgentProgress
{
    /// <summary>How the run ended; <see cref="AgentOutcome.Abandoned"/> until it does.</summary>
    public AgentOutcome Outcome { get; set; } = AgentOutcome.Abandoned;

    /// <summary>The text chunks of the model's answers so far, and of the words the Assistant added itself.</summary>
    public int TextChunks { get; set; }

    /// <summary>The tool calls answered with a result so far (the run ones, and the refused ones).</summary>
    public int ToolCalls { get; set; }

    /// <summary>Why the final answer came when it did; <see cref="AgentStopReason.Answered"/> until a bound is reached.</summary>
    public AgentStopReason StopReason { get; set; } = AgentStopReason.Answered;
}
