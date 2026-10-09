using System.Text;
using System.Windows.Threading;

namespace Assistant.UI.Messages;

/// <summary>
/// Streams prose into a <see cref="TextContent"/> as the model writes it, without redrawing it for every piece: pieces
/// gather here and are shown together once the dispatcher has nothing more urgent to do, so input and rendering always
/// come first, and however fast the model writes, the prose is read into blocks and laid out at most once per turn of
/// the dispatcher. It belongs to the UI thread.
/// </summary>
public sealed class StreamingText
{
    private readonly TextContent _target;
    private readonly Dispatcher _dispatcher;
    private readonly StringBuilder _text;
    private bool _scheduled;

    /// <summary>Streams into <paramref name="target"/>, after the text it already has.</summary>
    public StreamingText(TextContent target, Dispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _text = new StringBuilder(target.Text);
    }

    /// <summary>Whether pieces have arrived that are not shown yet.</summary>
    public bool IsPending => _text.Length != _target.Text.Length;

    /// <summary>Adds <paramref name="piece"/> to the prose; it shows with the pieces around it, shortly.</summary>
    public void Append(string piece)
    {
        _dispatcher.VerifyAccess();
        if (string.IsNullOrEmpty(piece))
        {
            return;
        }

        _text.Append(piece);
        if (!_scheduled)
        {
            _scheduled = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, ShowGathered);
        }
    }

    /// <summary>Shows every piece that has arrived, at once.</summary>
    public void Flush()
    {
        _dispatcher.VerifyAccess();
        if (IsPending)
        {
            _target.Text = _text.ToString();
        }
    }

    private void ShowGathered()
    {
        _scheduled = false;
        Flush();
    }
}
