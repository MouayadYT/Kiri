namespace Assistant.UI.Messages;

/// <summary>
/// A bulleted or numbered list. Nested items follow their parent in <see cref="Items"/> with a greater
/// <see cref="ListItemBlock.Depth"/>, so each item carries its own marker.
/// </summary>
public sealed record ListBlock : MessageBlock
{
    /// <summary>Creates a list of <paramref name="items"/>, in order.</summary>
    public ListBlock(IReadOnlyList<ListItemBlock> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    /// <summary>The list's items, in order, nested items included.</summary>
    public IReadOnlyList<ListItemBlock> Items { get; }

    /// <summary>Lists are equal when their items are.</summary>
    public bool Equals(ListBlock? other) => other is not null && Items.SequenceEqual(other.Items);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}
