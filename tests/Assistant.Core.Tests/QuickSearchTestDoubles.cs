using Assistant.Core.QuickSearch;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Tests;

/// <summary>A quick-search provider the test scripts: what it finds, how long it waits, and what it was asked.</summary>
internal sealed class ScriptedProvider(
    string id, QuickSearchResultType type = QuickSearchResultType.Applications, int priority = 50, int minimumLength = 0,
    TimeSpan? debounce = null) : IQuickSearchProvider
{
    private readonly object _gate = new();
    private readonly List<QuickSearchRequest> _requests = [];

    /// <summary>What the provider returns; by default nothing.</summary>
    public Func<QuickSearchRequest, CancellationToken, Task<IReadOnlyList<QuickSearchResult>>> Answer { get; set; } =
        (_, _) => Task.FromResult<IReadOnlyList<QuickSearchResult>>([]);

    public string Id => id;

    public string DisplayName => id;

    public QuickSearchResultType ResultType => type;

    public int Priority => priority;

    public int MinimumQueryLength => minimumLength;

    public TimeSpan Debounce => debounce ?? TimeSpan.Zero;

    /// <summary>The requests the provider was asked, in order.</summary>
    public IReadOnlyList<QuickSearchRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Task<IReadOnlyList<QuickSearchResult>> SearchAsync(QuickSearchRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        return Answer(request, cancellationToken);
    }

    /// <summary>A provider that returns <paramref name="results"/> at once.</summary>
    public static ScriptedProvider Returning(
        string id, QuickSearchResultType type, int priority, params QuickSearchResult[] results) =>
        new(id, type, priority) { Answer = (_, _) => Task.FromResult<IReadOnlyList<QuickSearchResult>>(results) };
}

/// <summary>Makes results for tests.</summary>
internal static class QuickResults
{
    public static QuickSearchResult Make(
        string id, string title, QuickSearchResultType type = QuickSearchResultType.Applications, string provider = "p",
        double relevance = 0, string[]? keywords = null) =>
        new(id, type, provider, title, new QuickSearchAction(QuickSearchActionKind.OpenPath, "Open", id))
        {
            Relevance = relevance,
            Keywords = keywords ?? [],
        };
}

/// <summary>A clock that runs only when told to, whose timers fire when it is advanced past them.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _now;

    /// <summary>How many timers are waiting to fire.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => !timer.Done);
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(timer => !timer.Done && timer.Due <= _now)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public bool Done { get; private set; }

        public TimeSpan Due => due;

        public void Fire()
        {
            if (!Done)
            {
                Done = true;
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => Done = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A logger that keeps every line it is given, fully formatted, so a test can look for what must never be in a log.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
        }
    }
}
