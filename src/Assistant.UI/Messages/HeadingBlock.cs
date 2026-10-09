namespace Assistant.UI.Messages;

/// <summary>A heading, such as a Markdown <c># Title</c>.</summary>
/// <param name="Level">Its level, from 1 (the largest) to 6.</param>
/// <param name="Text">The heading's text.</param>
public sealed record HeadingBlock(int Level, string Text) : MessageBlock;
