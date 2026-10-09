using Assistant.Core.Domain;

namespace Assistant.Core.Agent;

/// <summary>What became of one tool call of an agent run.</summary>
public enum AgentCallDisposition
{
    /// <summary>The tool was run (it may have failed: see <see cref="AgentCallTrace.Status"/>).</summary>
    Ran,

    /// <summary>The call could not be read (bad JSON, a bad name or arguments the parser refused); the tool was not asked.</summary>
    Unreadable,

    /// <summary>The same call had already been made in the run, so it was not run again.</summary>
    Repeated,

    /// <summary>The tool is not one the model was offered in this round.</summary>
    NotOffered,

    /// <summary>The round, the run or the time had no room for it.</summary>
    OverLimit,
}

/// <summary>
/// One tool call of a round of an agent run, as the internal trace keeps it: which tool, what became of the call and how long it took. It never holds
/// the call's arguments or the tool's result (private content, PROJECT_SPEC §3.2), only a fingerprint of the arguments, which tells two calls apart
/// without saying what they were.
/// </summary>
/// <param name="CallId">The id of the call in the conversation.</param>
/// <param name="ToolName">The tool the call named, as the Assistant tidied it.</param>
/// <param name="Disposition">What became of the call.</param>
/// <param name="Status">The status of the result the model was given.</param>
/// <param name="ErrorCode">The <see cref="Tools.ToolErrors"/> code when the result was a failure, otherwise <see langword="null"/>.</param>
/// <param name="Duration">How long the tool ran, zero when it was not run.</param>
/// <param name="ResultCharacters">The length of the result the model was given.</param>
/// <param name="ArgumentsFingerprint">A short hash of the call's arguments.</param>
public sealed record AgentCallTrace(
    string CallId,
    string ToolName,
    AgentCallDisposition Disposition,
    ToolResultStatus Status,
    string? ErrorCode,
    TimeSpan Duration,
    int ResultCharacters,
    string ArgumentsFingerprint);

/// <summary>One round of an agent run: the tools the model was offered, what it said and called, and how long the model took.</summary>
/// <param name="Number">The round, from 1. The final answer's round has no calls and no tools.</param>
/// <param name="OfferedTools">The names of the tools the model was offered in this round.</param>
/// <param name="AnswerCharacters">The length of the words the model wrote in the round.</param>
/// <param name="ModelTime">How long the model took to answer.</param>
/// <param name="Calls">The calls the model made in the round, in order.</param>
/// <param name="IsFinalAnswer">Whether the model was asked for its final answer in this round (no tools offered).</param>
/// <param name="TimedOut">Whether the run's time ran out while the round was going on.</param>
public sealed record AgentStepTrace(
    int Number,
    IReadOnlyList<string> OfferedTools,
    int AnswerCharacters,
    TimeSpan ModelTime,
    IReadOnlyList<AgentCallTrace> Calls,
    bool IsFinalAnswer,
    bool TimedOut);

/// <summary>
/// The detailed internal record of an agent run (PROJECT_SPEC §4.8, step 114), kept apart from the conversation the user sees: the conversation holds
/// what was said and the tool chips; the trace holds how the loop went (rounds, offered tools, timings, why it stopped). It holds no private content:
/// no prompt, answer, argument or result, only names, counts, durations and fingerprints, so it can be shown to the user in a log of what the
/// Assistant did, or kept, without more care than a count.
/// </summary>
/// <param name="RunId">An id for the run.</param>
/// <param name="ConversationId">The conversation the run belongs to.</param>
/// <param name="StartedAt">When the run began.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="StopReason">Why the final answer came when it did.</param>
/// <param name="Steps">The rounds, in order.</param>
/// <param name="AssistantWrotePart">Whether the Assistant had to write the end of the answer itself because the model said nothing.</param>
public sealed record AgentTrace(
    Guid RunId,
    Guid ConversationId,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    AgentOutcome Outcome,
    AgentStopReason StopReason,
    IReadOnlyList<AgentStepTrace> Steps,
    bool AssistantWrotePart)
{
    /// <summary>The calls of every round, in order.</summary>
    public IEnumerable<AgentCallTrace> Calls => Steps.SelectMany(step => step.Calls);

    /// <summary>How many calls were run, which is what <see cref="AgentLimits.MaxToolCalls"/> bounds.</summary>
    public int ToolsRun => Calls.Count(call => call.Disposition == AgentCallDisposition.Ran);
}

/// <summary>Where the trace of an agent run goes when the run is over.</summary>
public interface IAgentTraceSink
{
    /// <summary>Takes the trace of a run that ended, however it ended. Never throws to the run: what goes wrong here is the sink's own.</summary>
    void Record(AgentTrace trace);
}

/// <summary>
/// Keeps the traces of the last runs in memory, for a log of what the Assistant did or for diagnosing a run. Nothing is written to disk and nothing is
/// kept past the app's life; it holds counts and names only (<see cref="AgentTrace"/>).
/// </summary>
public sealed class AgentTraceStore : IAgentTraceSink
{
    /// <summary>The most traces kept; the oldest go first.</summary>
    public const int Capacity = 32;

    private readonly object _gate = new();
    private readonly List<AgentTrace> _traces = [];

    /// <inheritdoc/>
    public void Record(AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        lock (_gate)
        {
            _traces.Add(trace);
            if (_traces.Count > Capacity)
            {
                _traces.RemoveRange(0, _traces.Count - Capacity);
            }
        }
    }

    /// <summary>The traces kept, oldest first.</summary>
    public IReadOnlyList<AgentTrace> Recent()
    {
        lock (_gate)
        {
            return [.. _traces];
        }
    }

    /// <summary>The traces kept for one conversation, oldest first.</summary>
    public IReadOnlyList<AgentTrace> Recent(Guid conversationId)
    {
        lock (_gate)
        {
            return [.. _traces.Where(trace => trace.ConversationId == conversationId)];
        }
    }
}
