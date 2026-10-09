using System.Text;

namespace Assistant.Core.QuickSearch;

/// <summary>What a result's icon shows when it has no picture of its own.</summary>
public enum QuickSearchIconKind
{
    /// <summary>An application.</summary>
    Application = 0,

    /// <summary>A file.</summary>
    File = 1,

    /// <summary>A folder.</summary>
    Folder = 2,

    /// <summary>An action.</summary>
    Action = 3,

    /// <summary>Copied text.</summary>
    Clipboard = 4,
}

/// <summary>
/// The picture at the left of a result: the kind's own icon, or, for something with an icon of its own such as an application, that
/// picture as PNG bytes. It holds no WPF type, so the module that finds a result never needs the UI.
/// </summary>
/// <param name="Kind">What it stands for, which decides the icon drawn when there is no picture.</param>
/// <param name="Image">A PNG, or <see langword="null"/> to draw the kind's own icon.</param>
public sealed record QuickSearchIcon(QuickSearchIconKind Kind, byte[]? Image = null)
{
    // Not the bytes: they are a picture of something the user has.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, HasImage = {Image is not null}");
        return true;
    }
}

/// <summary>
/// One result of a quick search (PROJECT_SPEC §4.1): what a provider found, as data. The title, the subtitle, the keywords and the
/// actions' targets are private content (names, paths, copied text), so nothing here is ever printed or logged.
/// </summary>
/// <param name="Id">
/// A stable identity: the same thing is always the same id, so what the user opened before can be recognised again
/// (<c>app:...</c>, <c>file:...</c>, <c>action:...</c>, <c>clip:...</c>).
/// </param>
/// <param name="ResultType">What it is, which is also the group it is listed in.</param>
/// <param name="ProviderId">The <see cref="IQuickSearchProvider.Id"/> that found it.</param>
/// <param name="Title">Its name, which the query is matched against.</param>
/// <param name="Primary">What pressing Enter on it does.</param>
public sealed record QuickSearchResult(
    string Id, QuickSearchResultType ResultType, string ProviderId, string Title, QuickSearchAction Primary)
{
    /// <summary>A second line under the name, such as the folder a file is in; empty for none.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>When the result's item last changed, which the list writes the way a message's date is written; <see langword="null"/> for none.</summary>
    public DateTimeOffset? When { get; init; }

    /// <summary>A note at the right of the name, such as a status word, drawn in place of <see cref="When"/>; empty for none.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The picture at the left.</summary>
    public QuickSearchIcon Icon { get; init; } = new(QuickSearchIconKind.Application);

    /// <summary>Other names the query is matched against ("settings" for "Control Panel"), counted a little below the title.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// Other names the result is called by, counted as its title is: "Kiri" for the Assistant, which Start lists as "Assistant". Unlike a keyword, an alias
    /// can be an exact match, so what is typed to find the result by it puts it first.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>
    /// What else can be done with the result, in the order they are listed when the user asks for the alternate actions (Tab). The
    /// default row never shows them.
    /// </summary>
    public IReadOnlyList<QuickSearchAction> Alternates { get; init; } = [];

    /// <summary>
    /// How relevant the provider itself finds the result among its own, from 0 to 1 (the first of an index's answers is the most
    /// relevant). Used only to order results the ranking cannot tell apart.
    /// </summary>
    public double Relevance { get; init; }

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ResultType = {ResultType}, ProviderId = {ProviderId}");
        return true;
    }
}
