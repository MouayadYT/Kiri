using System.Windows.Input;

namespace Assistant.UI.Messages;

/// <summary>
/// Copies one piece of a message, such as a result or a block of code, to the clipboard. It can always run while
/// there is text to copy.
/// </summary>
public sealed class CopyTextCommand : ICommand
{
    private readonly ITextClipboard _clipboard;
    private readonly string _text;

    /// <summary>Creates a command that puts <paramref name="text"/> on <paramref name="clipboard"/>.</summary>
    public CopyTextCommand(ITextClipboard clipboard, string text)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(text);
        _clipboard = clipboard;
        _text = text;
    }

    // Whether the command can run never changes.
    event EventHandler? ICommand.CanExecuteChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => _text.Length > 0;

    /// <inheritdoc/>
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _clipboard.TrySetText(_text);
        }
    }
}
