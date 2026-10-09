using System.IO;
using System.Windows.Input;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.UI.Messages;

/// <summary>
/// What a user can do with a file in an answer (PROJECT_SPEC §4.2): open it, show it in File Explorer, or attach it to the
/// conversation. They are routed commands, like the copy button of a block of code, and the parameter is the item itself, a
/// <see cref="FileItem"/> or an <see cref="ImageItem"/>, so they work on a result that was just found and on one that is
/// drawn again from the saved history. The conversation view handles them (<c>ConversationView</c>).
/// </summary>
public static class FileActions
{
    /// <summary>Opens the file or folder with its default handler, as a double click in File Explorer does.</summary>
    public static readonly RoutedUICommand Open = new("Open", nameof(Open), typeof(FileActions));

    /// <summary>Shows the file or folder selected in a File Explorer window.</summary>
    public static readonly RoutedUICommand Reveal = new("Show in Explorer", nameof(Reveal), typeof(FileActions));

    /// <summary>Attaches the image, or the document the Assistant reads, to the conversation, so the next question is asked about it.</summary>
    public static readonly RoutedUICommand Attach = new("Attach to conversation", nameof(Attach), typeof(FileActions));

    /// <summary>The path of the file or folder a command's <paramref name="item"/> is, or <see langword="null"/> when it is not one.</summary>
    public static string? PathOf(object? item) => item switch
    {
        FileItem { Kind: not SearchResultItemType.App, Path.Length: > 0 } file => file.Path,
        ImageItem { Path: { Length: > 0 } path } => path,
        _ => null,
    };

    /// <summary>
    /// The image an item is, for attaching it: an image of the gallery as it is, or a file whose type is an image. Anything
    /// else (a document, which <see cref="DocumentOf"/> takes, a folder, an image that only exists in memory) is not one.
    /// </summary>
    public static ImageItem? ImageOf(object? item) => item switch
    {
        ImageItem { Path: not null } image => image,
        FileItem { Kind: SearchResultItemType.File } file when ImageFileTypes.IsImageExtension(Path.GetExtension(file.Path))
            => new ImageItem(file.Name, file.Path),
        _ => null,
    };

    /// <summary>
    /// The document an item is, for attaching it: a file whose type the Assistant reads the text of (<see cref="DocumentFileTypes"/>:
    /// text, Markdown, PDF, Word and PowerPoint). Any other file, a folder and an image are not one.
    /// </summary>
    public static DocumentAttachment? DocumentOf(object? item) => item switch
    {
        FileItem { Kind: SearchResultItemType.File } file when DocumentFileTypes.IsDocument(file.Path)
            => new DocumentAttachment(Path.GetFileName(file.Path) is { Length: > 0 } name ? name : file.Name, file.Path),
        _ => null,
    };

    /// <summary>
    /// The one document <paramref name="message"/> lists, when it is an answer that lists exactly one file and the Assistant can
    /// read it; otherwise <see langword="null"/>.
    /// </summary>
    public static DocumentAttachment? SingleDocumentIn(ViewModels.MessageViewModel? message)
    {
        if (message is not { Role: MessageRole.Assistant })
        {
            return null;
        }

        var files = message.Content.OfType<FileCollection>().SelectMany(collection => collection.Files).Take(2).ToArray();
        return files.Length == 1 ? DocumentOf(files[0]) : null;
    }
}
