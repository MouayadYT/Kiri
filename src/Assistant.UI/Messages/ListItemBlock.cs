namespace Assistant.UI.Messages;

/// <summary>One item of a <see cref="ListBlock"/>.</summary>
/// <param name="Marker">What is drawn before the item: a bullet, or its number such as <c>3.</c>.</param>
/// <param name="Text">The item's text. Line breaks inside it are kept.</param>
/// <param name="Depth">How deeply the item is nested, from 0 for the list's own items.</param>
public sealed record ListItemBlock(string Marker, string Text, int Depth) : MessageBlock
{
    /// <summary>The marker of an unordered item.</summary>
    public const string Bullet = "•";
}
