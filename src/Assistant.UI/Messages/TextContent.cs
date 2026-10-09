using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Assistant.UI.Messages;

/// <summary>
/// Prose written by the model, the default content of an assistant message, drawn as open, unboxed text. Its text can
/// change, so an answer can stream into it, and it is read as <see cref="Blocks"/> that follow the text.
/// </summary>
public sealed class TextContent : MessageContent, INotifyPropertyChanged
{
    private readonly ObservableCollection<MessageBlock> _blocks = [];
    private string _text = "";

    /// <summary>Creates prose with <paramref name="text"/>, which may be empty until an answer streams in.</summary>
    public TextContent(string text = "")
    {
        Blocks = new ReadOnlyObservableCollection<MessageBlock>(_blocks);
        Text = text;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The prose, as the model wrote it.</summary>
    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (_text != value)
            {
                _text = value;
                UpdateBlocks();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// The prose as paragraphs, headings and lists (<see cref="MessageTextParser"/>). Only blocks that change are
    /// replaced, so a streaming answer redraws only its end.
    /// </summary>
    public ReadOnlyObservableCollection<MessageBlock> Blocks { get; }

    private void UpdateBlocks()
    {
        var blocks = MessageTextParser.Parse(_text);
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i == _blocks.Count)
            {
                _blocks.Add(blocks[i]);
            }
            else if (!_blocks[i].Equals(blocks[i]))
            {
                _blocks[i] = blocks[i];
            }
        }

        while (_blocks.Count > blocks.Count)
        {
            _blocks.RemoveAt(_blocks.Count - 1);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
