using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Assistant.Core.Domain;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.History;

/// <summary>
/// Turns the messages the conversation shows into the messages history saves, and back (PROJECT_SPEC §3.5). What is kept
/// of a message is its text, its place, its time, how its answer ended, the descriptors of what was attached to it, and
/// the structured parts of an answer that are not prose, as data that draws them again. Nothing captured is kept: an
/// image or a document is a path to the file, a list of files holds no text from them, and an image that only exists in
/// memory is left out.
/// </summary>
/// <remarks>
/// An answer's parts (<see cref="MessageContent"/>) become its text and its cards: the prose parts, joined by blank
/// lines, are the text, and every other part is a card that says how much of the text comes before it, so a card comes
/// back between the paragraphs it was shown between. Two prose parts with no card between them come back as one, which
/// draws the same.
/// </remarks>
internal sealed class MessageMapper(ITextClipboard clipboard)
{
    /// <summary>The separator between an answer's prose parts, which is also the one <see cref="MessageViewModel.Text"/> uses.</summary>
    internal const string ProseSeparator = "\n\n";

    /// <summary>The kinds of card an answer's parts are saved as.</summary>
    internal static class Kinds
    {
        public const string RichAnswerCard = "rich_answer_card";
        public const string CalculationResult = "calculation_result";
        public const string Code = "code";
        public const string ImageGallery = "image_gallery";
        public const string FileList = "file_list";
        public const string ContextWarning = "context_warning";
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Makes the message history saves out of what the conversation shows.</summary>
    public Message ToDomain(MessageViewModel message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role != MessageRole.Assistant)
        {
            return new Message(message.Id, message.Role, message.Text, message.CreatedAt)
            {
                ContextItems = DescribeAttachments(message),
            };
        }

        var text = new StringBuilder();
        var cards = new List<CardMetadata>();
        foreach (var part in message.Content)
        {
            if (part is TextContent prose)
            {
                if (prose.Text.Length > 0)
                {
                    if (text.Length > 0)
                    {
                        text.Append(ProseSeparator);
                    }

                    text.Append(prose.Text);
                }
            }
            else if (Encode(part) is { } card)
            {
                cards.Add(new CardMetadata(card.Kind, card.DataJson, text.Length));
            }
        }

        return new Message(message.Id, MessageRole.Assistant, text.ToString(), message.CreatedAt)
        {
            Cards = cards,
            Outcome = message.Status switch
            {
                MessageStatus.Failed => MessageOutcome.Failed,

                // An answer that is still coming in when it is saved was cut short.
                MessageStatus.Stopped or MessageStatus.Answering => MessageOutcome.Stopped,
                _ => MessageOutcome.Complete,
            },
        };
    }

    /// <summary>Makes the message the conversation shows out of one that history saved.</summary>
    public MessageViewModel ToViewModel(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role != MessageRole.Assistant)
        {
            return new MessageViewModel(
                message.Role, message.Text, message.ContextItems.Select(ToImage).OfType<ImageItem>(),
                documents: message.ContextItems.Select(ToDocument).OfType<DocumentAttachment>())
            {
                Id = message.Id,
                CreatedAt = message.CreatedAt,
            };
        }

        var view = new MessageViewModel(MessageRole.Assistant)
        {
            Id = message.Id,
            CreatedAt = message.CreatedAt,
            Status = message.Outcome switch
            {
                MessageOutcome.Failed => MessageStatus.Failed,
                MessageOutcome.Stopped => MessageStatus.Stopped,
                _ => MessageStatus.Complete,
            },
        };

        var text = message.Text;
        var position = 0;
        foreach (var card in message.Cards)
        {
            // A card sits after the prose that came before it; a bad offset is brought back into the text.
            var offset = Math.Clamp(card.TextOffset, position, text.Length);
            AddProse(view, text, position, offset);
            position = offset;
            if (Decode(card) is { } content)
            {
                view.Content.Add(content);
            }
        }

        AddProse(view, text, position, text.Length);
        return view;
    }

    // The prose between two cards. Every piece but the first begins with the separator that joined it to what came before.
    private static void AddProse(MessageViewModel view, string text, int start, int end)
    {
        var piece = text[start..end];
        if (start > 0 && piece.StartsWith(ProseSeparator, StringComparison.Ordinal))
        {
            piece = piece[ProseSeparator.Length..];
        }

        if (!string.IsNullOrWhiteSpace(piece))
        {
            view.Content.Add(new TextContent(piece));
        }
    }

    /// <summary>
    /// The image a saved descriptor points to, as a message and a conversation's card in the History window show it (named
    /// by its display name, or else its file's name), or <see langword="null"/> for a descriptor that is not an image file.
    /// </summary>
    internal static ImageItem? ToImage(ContextItem? item) =>
        item is { Type: ContextItemType.Image, FilePath: { Length: > 0 } path }
            ? new ImageItem(string.IsNullOrWhiteSpace(item.DisplayName) ? Path.GetFileName(path) : item.DisplayName, path)
            : null;

    /// <summary>
    /// The document a saved descriptor points to, as the message shows it (named by its display name, or else its file's name), or
    /// <see langword="null"/> for a descriptor that is not a file.
    /// </summary>
    internal static DocumentAttachment? ToDocument(ContextItem? item) =>
        item is { Type: ContextItemType.File, FilePath: { Length: > 0 } path }
            ? new DocumentAttachment(string.IsNullOrWhiteSpace(item.DisplayName) ? Path.GetFileName(path) : item.DisplayName, path)
            : null;

    // The descriptors of what was attached to a message: its images that are files, a part of the screen that was captured (the
    // descriptor says that it was a screenshot, and nothing of what it showed), then its documents.
    private static List<ContextItem> DescribeAttachments(MessageViewModel message) =>
        [.. message.Attachments.Where(image => image.Path is not null || image.IsCapture).Select(Describe), .. message.Documents.Select(Describe)];

    private static ContextItem Describe(ImageItem image) => image.IsCapture
        ? new ContextItem(Guid.NewGuid(), ContextItemType.Screenshot, image.Name)
        : new ContextItem(Guid.NewGuid(), ContextItemType.Image, image.Name) { FilePath = image.Path };

    // What is kept of an attached document is its name and where the file is, never any of its text.
    private static ContextItem Describe(DocumentAttachment document) =>
        new(Guid.NewGuid(), ContextItemType.File, document.Name) { FilePath = document.Path };

    // The card a part is saved as, or null for one that has nothing to keep.
    private static (string Kind, string DataJson)? Encode(MessageContent part) => part switch
    {
        CalculationResult calculation => (
            Kinds.CalculationResult,
            Serialize(new CalculationData(calculation.Expression ?? string.Empty, calculation.Result, calculation.Secondary))),
        RichAnswerCard card => (
            Kinds.RichAnswerCard,
            Serialize(new RichCardData(card.Label, card.Expression, card.Result, card.Secondary))),
        CodeContent code => (Kinds.Code, Serialize(new CodeData(code.Code, code.Language))),
        ImageCollection gallery => EncodeImages(gallery),
        FileCollection files => (
            Kinds.FileList,
            Serialize(new FileListData([.. files.Files.Select(file => new FileData(file.Kind, file.Name, file.Path, file.ModifiedAt))]))),
        ContextWarningContent warning when warning.Lines.Count > 0 =>
            (Kinds.ContextWarning, Serialize(new ContextWarningData(warning.Lines))),
        _ => null,
    };

    // Only images that are files can be drawn again; one that exists only in memory has nothing to point to.
    private static (string Kind, string DataJson)? EncodeImages(ImageCollection gallery)
    {
        var images = gallery.Images.Where(image => image.Path is not null)
            .Select(image => new ImageData(image.Name, image.Path!)).ToArray();
        return images.Length == 0 ? null : (Kinds.ImageGallery, Serialize(new ImageGalleryData(images)));
    }

    // The part a card is drawn as, or null for a card of a kind this build does not know or whose data cannot be read.
    private MessageContent? Decode(CardMetadata card)
    {
        try
        {
            switch (card.Kind)
            {
                case Kinds.CalculationResult when Deserialize<CalculationData>(card) is { } calculation:
                    return new CalculationResult(calculation.Expression, calculation.Result, Copy(calculation.Result), calculation.Secondary);
                case Kinds.RichAnswerCard when Deserialize<RichCardData>(card) is { } rich:
                    return new RichAnswerCard(rich.Label, rich.Result, rich.Expression, rich.Secondary, Copy(rich.Result));
                case Kinds.Code when Deserialize<CodeData>(card) is { } code:
                    return new CodeContent(code.Code, code.Language, Copy(code.Code));
                case Kinds.ImageGallery when Deserialize<ImageGalleryData>(card) is { Images.Count: > 0 } gallery:
                    return new ImageCollection(gallery.Images.Select(image => new ImageItem(image.Name, image.Path)));
                case Kinds.FileList when Deserialize<FileListData>(card) is { } files:
                    return new FileCollection(files.Files.Select(file => new FileItem(file.Kind, file.Name, file.Path, file.ModifiedAt)));
                case Kinds.ContextWarning when Deserialize<ContextWarningData>(card) is { Lines.Count: > 0 } warning:
                    return new ContextWarningContent(warning.Lines);
                default:
                    return null;
            }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            // Data that does not describe a card: the card is left out, and the rest of the message stays.
            return null;
        }
    }

    private ICommand Copy(string text) => new CopyTextCommand(clipboard, text);

    private static string Serialize<T>(T data) => JsonSerializer.Serialize(data, Json);

    private static T? Deserialize<T>(CardMetadata card)
        where T : class => JsonSerializer.Deserialize<T>(card.DataJson, Json);

    private sealed record RichCardData(string Label, string? Expression, string Result, string? Secondary);

    private sealed record CalculationData(string Expression, string Result, string? Secondary);

    private sealed record CodeData(string Code, string? Language);

    private sealed record ImageData(string Name, string Path);

    private sealed record ImageGalleryData(IReadOnlyList<ImageData> Images);

    private sealed record FileData(SearchResultItemType Kind, string Name, string Path, DateTimeOffset? ModifiedAt);

    private sealed record FileListData(IReadOnlyList<FileData> Files);

    // The words of a warning hold an item's label, never its content.
    private sealed record ContextWarningData(IReadOnlyList<string> Lines);
}
