using System.Collections;
using System.Windows.Data;

namespace Assistant.UI.Controls;

/// <summary>
/// Orders the groups of a grouped collection view by their names' own order, such as the History window's sections by
/// recency, rather than by when each group first appeared. Set it as a group description's
/// <see cref="GroupDescription.CustomSort"/>; the names must compare with each other.
/// </summary>
public sealed class GroupNameOrder : IComparer
{
    /// <summary>The one instance; it keeps no state.</summary>
    public static GroupNameOrder Instance { get; } = new();

    /// <inheritdoc/>
    public int Compare(object? x, object? y) =>
        Comparer.Default.Compare((x as CollectionViewGroup)?.Name ?? x, (y as CollectionViewGroup)?.Name ?? y);
}
