using System.Globalization;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;

namespace Assistant.Core.QuickSearch.Actions;

/// <summary>
/// Finds the Assistant's own quick actions for what is being typed (PROJECT_SPEC §4.1): open settings, take a screenshot, start a new
/// conversation, show the history, mute and unmute, change the volume, lock the PC, open a common folder. Every one is deterministic and
/// runs outside the model when the user chooses it. Only an action that is defined as available and safe
/// (<see cref="QuickActionDefinition.IsSafeToRun"/>), that something can run (<see cref="IQuickActionExecutor.CanRun"/>), and whose
/// permission, if it has one, is on, is ever listed; the ones that are planned or refused by design are not, whatever is typed. It looks
/// only in memory, so it answers at once. A number typed with a volume word ("volume 30") makes the one action that takes a number.
/// With nothing typed it lists every action that is available.
/// </summary>
public sealed class ActionsQuickSearchProvider : IQuickSearchProvider
{
    /// <summary>The provider's identity.</summary>
    public const string ProviderId = "actions";

    private readonly IQuickActionCatalog _catalog;
    private readonly IQuickActionExecutor _executor;
    private readonly IPermissionPolicy? _permissions;
    private readonly IQuickSearchUsage? _usage;
    private readonly TimeProvider _clock;

    /// <summary>Creates the provider.</summary>
    /// <param name="catalog">The actions that are defined.</param>
    /// <param name="executor">What runs them; an action it cannot run is not listed.</param>
    /// <param name="permissions">Says whether an action's permission is on; without it an action that needs one is not listed.</param>
    /// <param name="usage">What the user runs, which decides which of equal matches come first.</param>
    /// <param name="clock">The clock that tells how lately something was run; the system's by default.</param>
    public ActionsQuickSearchProvider(
        IQuickActionCatalog catalog, IQuickActionExecutor executor, IPermissionPolicy? permissions = null,
        IQuickSearchUsage? usage = null, TimeProvider? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _permissions = permissions;
        _usage = usage;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string Id => ProviderId;

    /// <inheritdoc/>
    public string DisplayName => "Actions";

    /// <inheritdoc/>
    public QuickSearchResultType ResultType => QuickSearchResultType.Actions;

    /// <inheritdoc/>
    public int Priority => 80;

    /// <inheritdoc/>
    public int MinimumQueryLength => 0;

    /// <summary>Nothing: the actions are in memory.</summary>
    public TimeSpan Debounce => TimeSpan.Zero;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QuickSearchResult>> SearchAsync(QuickSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var typed = request.Query ?? "";
        var volume = ParseVolume(typed);

        var candidates = new List<QuickSearchResult>();
        foreach (var definition in _catalog.All)
        {
            if (!definition.IsSafeToRun || !_executor.CanRun(definition.Id))
            {
                continue;
            }

            if (definition.Parameter is not null)
            {
                // An action that takes a number is listed only with the number: "volume 30".
                if (volume is { } level && definition.Id == QuickActionIds.SetVolume)
                {
                    candidates.Add(WithLevel(definition, level));
                }

                continue;
            }

            if (definition.RequiredPermission is { } capability && !await IsAllowedAsync(capability, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            candidates.Add(Result(definition));
        }

        // Only what the typed words match is listed; with nothing typed the whole list is.
        var ranker = new QuickSearchRanker([this], _usage);
        var ranked = ranker.Rank(typed, candidates, _clock.GetUtcNow(), new QuickSearchRankingOptions { Scope = QuickSearchResultType.Actions });
        return
        [
            .. ranked.Ordered
                .Where(candidate => typed.Length == 0 || candidate.Match.Kind != QuickSearchMatchKind.None)
                .Take(request.MaxResults)
                .Select(candidate => candidate.Result),
        ];
    }

    /// <summary>The id of the result for the action <paramref name="actionId"/>.</summary>
    public static string ResultId(string actionId) => "action:" + actionId;

    // Whether the action is listed: its permission is on, or set to ask every time (the action asks when it runs, step 119). Running it is guarded by what it starts.
    private async Task<bool> IsAllowedAsync(PermissionCapability capability, CancellationToken cancellationToken) =>
        _permissions is not null && (await _permissions.CheckAsync(capability, cancellationToken).ConfigureAwait(false)).CouldBeAllowed;

    private static QuickSearchResult Result(QuickActionDefinition definition) =>
        new(ResultId(definition.Id), QuickSearchResultType.Actions, ProviderId, definition.Title,
            new QuickSearchAction(QuickSearchActionKind.RunAction, QuickSearchActionTitles.Run, definition.Id))
        {
            Subtitle = definition.Description,
            Keywords = definition.Keywords,
            Icon = new QuickSearchIcon(definition.OpensFolder ? QuickSearchIconKind.Folder : QuickSearchIconKind.Action),
        };

    // "Set volume to 30%": the one action that is given what was typed.
    private static QuickSearchResult WithLevel(QuickActionDefinition definition, int level) =>
        new(ResultId(definition.Id), QuickSearchResultType.Actions, ProviderId,
            string.Create(CultureInfo.InvariantCulture, $"Set volume to {level}%"),
            new QuickSearchAction(
                QuickSearchActionKind.RunAction, QuickSearchActionTitles.Run, definition.Id,
                level.ToString(CultureInfo.InvariantCulture)))
        {
            Subtitle = definition.Description,
            Keywords = definition.Keywords,
            Icon = new QuickSearchIcon(QuickSearchIconKind.Action),
        };

    /// <summary>
    /// The volume level a typed query asks for: a whole number from 0 to 100 (an optional percent sign after it) with a volume word
    /// (<c>volume</c> or <c>vol</c>) and nothing else but filler ("set", "to", "the", "at", "level") around it. <see langword="null"/>
    /// for anything else, so that a number in another query ("volume of a sphere 30") is not a volume.
    /// </summary>
    public static int? ParseVolume(string? typed)
    {
        var words = QuickSearchText.Words(typed);
        if (words.Count is < 2 or > 6)
        {
            return null;
        }

        var volumeWords = 0;
        int? level = null;
        foreach (var word in words)
        {
            if (word is "volume" or "vol")
            {
                volumeWords++;
            }
            else if (word.All(char.IsAsciiDigit) && word.Length is >= 1 and <= 3)
            {
                if (level is not null)
                {
                    return null;
                }

                level = int.Parse(word, CultureInfo.InvariantCulture);
            }
            else if (word is not ("set" or "to" or "the" or "at" or "level" or "change" or "make" or "it" or "percent" or "turn"))
            {
                return null;
            }
        }

        return volumeWords == 1 && level is >= 0 and <= 100 ? level : null;
    }
}
