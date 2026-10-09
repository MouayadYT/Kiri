namespace Assistant.Core.Agent;

/// <summary>
/// The bounds of one agent run (PROJECT_SPEC §4.8, step 114): how many times the model may answer with tool calls, how many calls are run, how long
/// the whole run may take, and when a run that is going nowhere is stopped. A run that reaches a bound is not cut off: the model is asked for its
/// answer in words, without tools, from what it has (the final answer, <see cref="AgentRunner"/>).
/// </summary>
public sealed record AgentLimits
{
    /// <summary>The most rounds of tool calls of one answer by default (PROJECT_SPEC §4.8: at most 4 tool rounds per user turn).</summary>
    public const int DefaultMaxToolRounds = 4;

    /// <summary>The most calls of one round that are run by default.</summary>
    public const int DefaultMaxCallsPerRound = 6;

    /// <summary>The most calls of a whole run that are run by default.</summary>
    public const int DefaultMaxToolCalls = 12;

    /// <summary>The longest a run may work by default: model answers and tools together, before the final answer.</summary>
    public static readonly TimeSpan DefaultTotalTime = TimeSpan.FromMinutes(5);

    /// <summary>The time the final answer is given by default, after a bound has been reached.</summary>
    public static readonly TimeSpan DefaultFinalAnswerTime = TimeSpan.FromSeconds(60);

    private readonly int _maxToolRounds = DefaultMaxToolRounds;
    private readonly int _maxCallsPerRound = DefaultMaxCallsPerRound;
    private readonly int _maxToolCalls = DefaultMaxToolCalls;
    private readonly TimeSpan _totalTime = DefaultTotalTime;
    private readonly TimeSpan _finalAnswerTime = DefaultFinalAnswerTime;
    private readonly int _maxRoundsWithoutProgress = 2;
    private readonly int _maxRepeatedCalls = 3;

    /// <summary>The bounds a chat turn runs under.</summary>
    public static AgentLimits Default { get; } = new();

    /// <summary>The most times the model may answer with tool calls; after that many rounds it must answer in words.</summary>
    public int MaxToolRounds
    {
        get => _maxToolRounds;
        init => _maxToolRounds = InRange(value, 1, 16, nameof(MaxToolRounds));
    }

    /// <summary>The most calls of one round that are run; the rest of the round get a result that says so.</summary>
    public int MaxCallsPerRound
    {
        get => _maxCallsPerRound;
        init => _maxCallsPerRound = InRange(value, 1, 16, nameof(MaxCallsPerRound));
    }

    /// <summary>The most calls of the whole run that are run; a call that was refused or never ran does not count.</summary>
    public int MaxToolCalls
    {
        get => _maxToolCalls;
        init => _maxToolCalls = InRange(value, 1, 64, nameof(MaxToolCalls));
    }

    /// <summary>
    /// The longest the run may work, from the model's first request to the last tool: what is in progress when it runs out is given up (a
    /// tool that is running is cancelled and its result says it timed out) and the model is asked for its final answer.
    /// </summary>
    public TimeSpan TotalTime
    {
        get => _totalTime;
        init => _totalTime = InRange(value, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(30), nameof(TotalTime));
    }

    /// <summary>
    /// The time the final answer is given after a bound has been reached, on top of <see cref="TotalTime"/>. When it too runs out, or the model
    /// says nothing, the Assistant says in its own words that the run was stopped.
    /// </summary>
    public TimeSpan FinalAnswerTime
    {
        get => _finalAnswerTime;
        init => _finalAnswerTime = InRange(value, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(5), nameof(FinalAnswerTime));
    }

    /// <summary>
    /// How many rounds in a row may do nothing (every call failed, was refused or repeated one already made) before the run is stopped as
    /// going nowhere.
    /// </summary>
    public int MaxRoundsWithoutProgress
    {
        get => _maxRoundsWithoutProgress;
        init => _maxRoundsWithoutProgress = InRange(value, 1, 8, nameof(MaxRoundsWithoutProgress));
    }

    /// <summary>How many times in a run the model may repeat a call it already made before it is stopped as going in circles.</summary>
    public int MaxRepeatedCalls
    {
        get => _maxRepeatedCalls;
        init => _maxRepeatedCalls = InRange(value, 1, 8, nameof(MaxRepeatedCalls));
    }

    private static int InRange(int value, int minimum, int maximum, string name) =>
        value >= minimum && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} is from {minimum} to {maximum}.");

    private static TimeSpan InRange(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string name) =>
        value >= minimum && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} is from {minimum} to {maximum}.");
}
