using System.Windows.Input;

namespace Assistant.UI.Messages;

/// <summary>
/// A block of code in an answer, drawn as code: monospaced, with its lines and indentation as written, on a surface
/// of its own with a copy button. It is not a card, and prose is never drawn this way.
/// </summary>
public sealed class CodeContent : MessageContent
{
    /// <summary>How many columns apart tab stops are when code is drawn.</summary>
    public const int TabSize = 4;

    /// <summary>Creates a block of <paramref name="code"/>.</summary>
    /// <param name="code">The code, exactly as it should be shown and copied.</param>
    /// <param name="language">The language's name as it should be shown, such as "C#", or <see langword="null"/>.</param>
    /// <param name="copyCommand">Copies the code; without one the block has no copy button.</param>
    public CodeContent(string code, string? language = null, ICommand? copyCommand = null)
    {
        ArgumentNullException.ThrowIfNull(code);

        // Trailing line breaks would only add empty lines at the bottom of the block.
        Code = code.ReplaceLineEndings("\n").TrimEnd('\n');
        DisplayCode = ExpandTabs(Code);
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        CopyCommand = copyCommand;
    }

    /// <summary>The code, with <c>\n</c> line breaks.</summary>
    public string Code { get; }

    /// <summary>The code as it is drawn: the same, with each tab widened to the next tab stop.</summary>
    public string DisplayCode { get; }

    /// <summary>The language's name, shown above the code, or <see langword="null"/>.</summary>
    public string? Language { get; }

    /// <summary>Copies the code, from the block's copy button; <see langword="null"/> when it cannot be copied.</summary>
    public ICommand? CopyCommand { get; }

    /// <summary>A block of code reaches as far as a card, so its text lines up with the prose around it.</summary>
    public override bool IsWide => true;

    private static string ExpandTabs(string code)
    {
        if (!code.Contains('\t'))
        {
            return code;
        }

        var text = new System.Text.StringBuilder(code.Length + 16);
        var column = 0;
        foreach (var character in code)
        {
            switch (character)
            {
                case '\t':
                    var spaces = TabSize - column % TabSize;
                    text.Append(' ', spaces);
                    column += spaces;
                    break;
                case '\n':
                    text.Append(character);
                    column = 0;
                    break;
                default:
                    text.Append(character);
                    column++;
                    break;
            }
        }

        return text.ToString();
    }
}
