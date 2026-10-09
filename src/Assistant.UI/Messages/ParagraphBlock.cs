namespace Assistant.UI.Messages;

/// <summary>A paragraph of plain text. Line breaks inside it are kept.</summary>
/// <param name="Text">The paragraph's text.</param>
public sealed record ParagraphBlock(string Text) : MessageBlock;
