using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Assistant.Core.Domain;
using Assistant.Core.History;
using Assistant.UI.Capture;
using Assistant.UI.Messages;

namespace Assistant.UI.ViewModels;

/// <summary>
/// View model for the History window (PROJECT_SPEC §4.3): its list of conversations, newest first, how the sidebar
/// shows them, the one open in its workspace, and its search. The list comes from an <see cref="IHistorySource"/>, the
/// local history, without the messages, which load when a conversation is opened; it also gains the conversations opened
/// in the window from the floating conversation.
/// </summary>
/// <remarks>
/// The search reads the same local history and shows its results in the sidebar, where the conversations that do not
/// match are left out and each result shows the words that matched. It never asks a model.
/// </remarks>
public sealed class HistoryViewModel : INotifyPropertyChanged
{
    /// <summary>How long the search waits after a key, by default, so it runs for what was typed and not for each letter.</summary>
    public static readonly TimeSpan DefaultSearchDelay = TimeSpan.FromMilliseconds(180);

    private readonly TimeProvider _clock;
    private readonly IHistorySource? _source;
    private readonly AnswerCoordinator? _answers;
    private readonly TimeSpan _searchDelay;
    private readonly RelayCommand _sendCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _newConversationCommand;
    private readonly RelayCommand _removeAttachmentCommand;
    private readonly AttachmentChipSet _chips = new();
    private readonly ScreenAttachments? _screens;

    // The parts of the screen that conversations opened here carry with each question (PROJECT_SPEC §4.6), by conversation.
    private readonly Dictionary<Guid, List<ImageItem>> _captures = [];
    private IReadOnlyList<DocumentAttachment> _documents = [];
    private IReadOnlyList<ImageItem> _pictures = [];
    private HistoryConversationViewModel? _selected;
    private int _selectionHolds;
    private HistoryLayout _layout;
    private string _draft = "";
    private int _refreshes;
    private int _appliedRefresh;

    private string _searchText = "";
    private bool _isSearchOpen;
    private bool _isSearching;
    private bool _searchSettled;
    private CancellationTokenSource? _search;
    private Task _searchTask = Task.CompletedTask;
    private readonly Assistant.UI.History.ConversationSurfaces? _surfaces;

    /// <summary>
    /// Creates the list. With <paramref name="source"/> the saved history fills it, once <see cref="RefreshAsync"/> reads
    /// it; with <paramref name="answers"/> the open conversation can be continued from the composer, and with
    /// <paramref name="recorder"/> what is said there is saved.
    /// </summary>
    /// <param name="clock">The clock the cards' times are read against.</param>
    /// <param name="source">Where the saved conversations come from, or <see langword="null"/> for none.</param>
    /// <param name="answers">Where answers come from, or <see langword="null"/> when nothing can be asked here.</param>
    /// <param name="recorder">Saves the messages sent and answered here, or <see langword="null"/> to save none.</param>
    /// <param name="searchDelay">How long the search waits after a key; <see cref="DefaultSearchDelay"/> unless given.</param>
    public HistoryViewModel(
        TimeProvider clock, IHistorySource? source = null, IAnswerProvider? answers = null,
        IConversationRecorder? recorder = null, TimeSpan? searchDelay = null, ScreenAttachments? screens = null,
        VoiceInputViewModel? voice = null, Assistant.UI.History.ConversationSurfaces? surfaces = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;

        // What is said here, and a chat that is moved here from the bar, is a chat of the full window, for Cleanup (Settings > Privacy).
        _surfaces = surfaces;
        if (recorder is not null && surfaces is not null)
        {
            recorder = new Assistant.UI.History.SurfaceRecorder(recorder, surfaces, Assistant.UI.History.ConversationSurface.Window);
        }

        Voice = voice;
        _screens = screens;
        _source = source;
        _searchDelay = searchDelay ?? DefaultSearchDelay;
        _answers = answers is null ? null : new AnswerCoordinator(answers, recorder);
        _sendCommand = new RelayCommand(_ => SendDraft(), _ => CanSendDraft);
        _stopCommand = new RelayCommand(_ => Stop(), _ => IsAnswering);
        _newConversationCommand = new RelayCommand(_ => StartNew(), _ => _answers is not null && !IsAnswering);
        if (voice is not null)
        {
            // What was said is sent as soon as the speaker stops, as it is in the floating conversation; said where it cannot be sent, it waits in the composer.
            voice.UtteranceEnded += (_, utterance) =>
            {
                if (utterance.HasRequest && !Send(utterance.Text))
                {
                    Draft = utterance.Text;
                }
            };
        }

        _removeAttachmentCommand = new RelayCommand(attachment =>
        {
            // The chip the user pressed stands for the document or the part of the screen; only an attached one is taken off.
            var source = attachment is AttachmentChip chip ? chip.Source : attachment;
            if (source is DocumentAttachment document && _documents.Contains(document))
            {
                SetDocuments([.. _documents.Where(attached => !ReferenceEquals(attached, document))]);
            }
            else if (source is ImageItem { IsCapture: false } picture && _pictures.Contains(picture))
            {
                SetPictures([.. _pictures.Where(attached => !ReferenceEquals(attached, picture))]);
            }
            else if (source is ImageItem { IsCapture: true } capture && _selected is { } open
                && _captures.TryGetValue(open.Id, out var held) && held.Contains(capture))
            {
                // Letting go of it frees its memory everywhere; the chip goes with the event.
                _screens?.Release(open.Id, capture);
                if (_screens is null)
                {
                    capture.Release();
                    ForgetCapture(capture);
                }
            }
        });
        if (screens is not null)
        {
            screens.Released += (_, capture) => ForgetCapture(capture);
            screens.Attached += (_, attached) => TakeCapture(attached);
        }

        if (_answers is not null)
        {
            _answers.Changed += (_, _) => AnsweringChanged();
        }

        ShowGridCommand = new RelayCommand(() => Layout = HistoryLayout.Grid);
        ShowListCommand = new RelayCommand(() => Layout = HistoryLayout.List);
        OpenSearchCommand = new RelayCommand(() => IsSearchOpen = true);
        CloseSearchCommand = new RelayCommand(CloseSearch);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when a search has changed which conversations the sidebar lists, or a search has ended, so the views over
    /// <see cref="Conversations"/> can list again (<see cref="HistoryConversationViewModel.IsShown"/>).
    /// </summary>
    public event EventHandler? ListedConversationsChanged;

    /// <summary>The conversations, most recently changed first.</summary>
    public ObservableCollection<HistoryConversationViewModel> Conversations { get; } = [];

    /// <summary>
    /// How the sidebar shows the conversations: cards in a grid, the default, or a list grouped by recency. It is kept
    /// while the application runs.
    /// </summary>
    public HistoryLayout Layout
    {
        get => _layout;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (_layout != value)
            {
                _layout = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGrid));
                OnPropertyChanged(nameof(IsList));
            }
        }
    }

    /// <summary>Whether the sidebar shows the conversations as cards in a grid.</summary>
    public bool IsGrid => _layout == HistoryLayout.Grid;

    /// <summary>Whether the sidebar shows the conversations as a list grouped by recency.</summary>
    public bool IsList => _layout == HistoryLayout.List;

    /// <summary>Shows the conversations as cards in a grid (the view menu's Grid).</summary>
    public ICommand ShowGridCommand { get; }

    /// <summary>Shows the conversations as a list grouped by recency (the view menu's List).</summary>
    public ICommand ShowListCommand { get; }

    /// <summary>
    /// The conversation open in the workspace, or <see langword="null"/> when none is. Opening one that came from the
    /// saved history reads its messages, which appear when they are here.
    /// </summary>
    public HistoryConversationViewModel? Selected
    {
        get => _selected;
        set
        {
            if (value is not null && !Conversations.Contains(value))
            {
                throw new ArgumentException("Only a conversation in the list can be selected.", nameof(value));
            }

            // A search that leaves the open conversation out of the sidebar takes it out of the list, which then reports
            // that nothing is selected; so does a list that is made again while it is listed. The conversation stays open
            // in the workspace.
            if (value is null && (_selectionHolds > 0 || _selected is { IsShown: false }))
            {
                return;
            }

            if (_selected != value)
            {
                var left = _selected;
                _selected = value;

                // A file or picture attached for one conversation's next question is not attached to another's.
                SetDocuments([]);
                SetPictures([]);
                SyncChips();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                _sendCommand.RaiseCanExecuteChanged();
                if (value is { IsLoaded: false })
                {
                    _ = LoadMessagesAsync(value);
                }

                // A new conversation that was left before anything was asked in it was never one: its card goes.
                if (left is { IsLoaded: true, Messages.Count: 0 } && Conversations.Contains(left))
                {
                    using (HoldSelection())
                    {
                        Conversations.Remove(left);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The microphone of the composer, which is this window's own; <see langword="null"/> where there is none. What is said is sent as the next
    /// message when the speaker stops.
    /// </summary>
    public VoiceInputViewModel? Voice { get; }

    /// <summary>Starts a new conversation in the workspace (the button over the list); not while an answer is on its way.</summary>
    public ICommand NewConversationCommand => _newConversationCommand;

    /// <summary>Raised when a new conversation was started here, so the window can put the keyboard in the composer.</summary>
    public event EventHandler? NewConversationStarted;

    /// <summary>
    /// Opens a new, empty conversation in the workspace, first in the list, ready for its first message. One that is open and still empty is that
    /// conversation already; one that is left empty is taken out of the list again.
    /// </summary>
    /// <returns>The conversation, or <see langword="null"/> when nothing can be asked here or an answer is on its way.</returns>
    public HistoryConversationViewModel? StartNew()
    {
        if (_answers is null || IsAnswering)
        {
            return null;
        }

        var conversation = _selected is { IsLoaded: true, Messages.Count: 0 } open ? open : Open(Guid.NewGuid(), [], _clock.GetUtcNow());
        Draft = "";
        NewConversationStarted?.Invoke(this, EventArgs.Empty);
        return conversation;
    }

    /// <summary>Whether a conversation is open in the workspace.</summary>
    public bool HasSelection => _selected is not null;

    /// <summary>
    /// Keeps the open conversation open while the sidebar lists the conversations again: a list that is made again loses
    /// its selection and reports that nothing is selected, which would close the conversation. Dispose the result once the
    /// list is made.
    /// </summary>
    internal IDisposable HoldSelection()
    {
        _selectionHolds++;
        return new SelectionHold(this);
    }

    /// <summary>What the user has typed in the composer along the workspace's bottom. Sending clears it.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            value ??= "";
            if (_draft != value)
            {
                _draft = value;
                OnPropertyChanged();
                _sendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The documents attached to the open conversation that the next message is about (PROJECT_SPEC §4.2), in the order they were
    /// attached, at most <see cref="ConversationViewModel.MaxDocuments"/>: each shown by its file name above the composer, with a
    /// button that takes it off again. Sending moves them onto the message, where they are read for the question (PROJECT_SPEC §5.5,
    /// several files).
    /// </summary>
    public IReadOnlyList<DocumentAttachment> Documents => _documents;

    /// <summary>The first of the <see cref="Documents"/>, or <see langword="null"/> when none is attached.</summary>
    public DocumentAttachment? Document => _documents.Count > 0 ? _documents[0] : null;

    /// <summary>
    /// The chips above the composer, one for each thing attached: the documents, for now (PROJECT_SPEC §4.2). The same row as the
    /// floating conversation's.
    /// </summary>
    public ReadOnlyObservableCollection<AttachmentChip> Chips => _chips.Chips;

    /// <summary>Takes an attached document off again; its parameter is its chip or the document.</summary>
    public ICommand RemoveAttachmentCommand => _removeAttachmentCommand;

    /// <summary>
    /// Attaches <paramref name="document"/> to the open conversation, so the next message is asked about it, with the documents
    /// attached before it. The same file again changes nothing, nor does one more than <see cref="ConversationViewModel.MaxDocuments"/>,
    /// and nothing is attached when no conversation is open.
    /// </summary>
    /// <returns><see langword="true"/> when it was attached.</returns>
    public bool Attach(DocumentAttachment document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_selected is null
            || _answers is null
            || _documents.Count >= ConversationViewModel.MaxDocuments
            || _documents.Any(attached => attached.IsSameFile(document)))
        {
            return false;
        }

        SetDocuments([.. _documents, document]);
        return true;
    }

    /// <summary>The pictures attached to the open conversation that the next message is about (the composer's Photos), in the order they were attached.</summary>
    public IReadOnlyList<ImageItem> Pictures => _pictures;

    /// <summary>
    /// Attaches <paramref name="picture"/> to the open conversation, so the next message is asked about it, as the floating conversation's composer
    /// does. The same file again changes nothing, and nothing is attached when no conversation is open.
    /// </summary>
    /// <returns><see langword="true"/> when it was attached.</returns>
    public bool Attach(ImageItem picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (_selected is null || _answers is null
            || _pictures.Any(attached => attached.Path is { } path && string.Equals(path, picture.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        SetPictures([.. _pictures, picture]);
        return true;
    }

    /// <summary>Sends the <see cref="Draft"/> as the next message of the open conversation (Enter in the composer).</summary>
    public ICommand SendCommand => _sendCommand;

    /// <summary>Stops the answer on its way or streaming in, keeping what it said.</summary>
    public ICommand StopCommand => _stopCommand;

    /// <summary>Whether an answer to a message sent here is still on its way or streaming in.</summary>
    public bool IsAnswering => _answers?.IsAnswering == true;

    /// <summary>
    /// Reads the saved conversations and brings the list up to date with them: the ones it does not have are added, the
    /// ones it has take their latest title, time, preview and image, and the list is put in order again. A conversation
    /// that is open, or was started here in this session, keeps its messages; nothing in the list is removed.
    /// </summary>
    /// <remarks>Call it on the UI thread: the list changes there.</remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_source is null)
        {
            return;
        }

        var refresh = ++_refreshes;
        IReadOnlyList<HistoryConversation> saved;
        try
        {
            saved = await _source.ListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HistoryUnavailableException)
        {
            return;
        }

        // A later refresh has read more recently; this one's answer is out of date.
        if (refresh < _appliedRefresh)
        {
            return;
        }

        _appliedRefresh = refresh;
        Merge(saved);
    }

    /// <summary>
    /// Sends <paramref name="text"/> as the next message of the open conversation, which the answer takes into account
    /// along with the ones before it; the answer appears in the workspace as it comes. Nothing is sent while an answer
    /// is on its way, or when no conversation is open, its messages are not here yet, or none can be answered.
    /// </summary>
    /// <returns><see langword="true"/> when the message was sent.</returns>
    public bool Send(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (_selected is not { IsLoaded: true } conversation || _answers is null || _answers.IsAnswering)
        {
            return false;
        }

        // The documents and pictures attached so far are what this message is about: they move onto the message.
        // What "it" or "the first one" means in a message that attaches nothing is for the model to work out from the conversation.
        var asked = new MessageViewModel(MessageRole.User, text, attachments: _pictures, documents: _documents) { CreatedAt = _clock.GetUtcNow() };
        _screens?.Touch(conversation.Id);
        SetDocuments([]);
        SetPictures([]);

        // A part of the screen the conversation carries (one the Assistant took itself) has been asked about now: its chip goes, as in the bar's conversation.
        if (_captures.TryGetValue(conversation.Id, out var carried))
        {
            foreach (var capture in carried)
            {
                capture.WasAsked = true;
            }

            SyncChips();
        }

        conversation.Add(asked, _clock.GetUtcNow());
        MoveToTop(conversation);
        _ = AnswerAsync(conversation, asked);
        return true;
    }

    /// <summary>Stops the answer on its way or streaming in. What it said stays, marked as stopped.</summary>
    public void Stop() => _answers?.Cancel();

    // The attached documents change: the chips follow, and both properties say so.
    private void SetDocuments(IReadOnlyList<DocumentAttachment> documents)
    {
        if (documents.Count == 0 && _documents.Count == 0)
        {
            return;
        }

        _documents = documents;
        SyncChips();
        OnPropertyChanged(nameof(Documents));
        OnPropertyChanged(nameof(Document));
    }

    // The attached pictures change: the chips follow.
    private void SetPictures(IReadOnlyList<ImageItem> pictures)
    {
        if (pictures.Count == 0 && _pictures.Count == 0)
        {
            return;
        }

        _pictures = pictures;
        SyncChips();
        OnPropertyChanged(nameof(Pictures));
    }

    // The chips: the pictures and documents waiting for the next message, and the parts of the screen that the open conversation carries.
    private void SyncChips()
    {
        List<object> attached = [.. _pictures, .. _documents];
        if (_selected is { } open && _captures.TryGetValue(open.Id, out var held))
        {
            // One that was asked about is in its message, and has no chip.
            attached.AddRange(held.Where(capture => !capture.WasAsked));
        }

        _chips.Sync(attached);
    }

    // The model took a screenshot in a conversation this window holds: it goes with the conversation, and its chip shows when it is open.
    private void TakeCapture(ScreenAttachment attached)
    {
        if (Conversations.All(item => item.Id != attached.ConversationId))
        {
            return;
        }

        if (!_captures.TryGetValue(attached.ConversationId, out var held))
        {
            _captures[attached.ConversationId] = held = [];
        }

        if (!held.Contains(attached.Capture))
        {
            held.Add(attached.Capture);
        }

        SyncChips();
    }

    // A capture was let go of, here or in the floating conversation.
    private void ForgetCapture(ImageItem capture)
    {
        foreach (var held in _captures.Values)
        {
            held.Remove(capture);
        }

        SyncChips();
    }

    private bool CanSendDraft => _selected is { IsLoaded: true } && _answers is not null && !IsAnswering
        && !string.IsNullOrWhiteSpace(_draft);

    private void SendDraft()
    {
        if (Send(_draft))
        {
            Draft = "";
        }
    }

    // The answer joins the conversation it was asked in, even if the user has opened another since, and the card shows
    // it once it is written.
    private async Task AnswerAsync(HistoryConversationViewModel conversation, MessageViewModel question)
    {
        try
        {
            await _answers!.AskAsync(
                conversation.Id,
                question,
                answer => conversation.Add(answer, _clock.GetUtcNow()),
                () => true,
                [.. conversation.Messages.TakeWhile(message => !ReferenceEquals(message, question))]);
        }
        finally
        {
            conversation.Refresh();
        }
    }

    private void MoveToTop(HistoryConversationViewModel conversation)
    {
        var index = Conversations.IndexOf(conversation);
        if (index > 0)
        {
            Conversations.Move(index, 0);
        }
    }

    private void AnsweringChanged()
    {
        OnPropertyChanged(nameof(IsAnswering));
        _sendCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _newConversationCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Opens a conversation in the workspace: its card goes first in the list, added or brought up to date with
    /// <paramref name="messages"/>, and is selected.
    /// </summary>
    public HistoryConversationViewModel Open(
        Guid id, IEnumerable<MessageViewModel> messages, DateTimeOffset updatedAt, IEnumerable<ImageItem>? captures = null)
    {
        // A chat the user moved to the full window is one of its chats from now on.
        _surfaces?.Note(id, Assistant.UI.History.ConversationSurface.Window);

        // The parts of the screen the conversation was asked about come along: it goes on here with them, and they are not let go of
        // when the floating conversation is replaced.
        if (captures is not null)
        {
            var held = captures.Where(capture => capture.IsCapture).ToList();
            if (held.Count > 0)
            {
                _captures[id] = held;
                _screens?.HandOver(id);
            }
        }

        var conversation = Conversations.FirstOrDefault(item => item.Id == id);
        if (conversation is null)
        {
            conversation = new HistoryConversationViewModel(id, messages, updatedAt, _clock) { IsShown = !_isSearching };
            Conversations.Insert(0, conversation);
        }
        else
        {
            conversation.Update(messages, updatedAt);
            Conversations.Move(Conversations.IndexOf(conversation), 0);
        }

        Selected = conversation;
        SyncChips();
        if (_isSearching)
        {
            // It may or may not be one of the search's results.
            ScheduleSearch();
        }

        return conversation;
    }

    /// <summary>
    /// Brings the cards' times up to date, such as "Yesterday" for a card from before midnight, and moves each row of
    /// the list under the header it now belongs to.
    /// </summary>
    public void RefreshTimes()
    {
        foreach (var conversation in Conversations)
        {
            conversation.RefreshTime();
        }
    }

    // Reads a conversation's messages from the saved history the first time it is opened.
    private async Task LoadMessagesAsync(HistoryConversationViewModel conversation)
    {
        if (_source is null || conversation.IsLoaded || conversation.IsLoading)
        {
            return;
        }

        conversation.IsLoading = true;
        try
        {
            var messages = await _source.LoadMessagesAsync(conversation.Id);

            // It may have been opened and continued meanwhile, in which case what is here is the truth.
            if (!conversation.IsLoaded)
            {
                conversation.Load(messages ?? []);
                if (ReferenceEquals(_selected, conversation))
                {
                    _sendCommand.RaiseCanExecuteChanged();
                    MessagesLoaded?.Invoke(this, conversation);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or HistoryUnavailableException)
        {
            // It could not be read; opening it again tries again.
        }
        finally
        {
            conversation.IsLoading = false;
        }
    }

    /// <summary>Raised when the messages of the open conversation have been read from the saved history.</summary>
    public event EventHandler<HistoryConversationViewModel>? MessagesLoaded;

    // Adds the saved conversations the list does not have, updates the ones it has, and puts the list in order.
    private void Merge(IReadOnlyList<HistoryConversation> saved)
    {
        var known = Conversations.ToDictionary(conversation => conversation.Id);
        foreach (var item in saved)
        {
            if (known.TryGetValue(item.Id, out var conversation))
            {
                conversation.ApplyListing(item);
            }
            else
            {
                Conversations.Add(new HistoryConversationViewModel(item, _clock) { IsShown = !_isSearching });
            }
        }

        SortByRecency();
        if (_isSearching)
        {
            ScheduleSearch();
        }
    }

    // Newest first. Only the rows that are out of place move, so a list that is in order is left alone.
    private void SortByRecency()
    {
        var ordered = Conversations.OrderByDescending(conversation => conversation.UpdatedAt).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            if (!ReferenceEquals(Conversations[index], ordered[index]))
            {
                Conversations.Move(Conversations.IndexOf(ordered[index]), index);
            }
        }
    }

    /// <summary>What the user has typed in the search field. Changing it searches, after a short wait for more typing.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= "";
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                ScheduleSearch();
            }
        }
    }

    /// <summary>Whether the search field is showing, in place of the search button.</summary>
    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set
        {
            if (_isSearchOpen != value)
            {
                _isSearchOpen = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Shows the search field (the search button, or Ctrl+F).</summary>
    public ICommand OpenSearchCommand { get; }

    /// <summary>Clears the search and puts the search field away, showing every conversation again (Esc in the field).</summary>
    public ICommand CloseSearchCommand { get; }

    /// <summary>
    /// Whether the sidebar lists the results of a search: something has been typed that has words in it, so only the
    /// conversations that match are listed.
    /// </summary>
    public bool IsSearching => _isSearching;

    /// <summary>Whether a search has run for what is typed and found no conversation, when the sidebar says so.</summary>
    public bool HasNoResults => _isSearching && _searchSettled && !Conversations.Any(conversation => conversation.IsShown);

    /// <summary>
    /// A task that completes when the search for what is typed now has been shown, or that is already complete when none is
    /// running. The search waits for a pause in typing, so a caller that needs its results waits for this.
    /// </summary>
    public Task WhenSearchSettledAsync() => _searchTask;

    private void CloseSearch()
    {
        SearchText = "";
        IsSearchOpen = false;
    }

    // Starts a search for what is typed, ending the one before it; an empty search lists every conversation again.
    private void ScheduleSearch()
    {
        _search?.Cancel();
        _search = null;
        var text = _searchText;
        if (_source is null || SearchQuery.Parse(text).IsEmpty)
        {
            _searchTask = Task.CompletedTask;
            EndSearch();
            return;
        }

        var search = _search = new CancellationTokenSource();
        SetSearching(true, settled: false);
        _searchTask = SearchAsync(text, search.Token);
    }

    private async Task SearchAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            if (_searchDelay > TimeSpan.Zero)
            {
                await Task.Delay(_searchDelay, cancellationToken);
            }

            var hits = await _source!.SearchAsync(text, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                ShowResults(hits);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by what was typed next, or closed.
        }
        catch (HistoryUnavailableException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ShowResults([]);
            }
        }
    }

    // Lists only the conversations the search found, each with what matched, newest first.
    private void ShowResults(IReadOnlyList<HistorySearchHit> hits)
    {
        var known = Conversations.ToDictionary(conversation => conversation.Id);
        var found = new HashSet<Guid>();
        foreach (var hit in hits)
        {
            var id = hit.Conversation.Id;
            if (!known.TryGetValue(id, out var conversation))
            {
                // Saved since the list was read.
                conversation = new HistoryConversationViewModel(hit.Conversation, _clock);
                Conversations.Add(conversation);
                known[id] = conversation;
            }

            found.Add(id);
            conversation.ShowMatch(hit.Snippet, hit.Matches, hit.MessageId);
            conversation.IsShown = true;
        }

        foreach (var conversation in Conversations.Where(conversation => !found.Contains(conversation.Id)))
        {
            conversation.ClearMatch();
            conversation.IsShown = false;
        }

        SortByRecency();
        SetSearching(true, settled: true);
        OnPropertyChanged(nameof(HasNoResults));
        ListedConversationsChanged?.Invoke(this, EventArgs.Empty);
    }

    // The search is over: every conversation is listed again, with its own preview.
    private void EndSearch()
    {
        foreach (var conversation in Conversations)
        {
            conversation.ClearMatch();
            conversation.IsShown = true;
        }

        var wasSearching = _isSearching;
        SetSearching(false, settled: false);
        if (wasSearching)
        {
            ListedConversationsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetSearching(bool searching, bool settled)
    {
        var changed = _isSearching != searching;
        var settledChanged = _searchSettled != settled;
        _isSearching = searching;
        _searchSettled = settled;
        if (changed)
        {
            OnPropertyChanged(nameof(IsSearching));
        }

        if (changed || settledChanged)
        {
            OnPropertyChanged(nameof(HasNoResults));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class SelectionHold(HistoryViewModel owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                owner._selectionHolds--;
            }
        }
    }
}
