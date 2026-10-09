using System.Runtime.CompilerServices;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Search.Planning;

namespace Assistant.Search.Tests;

/// <summary>A clock that stands still at an instant and lives in a time zone of the test's choosing.</summary>
internal sealed class FixedZoneClock(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;

    public override TimeZoneInfo LocalTimeZone => zone;
}

/// <summary>
/// The day every planner test is set on: Friday 2 October 2026, 10:30 in a zone two hours ahead of UTC, with weeks that begin
/// on Monday. Yesterday is Thursday 1 October, last Tuesday is 29 September, last week is 21 to 27 September, last month is
/// September and last year is 2025.
/// </summary>
internal static class PlannerDay
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");

    public static TimeProvider Clock { get; } = new FixedZoneClock(new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.Zero), Zone);

    public static PlanCalendar Calendar { get; } = new(Clock, DayOfWeek.Monday);

    /// <summary>The instant a local midnight is, in this zone.</summary>
    public static DateTimeOffset Midnight(int year, int month, int day) =>
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(2)).ToUniversalTime();
}

/// <summary>Library folders at fixed paths, so a plan's folder is checked by value.</summary>
internal sealed class FakePlanFolders : IPlanFolders
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal)
    {
        ["documents"] = @"C:\Users\Test\Documents",
        ["downloads"] = @"C:\Users\Test\Downloads",
        ["desktop"] = @"C:\Users\Test\Desktop",
        ["pictures"] = @"C:\Users\Test\Pictures",
        ["screenshots"] = @"C:\Users\Test\Pictures\Screenshots",
        ["music"] = @"C:\Users\Test\Music",
        ["videos"] = @"C:\Users\Test\Videos",
    };

    public IReadOnlyList<string> Names => [.. _paths.Keys];

    public string? Resolve(string name) => _paths.GetValueOrDefault(name);
}

/// <summary>A model that answers a planning request with the pieces the test gives it, and keeps every request it was asked.</summary>
internal sealed class ScriptedPlanModel : IModelService
{
    public ScriptedPlanModel(params string[] pieces) => Pieces = pieces;

    /// <summary>The model's reply, as the pieces it streams.</summary>
    public IReadOnlyList<string> Pieces { get; set; }

    /// <summary>The model that is set up; none when <see langword="null"/>.</summary>
    public ModelInfo? Active { get; set; } = new("test-model", 4096);

    /// <summary>What it throws instead of answering, when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Whether it never answers, waiting until it is cancelled.</summary>
    public bool NeverAnswers { get; set; }

    public List<ModelRequest> Requests { get; } = [];

    /// <summary>How many pieces of the reply were read.</summary>
    public int Pulled { get; private set; }

    public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active);

    public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Failure is not null)
        {
            throw Failure;
        }

        if (NeverAnswers)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        foreach (var piece in Pieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pulled++;
            yield return AssistantResponseChunk.ForTextDelta(piece);
            await Task.Yield();
        }
    }
}

/// <summary>A permission policy that says what the test tells it, and remembers what it was asked.</summary>
internal sealed class FakePermissions(bool filesAllowed = true) : IPermissionPolicy
{
    public bool FilesAllowed { get; set; } = filesAllowed;

    public int Asked { get; private set; }

    public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default)
    {
        Asked++;
        var reason = capability == PermissionCapability.Files && FilesAllowed
            ? PermissionDecisionReason.Granted
            : PermissionDecisionReason.TurnedOff;
        return Task.FromResult(new PermissionDecision(capability, reason));
    }
}

/// <summary>A planner that plans with the function the test gives it.</summary>
internal sealed class FakePlanner(Func<string, PlannedFileSearch?>? quick = null, Func<string, PlannedFileSearch>? plan = null)
    : IFileSearchPlanner
{
    public List<string> Planned { get; } = [];

    public PlannedFileSearch? PlanKnownShape(string request) => quick?.Invoke(request);

    public Task<PlannedFileSearch> PlanAsync(string request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Planned.Add(request);
        return Task.FromResult(plan?.Invoke(request) ?? new PlannedFileSearch(null, FileSearchPlanSource.Read));
    }
}

/// <summary>A file search that answers with the items the test gives it, and keeps every query it was asked.</summary>
internal sealed class FakeFileSearch : IFileSearchService
{
    public IReadOnlyList<SearchResultItem> Items { get; set; } = [];

    public ContentSearchCapability Capability { get; set; } = ContentSearchCapability.NotRequested;

    public FileSearchException? Failure { get; set; }

    /// <summary>What each query is answered with, when the test needs queries answered differently; else <see cref="Items"/>.</summary>
    public Func<FileSearchQuery, IReadOnlyList<SearchResultItem>>? Answer { get; set; }

    /// <summary>What a query fails with, when set and it returns an exception; else <see cref="Failure"/>.</summary>
    public Func<FileSearchQuery, FileSearchException?>? FailFor { get; set; }

    public List<FileSearchQuery> Queries { get; } = [];

    public Task<IReadOnlyList<SearchResultItem>> SearchAsync(FileSearchQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Queries.Add(query);
        var failure = FailFor?.Invoke(query) ?? Failure;
        return failure is null ? Task.FromResult(Answer?.Invoke(query) ?? Items) : Task.FromException<IReadOnlyList<SearchResultItem>>(failure);
    }

    public async Task<FileSearchOutcome> SearchWithCapabilitiesAsync(FileSearchQuery query, CancellationToken cancellationToken = default) =>
        new(await SearchAsync(query, cancellationToken), Capability);
}

/// <summary>
/// A reviewer that picks with the function the test gives it (all the candidates when none is given; <see langword="null"/> from
/// the function is "could not judge"), and keeps what it was asked.
/// </summary>
internal sealed class FakeReviewer(Func<string, IReadOnlyList<SearchResultItem>, IReadOnlyList<SearchResultItem>?>? choose = null)
    : IFileMatchReviewer
{
    public List<(string Request, IReadOnlyList<SearchResultItem> Candidates)> Asked { get; } = [];

    public Task<IReadOnlyList<SearchResultItem>?> ChooseAsync(
        string request,
        IReadOnlyList<SearchResultItem> candidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Asked.Add((request, candidates));
        return Task.FromResult(choose is null ? candidates : choose(request, candidates));
    }
}
