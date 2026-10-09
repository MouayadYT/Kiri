using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Assistant.Core.Domain;
using Assistant.UI.Messages;

namespace Assistant.UI.ViewModels;

/// <summary>
/// One message shown in the floating conversation. The user's is their text, shown as typed. The assistant's is an
/// ordered list of typed <see cref="Content"/>, each part drawn according to what produced it: the model's prose
/// unboxed, a tool's result as a card, and so on.
/// </summary>
public sealed class MessageViewModel : INotifyPropertyChanged
{
    private readonly string _text;
    private IReadOnlyList<ImageItem> _attachments;
    private MessageStatus _status;
    private readonly List<INotifyPropertyChanged> _watched = [];
    private bool _detailsOpen;

    /// <summary>
    /// Creates a message by <paramref name="role"/>. An assistant message's <paramref name="text"/>, if any, becomes its
    /// first <see cref="TextContent"/>.
    /// </summary>
    /// <param name="role">Who wrote the message.</param>
    /// <param name="text">What the user typed, or the assistant's first prose.</param>
    /// <param name="attachments">The images the user attached to the message, in the order they were attached.</param>
    /// <param name="document">The document the user attached to the message, or <see langword="null"/> for none.</param>
    /// <param name="texts">The pieces of text the user attached to the message, in the order they were attached.</param>
    /// <param name="documents">
    /// More documents the user attached to the message, after <paramref name="document"/>, in the order they were attached; the same
    /// file twice is kept once.
    /// </param>
    public MessageViewModel(
        MessageRole role, string text = "", IEnumerable<ImageItem>? attachments = null, DocumentAttachment? document = null,
        IEnumerable<TextAttachment>? texts = null, IEnumerable<DocumentAttachment>? documents = null)
    {
        Role = role;
        _text = text ?? "";
        _attachments = attachments?.ToArray() ?? [];
        var attached = new List<DocumentAttachment>();
        foreach (var file in (document is null ? [] : new[] { document }).Concat(documents ?? []))
        {
            if (!attached.Any(known => known.IsSameFile(file)))
            {
                attached.Add(file);
            }
        }

        Documents = attached;
        TextAttachments = texts?.ToArray() ?? [];
        Content.CollectionChanged += OnContentChanged;
        if (role == MessageRole.Assistant && _text.Length > 0)
        {
            Content.Add(new TextContent(_text));
        }
    }

    /// <summary>Who wrote the message, which decides how it is drawn.</summary>
    public MessageRole Role { get; }

    /// <summary>
    /// Which message this is. It is the message's identity when it is saved (PROJECT_SPEC §3.5), so saving it again, as
    /// when its answer ends, changes it where it is instead of adding another.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Whether the user's message was spoken, with the microphone, rather than typed (PROJECT_SPEC §4.2, step 125): the answer to a spoken request is also
    /// read aloud as it is written, and one to a typed request is text only.
    /// </summary>
    public bool IsSpoken { get; init; }

    /// <summary>When the message was made: when the user asked, or when the answer began.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The images the user attached to the message, such as a photo they ask about, in the order they were attached.
    /// Images in an answer are part of its <see cref="Content"/> instead.
    /// </summary>
    public IReadOnlyList<ImageItem> Attachments => _attachments;

    /// <summary>
    /// The first document the user attached to the message, which their question is about, or <see langword="null"/> when there is
    /// none. <see cref="Documents"/> has every one.
    /// </summary>
    public DocumentAttachment? Document => Documents.Count > 0 ? Documents[0] : null;

    /// <summary>
    /// The documents the user attached to the message, which their question is about, in the order they were attached: each drawn
    /// above their bubble by its name, and all read when the question is asked (PROJECT_SPEC §5.5, several files). Empty when there
    /// are none.
    /// </summary>
    public IReadOnlyList<DocumentAttachment> Documents { get; }

    /// <summary>
    /// The pieces of text the user attached to the message, such as a selection their question is about: drawn above their bubble
    /// by their first words, and given to the model with the question. They are never saved. Empty when there are none.
    /// </summary>
    public IReadOnlyList<TextAttachment> TextAttachments { get; }

    /// <summary>
    /// Whether the message has more than two images attached, which are then drawn as a compact grid of small tiles rather than each
    /// at its own size, so that a question about ten pictures does not fill the conversation with them. One or two keep their own size.
    /// </summary>
    public bool HasManyPictures => _attachments.Count > 2;

    /// <summary>Whether the message has an image, a document or a text attached.</summary>
    public bool HasAttachments => _attachments.Count > 0 || Documents.Count > 0 || TextAttachments.Count > 0;

    /// <summary>
    /// Attaches <paramref name="image"/> to the message after it was sent, as when the Assistant asks which image a
    /// question is about; <see cref="Attachments"/> reports the change.
    /// </summary>
    public void Attach(ImageItem image)
    {
        ArgumentNullException.ThrowIfNull(image);
        // A new list, so views bound to the old one see the change.
        _attachments = [.. _attachments, image];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Attachments)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasManyPictures)));
    }

    /// <summary>
    /// The message's plain text: what the user typed, a tool's result, or the assistant's prose parts joined by blank
    /// lines.
    /// </summary>
    public string Text => Role == MessageRole.Assistant
        ? string.Join("\n\n", Content.OfType<TextContent>().Select(part => part.Text).Where(text => text.Length > 0))
        : _text;

    /// <summary>
    /// What an assistant message shows, in order. Parts can be added while it is shown, such as a card after the
    /// prose, or more prose after a card. Other messages have none.
    /// </summary>
    public ObservableCollection<MessageContent> Content { get; } = [];

    /// <summary>
    /// Where an assistant message's answer stands: still coming in, whole, stopped by the user (drawn with a note that
    /// says so) or failed. Other messages are always <see cref="MessageStatus.Complete"/>.
    /// </summary>
    public MessageStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                RaiseActions();
            }
        }
    }

    /// <summary>
    /// The answer's workings, in order: the parts of <see cref="Content"/> that are put away (<see cref="MessageContent.IsTucked"/>), such as the steps
    /// of what the Assistant did and a question the user has answered. They are drawn under the answer when <see cref="IsDetailsOpen"/>.
    /// </summary>
    public ObservableCollection<MessageContent> Details { get; } = [];

    /// <summary>Whether the answer has workings to open: the button with three dots is there then.</summary>
    public bool HasDetails => Details.Count > 0;

    /// <summary>Whether the answer's workings are open under it. The button with three dots opens and closes them.</summary>
    public bool IsDetailsOpen
    {
        get => _detailsOpen;
        set
        {
            if (_detailsOpen != value)
            {
                _detailsOpen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDetailsOpen)));
            }
        }
    }

    /// <summary>
    /// What the copy button under the answer copies: a message the Assistant sent for the user, when the answer is that, and else the answer's words.
    /// </summary>
    public string CopyText =>
        Content.OfType<SentMessageContent>().LastOrDefault(sent => sent.Message.Length > 0)?.Message ?? Text;

    /// <summary>Whether the answer can be copied: it is the Assistant's, it has ended, and there is something to copy.</summary>
    public bool CanCopy => Role == MessageRole.Assistant && _status != MessageStatus.Answering && CopyText.Length > 0;

    /// <summary>Whether anything stands under the answer: the copy button, the button with three dots, or both.</summary>
    public bool HasActions => CanCopy || HasDetails;

    /// <summary>Whether the answer shows anything but its workings: an answer that is only steps so far has nothing to read yet.</summary>
    public bool HasShownContent => Content.Any(part => !part.IsTucked);

    private void OnContentChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Parts that put themselves away later (a question, once answered) say so; the list of workings follows them.
        foreach (var gone in _watched.Where(part => !Content.Contains((MessageContent)part)).ToList())
        {
            gone.PropertyChanged -= OnPartChanged;
            _watched.Remove(gone);
        }

        foreach (var part in Content.OfType<INotifyPropertyChanged>().Where(part => !_watched.Contains(part)).ToList())
        {
            part.PropertyChanged += OnPartChanged;
            _watched.Add(part);
        }

        RefreshDetails();
    }

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MessageContent.IsTucked))
        {
            RefreshDetails();
        }
    }

    private void RefreshDetails()
    {
        var tucked = Content.Where(part => part.IsTucked).ToList();
        for (var index = Details.Count - 1; index >= 0; index--)
        {
            if (!tucked.Contains(Details[index]))
            {
                Details.RemoveAt(index);
            }
        }

        for (var index = 0; index < tucked.Count; index++)
        {
            if (index >= Details.Count || !ReferenceEquals(Details[index], tucked[index]))
            {
                Details.Remove(tucked[index]);
                Details.Insert(Math.Min(index, Details.Count), tucked[index]);
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetails)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasShownContent)));
        RaiseActions();
    }

    private void RaiseActions()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCopy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActions)));
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
}
