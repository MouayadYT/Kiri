namespace Assistant.Search.Index;

/// <summary>
/// The Windows properties every query selects, in the order a row holds them. <see cref="SearchSqlBuilder"/> writes them
/// into the query and <see cref="SearchRowMapper"/> reads them back by these positions.
/// </summary>
internal static class SearchColumns
{
    public const int Path = 0;
    public const int Name = 1;
    public const int Extension = 2;
    public const int Size = 3;
    public const int Modified = 4;
    public const int Created = 5;
    public const int Accessed = 6;
    public const int Attributes = 7;
    public const int IsFolder = 8;
    public const int TypeText = 9;
    public const int Kind = 10;
    public const int MimeType = 11;
    public const int Title = 12;
    public const int Authors = 13;
    public const int Keywords = 14;

    /// <summary>The excerpt of the item's text. Selected only when the query matches contents, and then last.</summary>
    public const int Summary = 15;

    /// <summary>The property names, by position.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "System.ItemPathDisplay",
        "System.ItemNameDisplay",
        "System.FileExtension",
        "System.Size",
        "System.DateModified",
        "System.DateCreated",
        "System.DateAccessed",
        "System.FileAttributes",
        "System.IsFolder",
        "System.ItemTypeText",
        "System.Kind",
        "System.MIMEType",
        "System.Title",
        "System.Author",
        "System.Keywords",
        "System.Search.AutoSummary",
    ];

    /// <summary>How many columns a query selects.</summary>
    public static int CountFor(bool withSummary) => withSummary ? Names.Count : Names.Count - 1;
}
