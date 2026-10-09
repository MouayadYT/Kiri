namespace Assistant.Core.Contracts;

/// <summary>
/// The order a file search returns its results in. Every order other than <see cref="Relevance"/> is fixed by the item's own
/// property, with the path deciding between items that are equal, so the same request gives the same list every time
/// ("the last 5 screenshots" is <see cref="ModifiedDescending"/> with a limit of five).
/// </summary>
public enum FileSearchOrder
{
    /// <summary>
    /// The best match first: a name that is what was typed, or begins with it, before one that only contains it, and among
    /// equals the index's own order. When the query says nothing in words this is newest first.
    /// </summary>
    Relevance = 0,

    /// <summary>Most recently modified first.</summary>
    ModifiedDescending = 1,

    /// <summary>Least recently modified first.</summary>
    ModifiedAscending = 2,

    /// <summary>Most recently created first.</summary>
    CreatedDescending = 3,

    /// <summary>Least recently created first.</summary>
    CreatedAscending = 4,

    /// <summary>By name, A to Z.</summary>
    NameAscending = 5,

    /// <summary>By name, Z to A.</summary>
    NameDescending = 6,

    /// <summary>Largest first. A folder has no size and comes last.</summary>
    SizeDescending = 7,

    /// <summary>Smallest first. A folder has no size and comes last.</summary>
    SizeAscending = 8,
}
