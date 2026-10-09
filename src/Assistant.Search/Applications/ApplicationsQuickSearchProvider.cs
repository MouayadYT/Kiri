using System.Collections.Concurrent;
using Assistant.Core.QuickSearch;

namespace Assistant.Search.Applications;

/// <summary>
/// Finds installed applications for what is being typed (PROJECT_SPEC §4.1): the applications Start lists, packaged ones included,
/// from the catalog kept in memory (<see cref="IApplicationCatalog"/>), so nothing waits for the shell. Each result has the
/// application's own icon, its name, the identity it is launched by and, for a desktop program whose file is known, the alternate
/// actions to show it in File Explorer and copy its path. With nothing typed it lists the applications the user runs most, then the
/// rest alphabetically, which is how the Applications category is browsed. Names are matched, ordered and cut to the number asked for
/// here, before any icon is read, so only the few that are listed ever need one. What is typed and what is found are private
/// content and are never logged.
/// </summary>
public sealed class ApplicationsQuickSearchProvider : IQuickSearchProvider
{
    /// <summary>The provider's identity.</summary>
    public const string ProviderId = "applications";

    private readonly IApplicationCatalog _catalog;
    private readonly ApplicationIconCache? _icons;
    private readonly QuickSearchRanker _ranker;
    private readonly TimeProvider _clock;

    /// <summary>Creates the provider over <paramref name="catalog"/>.</summary>
    /// <param name="catalog">The applications.</param>
    /// <param name="icons">Where an application's icon is read; without it results have the application glyph.</param>
    /// <param name="usage">What the user runs, which decides which of equal matches come first.</param>
    /// <param name="clock">The clock that tells how lately something was run; the system's by default.</param>
    /// <param name="iconPatience">How long a search waits for an icon that is not read yet; 300 milliseconds by default.</param>
    public ApplicationsQuickSearchProvider(
        IApplicationCatalog catalog, IApplicationIconSource? icons = null, IQuickSearchUsage? usage = null, TimeProvider? clock = null,
        TimeSpan? iconPatience = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _icons = icons is null ? null : new ApplicationIconCache(icons, patience: iconPatience);
        _ranker = new QuickSearchRanker([this], usage);
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string Id => ProviderId;

    /// <inheritdoc/>
    public string DisplayName => "Applications";

    /// <inheritdoc/>
    public QuickSearchResultType ResultType => QuickSearchResultType.Applications;

    /// <inheritdoc/>
    public int Priority => 100;

    /// <inheritdoc/>
    public int MinimumQueryLength => 0;

    /// <summary>Nothing: the list is in memory, so every keystroke is answered at once.</summary>
    public TimeSpan Debounce => TimeSpan.Zero;

    /// <summary>
    /// Reads the applications and then their icons, in the background and a little at a time, so that no search has to wait for
    /// either. Returns when the icons are read, or at once when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            _catalog.WarmUp();
            var applications = await _catalog.GetApplicationsAsync(cancellationToken).ConfigureAwait(false);
            if (_icons is not null)
            {
                await _icons.WarmUpAsync(applications.Select(application => application.IconPath ?? application.Id), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QuickSearchResult>> SearchAsync(QuickSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var applications = await _catalog.GetApplicationsAsync(cancellationToken).ConfigureAwait(false);
        if (applications.Count == 0)
        {
            return [];
        }

        var all = applications.Select(Result);
        var ranking = _ranker.Rank(
            request.Query, all, _clock.GetUtcNow(), new QuickSearchRankingOptions { Scope = QuickSearchResultType.Applications });

        // Only what matches is listed, unless nothing was typed and the whole list is being browsed.
        var typed = request.Query.Length > 0;
        var best = ranking.Ordered
            .Where(ranked => !typed || ranked.Match.Kind != QuickSearchMatchKind.None)
            .Take(request.MaxResults)
            .Select(ranked => ranked.Result)
            .ToArray();
        if (_icons is null || best.Length == 0)
        {
            return best;
        }

        // An application that a shortcut on the Desktop stands for is drawn with that shortcut's icon.
        var shortcuts = applications.Where(application => application.IconPath is not null).ToDictionary(application => application.Id, application => application.IconPath!, StringComparer.Ordinal);
        var withIcons = await Task.WhenAll(best.Select(result => WithIconAsync(result, shortcuts, cancellationToken))).ConfigureAwait(false);
        return withIcons;
    }

    // The result of one application, without its icon.
    private static QuickSearchResult Result(InstalledApplication application)
    {
        var alternates = new List<QuickSearchAction>(2);
        if (application.ExecutablePath is { Length: > 0 } path)
        {
            alternates.Add(new QuickSearchAction(QuickSearchActionKind.RevealPath, QuickSearchActionTitles.Reveal, path));
            alternates.Add(new QuickSearchAction(QuickSearchActionKind.CopyPath, QuickSearchActionTitles.CopyPath, path));
        }

        return new QuickSearchResult(
            ResultId(application.Id), QuickSearchResultType.Applications, ProviderId, application.DisplayName,
            new QuickSearchAction(QuickSearchActionKind.LaunchApplication, QuickSearchActionTitles.Open, application.Id))
        {
            Icon = new QuickSearchIcon(QuickSearchIconKind.Application),
            Alternates = alternates,

            // The Assistant is Kiri to the person who types it, whatever Start calls it.
            Aliases = IsTheAssistant(application) ? [AssistantNickname] : [],
        };
    }

    /// <summary>What the Assistant is called when it is looked for in the bar, besides what Start lists it as.</summary>
    public const string AssistantNickname = "Kiri";

    // The Assistant's own entry in Start: the program it starts is the Assistant's, or it is listed under its name.
    private static bool IsTheAssistant(InstalledApplication application) =>
        (application.ExecutablePath is { Length: > 0 } path && string.Equals(Path.GetFileName(path), "Assistant.UI.exe", StringComparison.OrdinalIgnoreCase))
        || string.Equals(application.DisplayName, "Assistant", StringComparison.OrdinalIgnoreCase)
        || string.Equals(application.DisplayName, AssistantNickname, StringComparison.OrdinalIgnoreCase);

    /// <summary>The id a result has for the application with launch identity <paramref name="applicationId"/>.</summary>
    public static string ResultId(string applicationId) => "app:" + applicationId;

    private async Task<QuickSearchResult> WithIconAsync(QuickSearchResult result, Dictionary<string, string> shortcuts, CancellationToken cancellationToken)
    {
        var image = await _icons!.GetAsync(shortcuts.GetValueOrDefault(result.Primary.Target, result.Primary.Target), cancellationToken).ConfigureAwait(false);
        return image is null ? result : result with { Icon = new QuickSearchIcon(QuickSearchIconKind.Application, image) };
    }
}

/// <summary>
/// Keeps the icons of applications once they are read, so that listing one is a lookup. A lookup that has to read an icon waits for it
/// only a moment, and lists the application without it when it is late, while the reading carries on and fills the cache for next
/// time. Only the most recently asked for icons are kept. An icon that is read from a file (a shortcut, a program) is kept for the file
/// as it was then: when the file is written again, as it is when the user gives a shortcut another icon, the icon is read again.
/// </summary>
internal sealed class ApplicationIconCache(IApplicationIconSource source, int capacity = 512, TimeSpan? patience = null)
{
    /// <summary>How long a search waits for an icon that is not cached yet.</summary>
    public static readonly TimeSpan LookupPatience = TimeSpan.FromMilliseconds(300);

    private readonly TimeSpan _patience = patience ?? LookupPatience;

    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _icons = new(StringComparer.Ordinal);

    /// <summary>The icon of the application, or <see langword="null"/> when it has none or is not read in time.</summary>
    public async Task<byte[]?> GetAsync(string applicationId, CancellationToken cancellationToken)
    {
        var reading = Reading(applicationId);
        if (reading.IsCompletedSuccessfully)
        {
            return reading.Result;
        }

        try
        {
            return await reading.WaitAsync(_patience, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Reads the icons of <paramref name="applicationIds"/> one or two at a time.</summary>
    public async Task WarmUpAsync(IEnumerable<string> applicationIds, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(2);
        var tasks = new List<Task>();
        foreach (var id in applicationIds)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var reading = Reading(id);
            tasks.Add(reading.ContinueWith(_ => gate.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private Task<byte[]?> Reading(string applicationId)
    {
        var key = KeyOf(applicationId);
        if (_icons.Count >= capacity && !_icons.ContainsKey(key))
        {
            // A cache that is full forgets everything and starts again: icons are read again quickly, and this needs no bookkeeping.
            _icons.Clear();
        }

        return _icons.GetOrAdd(key, _ => new Lazy<Task<byte[]?>>(() => ReadAsync(applicationId))).Value;
    }

    // What an icon is kept under: the identity, and for a file also when it was last written, so that a shortcut whose icon was changed is read again.
    private static string KeyOf(string applicationId)
    {
        if (!Path.IsPathFullyQualified(applicationId))
        {
            return applicationId;
        }

        try
        {
            return File.Exists(applicationId) ? applicationId + "|" + File.GetLastWriteTimeUtc(applicationId).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : applicationId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return applicationId;
        }
    }

    private async Task<byte[]?> ReadAsync(string applicationId)
    {
        try
        {
            return await source.GetIconAsync(applicationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An icon that cannot be read is no icon; the application is listed with its glyph.
            return null;
        }
    }
}
