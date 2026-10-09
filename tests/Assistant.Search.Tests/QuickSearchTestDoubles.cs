using System.Collections.Concurrent;
using Assistant.Core.QuickSearch;

namespace Assistant.Search.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// An application source the test controls: what it lists, when a reading ends, whether it fails and how often it was read.
/// </summary>
internal sealed class FakeApplicationSource : IApplicationSource
{
    private int _reads;

    public IReadOnlyList<InstalledApplication> Applications { get; set; } = [];

    /// <summary>While set, a reading waits for it before it answers.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public bool Fails { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public bool HasSubscribers => Changed is not null;

    public async Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Fails ? throw new InvalidOperationException("The shell cannot be read: C:\\secret") : Applications;
    }
}

/// <summary>A catalog with the applications the test gives it.</summary>
internal sealed class FakeApplicationCatalog(params InstalledApplication[] applications) : IApplicationCatalog
{
    public IReadOnlyList<InstalledApplication> Applications { get; set; } = applications;

    public event EventHandler? Changed;

    public IReadOnlyList<InstalledApplication> Current => Applications;

    public bool IsLoaded => true;

    public int WarmUps { get; private set; }

    public Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Applications);

    public void WarmUp() => WarmUps++;

    public void Invalidate() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>An icon source that draws what the test says, and remembers what it was asked and how many at once.</summary>
internal sealed class FakeIconSource : IApplicationIconSource
{
    private readonly ConcurrentQueue<string> _asked = new();
    private int _running;
    private int _peak;

    /// <summary>The icon of each application, by identity; an application it does not know has none.</summary>
    public Dictionary<string, byte[]> Icons { get; } = [];

    /// <summary>Applications whose icons never arrive until the gate is opened.</summary>
    public HashSet<string> Slow { get; } = [];

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HashSet<string> Failing { get; } = [];

    public IReadOnlyList<string> Asked => [.. _asked];

    public int Peak => Volatile.Read(ref _peak);

    public async Task<byte[]?> GetIconAsync(string applicationId, CancellationToken cancellationToken)
    {
        _asked.Enqueue(applicationId);
        var running = Interlocked.Increment(ref _running);
        InterlockedMax(ref _peak, running);
        try
        {
            if (Slow.Contains(applicationId))
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            await Task.Delay(1, cancellationToken);
            if (Failing.Contains(applicationId))
            {
                throw new InvalidOperationException("No icon.");
            }

            return Icons.GetValueOrDefault(applicationId);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
