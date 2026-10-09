using System.Windows.Input;

namespace Assistant.UI.Messages;

/// <summary>
/// A fenced block of code inside an answer's prose (<c>```</c> ... <c>```</c>), shown monospaced on a dark surface with a
/// copy button. Unlike <see cref="CodeContent"/>, which a tool produces, it comes from the model's own text.
/// </summary>
/// <param name="Code">The code as written, without the fences and the line break after its last line.</param>
/// <param name="Language">The name after the opening fence, such as <c>csharp</c>, or <see langword="null"/>.</param>
public sealed record CodeBlock(string Code, string? Language) : MessageBlock
{
    /// <summary>
    /// Copies the code, given as the command's parameter, to the clipboard. A conversation view handles it, so the
    /// copy button of any code block works wherever the conversation is shown.
    /// </summary>
    public static RoutedUICommand CopyCommand { get; } = new("Copy code", nameof(CopyCommand), typeof(CodeBlock));
}
