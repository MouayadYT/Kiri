using System.Diagnostics;
using Assistant.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace Assistant.Search.Planning;

/// <summary>
/// The natural-language search planner (PROJECT_SPEC §4.7): turns "find the PDF about biology I edited last Tuesday" into a
/// <see cref="FileSearchQuery"/>. A request of a known shape ("the last 5 screenshots I took") is read by its template; any other
/// is read word by word (<see cref="RequestReader"/>) into a kind of file, days, an order, a place, a size and the words of the
/// name. Both are fixed rules that forgive typing mistakes, so a plan is instant and its days are always right.
/// </summary>
/// <remarks>
/// A local model once wrote these plans as JSON; on the user's own model it gave the wrong day for "last Tuesday", made up a day
/// for "the last doc I made" and put "on my computer" in the name, all in valid JSON that no check could catch. The model now
/// helps where it is good, judging real names that partly match (<see cref="ModelFileMatchReviewer"/>). A request, a plan and a
/// word are never logged: the log line holds where the plan came from, how many words it has, and a duration.
/// </remarks>
public sealed class FileSearchPlanner : IFileSearchPlanner
{
    private readonly ILogger _logger;
    private readonly RecentMediaTemplate _template;
    private readonly RequestReader _reader;

    /// <summary>Creates the planner, dating a request by <paramref name="clock"/>.</summary>
    public FileSearchPlanner(TimeProvider clock, ILogger<FileSearchPlanner> logger)
        : this(clock, logger, new KnownPlanFolders())
    {
    }

    internal FileSearchPlanner(TimeProvider clock, ILogger logger, IPlanFolders folders, DayOfWeek? firstDayOfWeek = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(folders);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var calendar = new PlanCalendar(clock, firstDayOfWeek);
        _template = new RecentMediaTemplate(calendar);
        _reader = new RequestReader(calendar, folders);
    }

    /// <inheritdoc/>
    public PlannedFileSearch? PlanKnownShape(string request) => _template.TryPlan(RequestText.Clean(request));

    /// <inheritdoc/>
    public Task<PlannedFileSearch> PlanAsync(string request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Plan(request));
    }

    /// <summary>Plans <paramref name="request"/> at once.</summary>
    internal PlannedFileSearch Plan(string request)
    {
        var clock = Stopwatch.StartNew();
        var cleaned = RequestText.Clean(request);
        if (cleaned.Length == 0)
        {
            return new PlannedFileSearch(null, FileSearchPlanSource.Read);
        }

        if (_template.TryPlan(cleaned) is { } templated)
        {
            PlanningLog.Planned(_logger, FileSearchPlanSource.Template, 0, clock.ElapsedMilliseconds);
            return templated;
        }

        var reading = _reader.Read(cleaned);
        PlanningLog.Planned(_logger, FileSearchPlanSource.Read, reading.Keywords.Count, clock.ElapsedMilliseconds);
        return reading.HasCriterion || reading.Order is not null
            ? new PlannedFileSearch(_reader.ToQuery(reading), FileSearchPlanSource.Read)
            {
                Keywords = reading.Keywords,
                KindName = reading.Type is { } type ? (type.Singular, type.Plural) : null,
            }
            : new PlannedFileSearch(null, FileSearchPlanSource.Read);
    }

}
