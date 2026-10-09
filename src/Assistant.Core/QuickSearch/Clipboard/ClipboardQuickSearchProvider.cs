using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Core.QuickSearch.Clipboard;

/// <summary>
/// Finds text the user copied, in the short clipboard history the Assistant keeps itself (<see cref="IClipboardHistory"/>), for what is
/// being typed (PROJECT_SPEC §4.1). It never reads the clipboard or Windows' own clipboard history: it only looks at what the app
/// kept, and finds nothing while the user has not allowed the history (Clipboard History permission), whatever the history holds. A
/// result shows the start of the text on one line and when it was copied; choosing it puts the text back on the clipboard, and its
/// alternates attach the text to a new conversation or take it out of the history. With nothing typed it lists the newest items. What
/// is copied and what is typed are private content and are never logged.
/// </summary>
public sealed class ClipboardQuickSearchProvider : IQuickSearchProvider
{
    /// <summary>The provider's identity.</summary>
    public const string ProviderId = "clipboard";

    /// <summary>The longest preview of a copied text, in characters, before it is cut with an ellipsis.</summary>
    public const int PreviewLength = 90;

    private readonly IClipboardHistory _history;
    private readonly IPermissionPolicy? _permissions;

    /// <summary>Creates the provider over <paramref name="history"/>.</summary>
    /// <param name="history">What the app kept of what the user copied.</param>
    /// <param name="permissions">Says whether the user allows the history; without it nothing is listed.</param>
    public ClipboardQuickSearchProvider(IClipboardHistory history, IPermissionPolicy? permissions = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _permissions = permissions;
    }

    /// <inheritdoc/>
    public string Id => ProviderId;

    /// <inheritdoc/>
    public string DisplayName => "Clipboard";

    /// <inheritdoc/>
    public QuickSearchResultType ResultType => QuickSearchResultType.Clipboard;

    /// <inheritdoc/>
    public int Priority => 40;

    /// <inheritdoc/>
    public int MinimumQueryLength => 0;

    /// <summary>Nothing: the history is in memory.</summary>
    public TimeSpan Debounce => TimeSpan.Zero;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QuickSearchResult>> SearchAsync(QuickSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_history.IsEnabled || _permissions is null
            || !(await _permissions.CheckAsync(PermissionCapability.ClipboardHistory, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return [];
        }

        var words = QuickSearchText.Words(request.Query);
        var items = _history.Items;
        var matching = items
            .Where(item => words.Count == 0 || Holds(item.Text, words))
            .Take(request.MaxResults)
            .ToArray();
        return [.. matching.Select((item, index) => Result(item, 1.0 - ((double)index / Math.Max(1, matching.Length))))];
    }

    /// <summary>The id of the result for the history item <paramref name="itemId"/>.</summary>
    public static string ResultId(string itemId) => "clip:" + itemId;

    /// <summary>The text on one line, cut to <see cref="PreviewLength"/> characters with an ellipsis, with every run of white space as one space.</summary>
    public static string Preview(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, PreviewLength + 1));
        var gap = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                gap = builder.Length > 0;
                continue;
            }

            if (gap)
            {
                builder.Append(' ');
                gap = false;
            }

            builder.Append(character);
            if (builder.Length > PreviewLength)
            {
                return builder.ToString(0, PreviewLength).TrimEnd() + "...";
            }
        }

        return builder.ToString();
    }

    // Each typed word is somewhere in the text, whatever the case or accents.
    private static bool Holds(string text, IReadOnlyList<string> words)
    {
        var normalized = QuickSearchText.Normalize(text);
        return words.All(word => normalized.Contains(word, StringComparison.Ordinal));
    }

    private static QuickSearchResult Result(ClipboardHistoryItem item, double relevance) =>
        new(ResultId(item.Id), QuickSearchResultType.Clipboard, ProviderId, Preview(item.Text),
            new QuickSearchAction(QuickSearchActionKind.CopyClipboardItem, QuickSearchActionTitles.Copy, item.Id))
        {
            When = item.CopiedAt,
            Icon = new QuickSearchIcon(QuickSearchIconKind.Clipboard),

            // The whole text is what a typed word is looked for in, whatever part of it the row shows.
            Keywords = [item.Text],
            Alternates =
            [
                new QuickSearchAction(QuickSearchActionKind.AttachClipboardItem, QuickSearchActionTitles.Attach, item.Id),
                new QuickSearchAction(QuickSearchActionKind.RemoveClipboardItem, QuickSearchActionTitles.RemoveFromHistory, item.Id),
            ],
            Relevance = relevance,
        };
}
