namespace Assistant.ModelHost.Processes;

/// <summary>
/// How the model engine is restarted after it exits unexpectedly: a few times, waiting twice as long before each, and
/// not again once <see cref="MaxRestarts"/> in a row have failed. A run that lasts <see cref="StableUptime"/> starts
/// the count over.
/// </summary>
internal sealed record ModelProcessRestartPolicy
{
    /// <summary>How many restarts in a row may be tried before the engine is left stopped.</summary>
    public int MaxRestarts { get; init; } = 3;

    /// <summary>The wait before the first restart.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait before a restart.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a run must last to count as recovered, so that restarts are counted from zero again.</summary>
    public TimeSpan StableUptime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The wait before restart number <paramref name="restart"/> of a series (1 for the first), or
    /// <see langword="null"/> when the series has reached <see cref="MaxRestarts"/>.
    /// </summary>
    public TimeSpan? DelayBefore(int restart)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(restart, 1);
        if (restart > MaxRestarts)
        {
            return null;
        }

        var delay = InitialDelay.Ticks * Math.Pow(2, restart - 1);
        return TimeSpan.FromTicks((long)Math.Min(delay, MaxDelay.Ticks));
    }
}
