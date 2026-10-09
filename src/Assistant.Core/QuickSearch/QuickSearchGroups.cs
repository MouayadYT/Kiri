namespace Assistant.Core.QuickSearch;

/// <summary>
/// The groups quick-search results are listed in (PROJECT_SPEC §4.1): their order, their names and how many of each are listed. A
/// new type of result is given its place here.
/// </summary>
public static class QuickSearchGroups
{
    /// <summary>The order groups are listed in: applications, files, actions, then the clipboard.</summary>
    public static IReadOnlyList<QuickSearchResultType> Order { get; } =
    [
        QuickSearchResultType.Applications, QuickSearchResultType.Files, QuickSearchResultType.Actions, QuickSearchResultType.Clipboard,
    ];

    /// <summary>The header words of the group of <paramref name="type"/>.</summary>
    public static string TitleOf(QuickSearchResultType type) => type switch
    {
        QuickSearchResultType.Applications => "Applications",
        QuickSearchResultType.Files => "Files",
        QuickSearchResultType.Actions => "Actions",
        QuickSearchResultType.Clipboard => "Clipboard",
        _ => type.ToString(),
    };

    /// <summary>
    /// The most results listed from the group of <paramref name="type"/> while every group is listed at once: a glance at each. Files
    /// take five files and three folders, as the bar has always listed them.
    /// </summary>
    public static int GlanceLimit(QuickSearchResultType type) => type switch
    {
        QuickSearchResultType.Applications => 5,
        QuickSearchResultType.Files => 8,
        QuickSearchResultType.Actions => 4,
        QuickSearchResultType.Clipboard => 4,
        _ => 5,
    };

    /// <summary>The most results listed from a group the user has narrowed the list to.</summary>
    public const int ScopedLimit = 20;

    /// <summary>The place of <paramref name="type"/> in <see cref="Order"/>; a type without one comes last, in the order of its value.</summary>
    public static int PlaceOf(QuickSearchResultType type)
    {
        for (var i = 0; i < Order.Count; i++)
        {
            if (Order[i] == type)
            {
                return i;
            }
        }

        return Order.Count + (int)type;
    }
}
