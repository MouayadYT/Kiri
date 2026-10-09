namespace Assistant.UI.Messages;

/// <summary>Puts text on the clipboard, for the copy buttons of message content.</summary>
public interface ITextClipboard
{
    /// <summary>Replaces what is on the clipboard with <paramref name="text"/>.</summary>
    /// <returns><see langword="false"/> when the clipboard stayed busy, such as while another app held it open.</returns>
    bool TrySetText(string text);

    /// <summary>
    /// Replaces what is on the clipboard with the files and folders at <paramref name="paths"/> themselves, as File Explorer's Copy does, so that they can be
    /// pasted into a folder or a message. A clipboard that takes only text says no.
    /// </summary>
    /// <returns><see langword="false"/> when it could not, such as while another app held the clipboard open.</returns>
    bool TrySetFiles(IReadOnlyList<string> paths) => false;
}
