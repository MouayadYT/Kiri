using System.Diagnostics;
using Assistant.Core.Contracts;

namespace Assistant.Core.Agent;

/// <summary>
/// The time limit of an agent run (<see cref="AgentLimits.TotalTime"/>), which stops running while the run waits for the user (PROJECT_SPEC §4.8, step 115): a
/// person reading a question "may I send this message?" is not the model being slow, and a run must not run out of time under their eyes. It cancels the
/// source it was given when the time that is left is used up.
/// </summary>
public sealed class RunClock : IRunPause
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source;
    private TimeSpan _left;
    private long _runningSince;
    private int _holds;

    /// <summary>Starts the clock: <paramref name="source"/> is cancelled when <paramref name="total"/> of running time has passed.</summary>
    public RunClock(CancellationTokenSource source, TimeSpan total)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _left = total;
        _runningSince = Stopwatch.GetTimestamp();
        Arm(total);
    }

    /// <inheritdoc/>
    public IDisposable Pause()
    {
        lock (_gate)
        {
            if (_holds++ == 0)
            {
                _left -= Stopwatch.GetElapsedTime(_runningSince);
                Arm(Timeout.InfiniteTimeSpan);
            }
        }

        return new Hold(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holds == 0)
            {
                _runningSince = Stopwatch.GetTimestamp();
                Arm(_left > TimeSpan.Zero ? _left : TimeSpan.Zero);
            }
        }
    }

    // The run may be over (its source disposed) by the time a tool that was given up on lets go of the clock: there is then nothing left to time.
    private void Arm(TimeSpan time)
    {
        try
        {
            _source.CancelAfter(time);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class Hold(RunClock clock) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                clock.Release();
            }
        }
    }
}
