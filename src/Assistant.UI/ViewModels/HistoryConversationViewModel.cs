using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Assistant.Core.Domain;
using Assistant.Core.History;
using Assistant.UI.Messages;

namespace Assistant.UI.ViewModels;

/// <summary>
/// One conversation in the History window (PROJECT_SPEC §4.3): its card in the list, with when it last changed, its
/// title, a preview of the latest answer or the image most recently attached to it, and its messages for the workspace.
/// </summary>
/// <remarks>
/// A conversation from the saved history is listed without its messages, which is all a card needs, and they are loaded
/// when it is opened (<see cref="IsLoaded"/>). While a search shows it as a result, its card shows the words around what
/// matched instead of the latest answer.
/// </remarks>
public sealed class HistoryConversationViewModel : INotifyPropertyChanged
{
    private readonly TimeProvider _clock;
    private string? _title;
    private string? _latestAnswerText;
    private ObservableCollection<MessageViewModel> _messages = [];
    private ImageItem? _listedImage;
    private DateTimeOffset _updatedAt;
    private string? _searchSnippet;
    private IReadOnlyList<TextMatch> _searchMatches = [];
    private bool _isShown = true;
    private bool _isLoaded;

    /// <summary>Creates the conversation <paramref name="id"/> with <paramref name="messages"/>, oldest first.</summary>
    /// <param name="id">Which conversation it is.</param>
    /// <param name="messages">Its messages, oldest first.</param>
    /// <param name="updatedAt">When it last changed.</param>
    /// <param name="clock">The clock its card's time is read against.</param>
    /// <param name="title">Its title, or <see langword="null"/> for a provisional one from what the user first asked.</param>
    public HistoryConversationViewModel(
        Guid id, IEnumerable<MessageViewModel> messages, DateTimeOffset updatedAt, TimeProvider clock,
        string? title = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Id = id;
        _clock = clock;
        _title = TitleOf(title);
        Update(messages, updatedAt);
    }

    /// <summary>
    /// Creates a conversation from what the History window's source lists: with its messages when it has them, and
    /// otherwise as a card that shows its title, time, preview and image, its messages loading when it is opened.
    /// </summary>
    public HistoryConversationViewModel(HistoryConversation item, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(clock);
        Id = item.Id;
        _clock = clock;
        _title = TitleOf(item.Title);
        if (item.Messages is { } messages)
        {
            Update(messages, item.UpdatedAt);
        }
        else
        {
            _updatedAt = item.UpdatedAt;
            _latestAnswerText = item.LatestAnswerText;
            _listedImage = item.Image;
            Refresh();
        }
    }

    /// <summary>What the card of a conversation says while nothing has been asked in it yet.</summary>
    public const string NewConversationTitle = "New conversation";

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Which conversation this is.</summary>
    public Guid Id { get; }

    /// <summary>
    /// The conversation's messages, oldest first, drawn in the workspace as in the floating conversation. Messages added
    /// while the conversation goes on appear in it as they come. Empty until <see cref="IsLoaded"/>.
    /// </summary>
    public IReadOnlyList<MessageViewModel> Messages => _messages;

    /// <summary>
    /// Whether the conversation's messages are here. A conversation that came from the saved history is listed without
    /// them, and they load when it is opened; one that was started or continued in this session has them from the start.
    /// </summary>
    public bool IsLoaded => _isLoaded;

    /// <summary>Whether the conversation's messages are being read from the saved history.</summary>
    public bool IsLoading { get; internal set; }

    /// <summary>When the conversation last changed.</summary>
    public DateTimeOffset UpdatedAt => _updatedAt;

    /// <summary>
    /// The conversation's title: the one it was given, or else a provisional one from what the user first asked, on one
    /// line and cut short when it is long. A better title, such as one the local model writes, replaces it later.
    /// </summary>
    public string Title { get; private set; } = "";

    /// <summary>
    /// What the card shows under the title: the words around what a search matched, while the conversation is one of its
    /// results, and otherwise the latest answer's prose on one line. It is empty when there is nothing to show.
    /// </summary>
    public string Preview { get; private set; } = "";

    /// <summary>Where the searched words are in <see cref="Preview"/>; empty unless the conversation is a search result.</summary>
    public IReadOnlyList<TextMatch> PreviewMatches { get; private set; } = [];

    /// <summary>
    /// The image most recently attached to the conversation, in whichever message, such as a photo the user asked
    /// about, which its card shows in place of the preview; or <see langword="null"/> when no image was attached.
    /// Pictures in answers do not count. While a search shows the conversation as a result, the card shows the matching
    /// words instead, so the image is left out.
    /// </summary>
    public ImageItem? Image { get; private set; }

    /// <summary>
    /// When the conversation last changed, as its card shows it: the time for today, "Yesterday", the day of the week
    /// within the last week, and otherwise the date.
    /// </summary>
    public string TimeLabel => FormatTime(_updatedAt, _clock.GetLocalNow());

    /// <summary>The span of time the list groups the conversation under, such as Today or Previous 7 Days.</summary>
    public HistorySection Section { get; private set; } = HistorySection.Today;

    /// <summary>
    /// Whether the sidebar lists the conversation. It is, unless a search is on and the conversation is not one of its
    /// results.
    /// </summary>
    public bool IsShown
    {
        get => _isShown;
        internal set
        {
            if (_isShown != value)
            {
                _isShown = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The message the search's snippet comes from, or <see langword="null"/>.</summary>
    public Guid? MatchedMessageId { get; private set; }

    /// <summary>Replaces the conversation's messages, such as when it is opened again after it went on.</summary>
    public void Update(IEnumerable<MessageViewModel> messages, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = new ObservableCollection<MessageViewModel>(messages);
        _updatedAt = updatedAt;
        _isLoaded = true;
        IsLoading = false;
        Refresh();
    }

    /// <summary>
    /// Fills in the messages of a conversation that was listed without them, once they are read from the saved history.
    /// The conversation keeps the time it was listed with.
    /// </summary>
    internal void Load(IEnumerable<MessageViewModel> messages) => Update(messages, _updatedAt);

    /// <summary>
    /// Brings a conversation that is already listed up to date with the saved history's latest listing of it. One whose
    /// messages are here keeps them, and its own preview and image, which come from them; it takes the title.
    /// </summary>
    internal void ApplyListing(HistoryConversation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _title = TitleOf(item.Title);
        if (!_isLoaded)
        {
            _updatedAt = item.UpdatedAt;
            _latestAnswerText = item.LatestAnswerText;
            _listedImage = item.Image;
        }

        Refresh();
    }

    /// <summary>Shows the words around what a search matched, in place of the preview, for as long as the search is on.</summary>
    internal void ShowMatch(string? snippet, IReadOnlyList<TextMatch>? matches, Guid? messageId)
    {
        _searchSnippet = string.IsNullOrEmpty(snippet) ? null : snippet;
        _searchMatches = _searchSnippet is null ? [] : matches ?? [];
        MatchedMessageId = _searchSnippet is null ? null : messageId;
        Refresh();
    }

    /// <summary>Goes back to showing the latest answer.</summary>
    internal void ClearMatch()
    {
        if (_searchSnippet is null && MatchedMessageId is null)
        {
            return;
        }

        _searchSnippet = null;
        _searchMatches = [];
        MatchedMessageId = null;
        Refresh();
    }

    /// <summary>
    /// Adds the next message of the conversation as it goes on, such as the user's follow-up or the answer to it, which
    /// appears in <see cref="Messages"/> at once. Call <see cref="Refresh"/> when the answer has been written, so the
    /// card shows it.
    /// </summary>
    public void Add(MessageViewModel message, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
        _updatedAt = updatedAt;
        Refresh();
    }

    /// <summary>Reads the conversation's title, preview, image and section again from its messages or its listing.</summary>
    public void Refresh()
    {
        if (_isLoaded)
        {
            Title = _title ?? (_messages.Count == 0
                ? NewConversationTitle
                : ConversationTitle.Provisional(_messages.FirstOrDefault(message => message.Role == MessageRole.User)?.Text));
            Image = _messages.SelectMany(message => message.Attachments).LastOrDefault();
            var answer = _messages.Where(message => message.Role == MessageRole.Assistant)
                .Select(message => ConversationTitle.OneLine(ProseOf(message.Content.OfType<TextContent>())))
                .LastOrDefault(text => text.Length > 0) ?? "";
            SetPreview(answer);
        }
        else
        {
            Title = _title ?? "";
            Image = _listedImage;
            SetPreview(_latestAnswerText is null
                ? ""
                : ConversationTitle.OneLine(ProseOf([new TextContent(_latestAnswerText)])));
        }

        Section = HistorySection.For(_updatedAt, _clock.GetLocalNow());
        OnPropertyChanged(null);
    }

    // What the card shows under the title: the search's words, or the latest answer.
    private void SetPreview(string answerPreview)
    {
        if (_searchSnippet is not null)
        {
            Preview = _searchSnippet;
            PreviewMatches = _searchMatches;

            // The matching words take the place of the picture, so the reader sees why the conversation was found.
            Image = null;
        }
        else
        {
            Preview = answerPreview;
            PreviewMatches = [];
        }
    }

    /// <summary>
    /// Reads <see cref="TimeLabel"/> again, as the day it names may have changed, and <see cref="Section"/> if the
    /// conversation now belongs under another header.
    /// </summary>
    public void RefreshTime()
    {
        OnPropertyChanged(nameof(TimeLabel));
        var section = HistorySection.For(_updatedAt, _clock.GetLocalNow());
        if (section != Section)
        {
            Section = section;
            OnPropertyChanged(nameof(Section));
        }
    }

    /// <summary>Formats when a conversation changed, <paramref name="at"/>, as seen at <paramref name="now"/>.</summary>
    internal static string FormatTime(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToOffset(now.Offset);
        var days = (now.Date - local.Date).Days;
        return days switch
        {
            <= 0 => local.ToString("t", CultureInfo.CurrentCulture),
            1 => "Yesterday",
            < 7 => local.ToString("dddd", CultureInfo.CurrentCulture),
            _ => local.ToString("d", CultureInfo.CurrentCulture),
        };
    }

    // The words of an answer's prose, without the marks that make its headings, lists and emphasis; code is left out.
    private static string ProseOf(IEnumerable<TextContent> parts)
    {
        var text = new StringBuilder();
        foreach (var block in parts.SelectMany(prose => prose.Blocks))
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    text.Append(InlineMarkdown.ToPlainText(paragraph.Text)).Append(' ');
                    break;
                case HeadingBlock heading:
                    text.Append(InlineMarkdown.ToPlainText(heading.Text)).Append(' ');
                    break;
                case ListBlock list:
                    foreach (var item in list.Items) text.Append(InlineMarkdown.ToPlainText(item.Text)).Append(' ');
                    break;
            }
        }

        return text.ToString();
    }

    // A title given to the conversation, on one line; none when it is blank.
    private static string? TitleOf(string? title) =>
        string.IsNullOrWhiteSpace(title) ? null : ConversationTitle.OneLine(title);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
