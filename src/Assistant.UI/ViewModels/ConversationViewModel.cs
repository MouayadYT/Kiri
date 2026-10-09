using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Assistant.Core.Domain;
using Assistant.UI.Capture;
using Assistant.UI.Messages;
using Assistant.UI.Selection;
using Assistant.UI.Voice;

namespace Assistant.UI.ViewModels;

/// <summary>
/// View model for the floating conversation (PROJECT_SPEC §4.2): the messages it shows, its follow-up composer and its
/// voice input. Answers come from an <see cref="IAnswerProvider"/>; with an <see cref="IConversationRecorder"/>, each
/// message is saved to the history as it is asked and answered.
/// </summary>
public sealed class ConversationViewModel : INotifyPropertyChanged
{
    private readonly AnswerCoordinator _answers;
    private readonly TimeProvider _clock;
    private readonly RelayCommand _openInHistoryCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _askCommand;
    private readonly RelayCommand _removeAttachmentCommand;
    private readonly ObservableCollection<ImageItem> _attachments = [];
    private readonly ObservableCollection<TextAttachment> _texts = [];
    private readonly ObservableCollection<DocumentAttachment> _documents = [];
    private readonly AttachmentChipSet _chips = new();
    private readonly ScreenAttachments? _screens;
    private readonly ISpokenAnswers? _speech;
    private readonly RelayCommand _speakerCommand;
    private readonly RelayCommand _useKeyboardCommand;
    private readonly IReadOnlyList<CommandItemViewModel> _quickActions;

    // The question whose answer the user silenced with the speaker button: nothing more of its answer is said, though it carries on being written.
    private Guid? _silencedQuestion;
    private string _draft = "";

    // Whether the conversation was begun empty on purpose (StartEmpty), so that its composer shows with nothing in it.
    private bool _startedBlank;
    private string _attachNotice = "";

    public ConversationViewModel(
        VoiceInputViewModel voice, IAnswerProvider answers, TimeProvider? clock = null, ActivityViewModel? activity = null,
        IConversationRecorder? recorder = null, ScreenAttachments? screens = null, ISpokenAnswers? speech = null,
        Assistant.UI.History.ConversationSurfaces? surfaces = null)
    {
        // What is said here is a chat from the bar, for Cleanup (Settings > Privacy).
        if (recorder is not null && surfaces is not null)
        {
            recorder = new Assistant.UI.History.SurfaceRecorder(recorder, surfaces, Assistant.UI.History.ConversationSurface.Bar);
        }

        Voice = voice;
        Activity = activity;
        _screens = screens;
        _speech = speech;
        _speakerCommand = new RelayCommand(_ => PressSpeaker(), _ => CanPressSpeaker);
        _useKeyboardCommand = new RelayCommand(_ => UseKeyboard());
        _answers = new AnswerCoordinator(answers, recorder);
        _clock = clock ?? TimeProvider.System;
        _openInHistoryCommand = new RelayCommand(
            _ => OpenInHistoryRequested?.Invoke(this, EventArgs.Empty), _ => Messages.Count > 0);
        _stopCommand = new RelayCommand(_ => Stop(), _ => IsAnswering);
        _askCommand = new RelayCommand(_ => SendDraft(), _ => CanSendDraft);
        _quickActions =
        [
            .. SelectionQuickActions.All.Select(action =>
                new CommandItemViewModel(action.Title, action.IconKey, new RelayCommand(() => RunQuickAction(action)))),
        ];
        _removeAttachmentCommand = new RelayCommand(attachment =>
        {
            // The chip the user pressed stands for the attachment, which is what is taken off.
            switch (attachment is AttachmentChip chip ? chip.Source : attachment)
            {
                case ImageItem attached:
                    // A part of the screen that is taken off is let go of everywhere it is held, which frees its memory.
                    _attachments.Remove(attached);
                    _screens?.Release(Id, attached);
                    break;
                case TextAttachment text:
                    _texts.Remove(text);
                    break;
                case DocumentAttachment document:
                    _documents.Remove(document);
                    break;
            }
        });
        if (screens is not null)
        {
            // Taken off in the History window, where the conversation may have been continued: its chip goes here too.
            screens.Released += (_, capture) => _attachments.Remove(capture);

            // A part of the screen the model took in this conversation, in the middle of an answer, is attached like one the user captured.
            screens.Attached += (_, attached) =>
            {
                if (attached.ConversationId == Id && !_attachments.Contains(attached.Capture))
                {
                    _attachments.Add(attached.Capture);
                }
            };
        }

        Attachments = new ReadOnlyObservableCollection<ImageItem>(_attachments);
        Texts = new ReadOnlyObservableCollection<TextAttachment>(_texts);
        Documents = new ReadOnlyObservableCollection<DocumentAttachment>(_documents);
        _attachments.CollectionChanged += (_, _) => AttachedChanged();
        _texts.CollectionChanged += (_, _) => AttachedChanged();
        _documents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Document));
            AttachedChanged();
        };
        Messages.CollectionChanged += (_, _) =>
        {
            _openInHistoryCommand.RaiseCanExecuteChanged();
            _speakerCommand.RaiseCanExecuteChanged();
            ComposingChanged();
        };
        _answers.Changed += (_, _) => AnsweringChanged();
        voice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VoiceInputViewModel.IsListening))
            {
                ComposingChanged();
            }
        };

        // What was said is asked as soon as the speaker stops, as a spoken request: it also answers aloud (step 125).
        voice.UtteranceEnded += (_, utterance) =>
        {
            if (utterance.HasRequest)
            {
                AskBySpeech(utterance.Text);
            }
        };

        if (speech is not null)
        {
            // The speaker button shows whether the voice is on, and its answer to a press depends on it.
            speech.SpeakingChanged += (_, _) =>
            {
                _speakerCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsSpeaking));
            };
        }
    }

    /// <summary>
    /// Whether an answer is being said aloud, or waits for its first words to be: the speaker button is lit, and pressing it stops the voice (not the
    /// answer, which carries on being written).
    /// </summary>
    public bool IsSpeaking => _speech?.IsSpeaking == true;

    /// <summary>
    /// The speaker button (PROJECT_SPEC §4.2, step 125): while the Assistant is speaking it silences the voice at once, drops what was queued and keeps the
    /// rest of the answer from being said, without stopping the answer; otherwise it reads the newest answer aloud.
    /// </summary>
    public ICommand SpeakerCommand => _speakerCommand;

    /// <summary>
    /// The keyboard button the voice button turns into while the microphone listens: voice input goes off, and what was heard so far is put in the
    /// composer, to be read, changed and sent.
    /// </summary>
    public ICommand UseKeyboardCommand => _useKeyboardCommand;

    /// <summary>Raised when the user asks to go on with the conversation in the History window.</summary>
    public event EventHandler? OpenInHistoryRequested;

    /// <summary>
    /// Raised when something other than the user's own typing has put words in the composer, such as a quick action, so the window moves
    /// the keyboard to it with the caret after them.
    /// </summary>
    public event EventHandler? ComposerFocusRequested;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Which conversation the panel holds. Each new conversation has its own.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>When the conversation last changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The conversation's messages, oldest first.</summary>
    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    /// <summary>
    /// The images attached to the conversation that the next question is asked about (PROJECT_SPEC §4.2): shown as small
    /// pictures above the follow-up composer, each with a button that takes it off again. Asking moves them onto the user's
    /// message; a new conversation has none.
    /// </summary>
    public ReadOnlyObservableCollection<ImageItem> Attachments { get; }

    /// <summary>The most documents attached for one question (PROJECT_SPEC §4.4: at most ten files).</summary>
    public const int MaxDocuments = 10;

    /// <summary>What the composer says in a conversation that was begun empty, before anything is asked.</summary>
    public const string BlankComposerPlaceholder = "Ask Assistant";

    /// <summary>
    /// The documents attached to the conversation that the next question is asked about (PROJECT_SPEC §4.2), in the order they were
    /// attached, at most <see cref="MaxDocuments"/>: each shown by its file name above the follow-up composer, with a button that takes
    /// it off again. Asking moves them onto the user's message, where they are read for the question (PROJECT_SPEC §5.5, several
    /// files); a new conversation has none.
    /// </summary>
    public ReadOnlyObservableCollection<DocumentAttachment> Documents { get; }

    /// <summary>The first of the <see cref="Documents"/>, or <see langword="null"/> when none is attached.</summary>
    public DocumentAttachment? Document => _documents.Count > 0 ? _documents[0] : null;

    /// <summary>
    /// The pieces of text attached to the conversation that the next question is asked about (PROJECT_SPEC §4.2), such as a
    /// selection: each is a chip above the follow-up composer with a button that takes it off again. Asking moves them onto
    /// the user's message, where they go to the model with the question; a new conversation has none. They are never saved.
    /// </summary>
    public ReadOnlyObservableCollection<TextAttachment> Texts { get; }

    /// <summary>
    /// What could not be attached when files came from File Explorer (PROJECT_SPEC §4.4: reported per file), such as a file that
    /// was gone or a folder, shown in small type above the chips; empty when there is nothing to say. It goes when the
    /// next question is asked, a conversation starts, or Esc takes the attachments off.
    /// </summary>
    public string AttachNotice
    {
        get => _attachNotice;
        private set
        {
            value ??= "";
            if (_attachNotice != value)
            {
                _attachNotice = value;
                OnPropertyChanged(nameof(AttachNotice));
                OnPropertyChanged(nameof(HasAttachNotice));
                ComposingChanged();
            }
        }
    }

    /// <summary>Whether there is an <see cref="AttachNotice"/>.</summary>
    public bool HasAttachNotice => _attachNotice.Length > 0;

    /// <summary>
    /// The chips above the follow-up composer, one for each picture, document and text attached, in the order they were attached
    /// (PROJECT_SPEC §4.2): an icon or thumbnail, a name cut to fit, and a button that takes it off. The row scrolls sideways when
    /// they do not fit, so any number of attachments leaves the composer the same height.
    /// </summary>
    public ReadOnlyObservableCollection<AttachmentChip> Chips => _chips.Chips;

    /// <summary>
    /// Takes an attached image, document or text off again; its parameter is the chip that was pressed, or the image, the
    /// document or the text itself.
    /// </summary>
    public ICommand RemoveAttachmentCommand => _removeAttachmentCommand;

    /// <summary>The panel's voice input, shown by its visualizer and its voice button.</summary>
    public VoiceInputViewModel Voice { get; }

    /// <summary>
    /// The Searching chip's state, shared with the bar: it shows at the bottom of the panel while a search, the model or
    /// a tool runs. Esc cancels what it shows. It is <see langword="null"/> where nothing reports activity.
    /// </summary>
    public ActivityViewModel? Activity { get; }

    /// <summary>Opens the conversation in the History window (the panel's expand button), once it has a message.</summary>
    public ICommand OpenInHistoryCommand => _openInHistoryCommand;

    /// <summary>Stops the answer on its way or streaming in (the panel's Stop button), keeping what it said.</summary>
    public ICommand StopCommand => _stopCommand;

    /// <summary>
    /// What the user has typed in the follow-up composer. Asking clears it; it is also cleared by <see cref="StartNew"/>,
    /// which begins another conversation.
    /// </summary>
    public string Draft
    {
        get => _draft;
        set
        {
            value ??= "";
            if (_draft != value)
            {
                _draft = value;
                OnPropertyChanged(nameof(Draft));
                _askCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Sends the <see cref="Draft"/> as the next message of the conversation (Enter in the composer).</summary>
    public ICommand AskCommand => _askCommand;

    /// <summary>
    /// Whether the next message can be sent from the composer: once the conversation has a message or an attachment, and while no answer
    /// is on its way and the microphone is off (the voice button offers the keyboard then).
    /// </summary>
    public bool CanCompose => ShowsComposer && !IsAnswering;

    /// <summary>
    /// Whether the follow-up composer shows: once the conversation has a message or an attachment, and while the microphone is off. It stays
    /// while an answer comes, so the next message can be typed ahead; it is sent once the answer is done (<see cref="CanCompose"/>), and
    /// what the Assistant is doing meanwhile is shown in the conversation and not over the composer.
    /// </summary>
    public bool ShowsComposer => (Messages.Count > 0 || HasAttachment || HasAttachNotice || _startedBlank) && !Voice.IsListening;

    /// <summary>
    /// What the empty composer says. Before anything is asked, a conversation that has files attached, such as the ones sent from
    /// File Explorer, says what they are for ("Ask about these pictures") so that the user knows the next words are about them;
    /// otherwise it is a follow-up.
    /// </summary>
    public string ComposerPlaceholder
    {
        get
        {
            if (Messages.Count > 0)
            {
                return "Ask a follow-up";
            }

            if (!HasAttachment)
            {
                return _startedBlank ? BlankComposerPlaceholder : "Ask a follow-up";
            }

            var count = _attachments.Count + _texts.Count + _documents.Count;
            var onlyPictures = _texts.Count == 0 && _documents.Count == 0;
            var onlyOneThing = count == 1;
            return (onlyPictures, onlyOneThing, _documents.Count > 0) switch
            {
                (true, true, _) => "Ask about this picture",
                (true, false, _) => $"Ask about these {count} pictures",
                (false, true, true) => "Ask about this file",
                (false, true, false) => "Ask about this text",
                _ => $"Ask about these {count} files",
            };
        }
    }

    /// <summary>
    /// The quick actions offered with selected text (PROJECT_SPEC §4.5), top to bottom: Summarize, Explain, Rewrite, Solve, Define and
    /// Ask Anything. Each writes its request in the composer and puts the keyboard there; none of them sends it.
    /// </summary>
    public IReadOnlyList<CommandItemViewModel> QuickActions => _quickActions;

    /// <summary>
    /// Whether the panel lists the <see cref="QuickActions"/> and the selected text they are about: while text is attached and nothing
    /// has been asked yet. Asking, or taking the text off, puts them away.
    /// </summary>
    public bool ShowQuickActions => Messages.Count == 0 && _texts.Count > 0;

    /// <summary>The start of the attached text that the quick actions are about, for the user to see what was selected.</summary>
    public string SelectionPreview => _texts.Count > 0 ? _texts[0].Preview : "";

    /// <summary>
    /// What the selection's card says when page text around the selection came with it (step 88): how much, so the user knows more than
    /// their selection goes to the model. Empty for a selection that is only the selected words.
    /// </summary>
    public string SelectionNearbyNotice => _texts.Count > 0 ? _texts[0].NearbyNotice : "";

    /// <summary>Whether the selection's card says that page text around the selection is included.</summary>
    public bool HasSelectionNearbyContext => _texts.Count > 0 && _texts[0].HasNearbyContext;

    /// <summary>What the selection's card is called: "Selected text", and how long it is.</summary>
    public string SelectionCaption
    {
        get
        {
            if (_texts.Count == 0)
            {
                return "";
            }

            var length = _texts[0].Text.Length;
            var origin = _texts[0].WebPage is { } web
                ? string.IsNullOrWhiteSpace(web.BrowserName) ? " from the browser" : $" from {web.BrowserName.Trim()}"
                : "";
            return string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"Selected text{origin} · {length:N0} {(length == 1 ? "character" : "characters")}");
        }
    }

    /// <summary>Whether an answer is still on its way or still streaming in, so Esc stops it before it closes the panel.</summary>
    public bool IsAnswering => _answers.IsAnswering;

    /// <summary>
    /// Whether an answer is shown and still streaming in, when the panel offers its Stop button. Before its first words
    /// the Searching chip stands there, and pressing it stops the answer as well.
    /// </summary>
    public bool IsStreaming => _answers.IsStreaming;

    /// <summary>
    /// Starts a new conversation with <paramref name="question"/> as its first message, followed by the answer, if
    /// there is one. An answer that takes time arrives when it is ready, a streamed one grows as it comes, and a new
    /// conversation, or cancelling it, ends the wait or the stream.
    /// </summary>
    public void StartNew(string question, bool spoken = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        AbandonCaptures();
        _startedBlank = false;
        Id = Guid.NewGuid();
        UpdatedAt = _clock.GetUtcNow();
        _answers.Cancel();

        // A new question is more important than what the Assistant is still saying of the last one.
        _speech?.Stop();
        _silencedQuestion = null;
        Draft = "";
        _attachments.Clear();
        _texts.Clear();
        _documents.Clear();
        AttachNotice = "";
        Messages.Clear();
        var asked = new MessageViewModel(MessageRole.User, question) { CreatedAt = _clock.GetUtcNow(), IsSpoken = spoken };
        Messages.Add(asked);
        _ = ShowAnswerAsync(asked);
    }

    /// <summary>
    /// Starts a new conversation with no message yet and <paramref name="image"/> attached, for the user to ask about it
    /// (PROJECT_SPEC §4.2): the follow-up composer shows with the picture above it. What the user types next is its first
    /// message, asked about the image.
    /// </summary>
    public void StartWithAttachment(ImageItem image)
    {
        ArgumentNullException.ThrowIfNull(image);
        AbandonCaptures();
        _startedBlank = false;
        Id = Guid.NewGuid();
        UpdatedAt = _clock.GetUtcNow();
        _answers.Cancel();
        Draft = "";
        Messages.Clear();
        _attachments.Clear();
        _texts.Clear();
        _documents.Clear();
        AttachNotice = "";
        _attachments.Add(image);
    }

    /// <summary>
    /// Starts a new conversation with no message yet and <paramref name="capture"/>, a part of the screen the user captured, attached
    /// (PROJECT_SPEC §4.6): the follow-up composer shows with its chip above it, ready for what the user wants to know about it. The
    /// picture is given to the context service for this conversation, and stays attached to it, going with each question, until
    /// the user takes it off with its chip's button.
    /// </summary>
    public void StartWithCapture(ImageItem capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        StartWithAttachment(capture);
        _screens?.Attach(Id, capture);
    }

    /// <summary>
    /// Starts a new conversation that has only a message from the Assistant, <paramref name="text"/>, such as why a capture could not be
    /// made. There is nothing to ask about yet, so no composer shows.
    /// </summary>
    public void StartWithNotice(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        AbandonCaptures();
        _startedBlank = false;
        Id = Guid.NewGuid();
        UpdatedAt = _clock.GetUtcNow();
        _answers.Cancel();
        Draft = "";
        _attachments.Clear();
        _texts.Clear();
        _documents.Clear();
        AttachNotice = "";
        Messages.Clear();
        Messages.Add(new MessageViewModel(MessageRole.Assistant, text) { CreatedAt = _clock.GetUtcNow() });
    }

    /// <summary>
    /// Starts a new conversation with no message yet and <paramref name="selection"/>, the text the user selected in another application,
    /// attached (PROJECT_SPEC §4.5): the follow-up composer shows with its chip above it and the <see cref="QuickActions"/> are listed,
    /// and what the user sends next is its first message, asked about the text. Nothing is asked here. The text is held in memory only.
    /// </summary>
    /// <param name="selection">The selected text.</param>
    /// <param name="notice">What to say above the composer about the selection, such as that only its start was read, or <see langword="null"/>.</param>
    public void StartWithSelection(TextAttachment selection, string? notice = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        AbandonCaptures();
        _startedBlank = false;
        Id = Guid.NewGuid();
        UpdatedAt = _clock.GetUtcNow();
        _answers.Cancel();
        Draft = "";
        Messages.Clear();
        _attachments.Clear();
        _documents.Clear();
        _texts.Clear();
        _texts.Add(selection);
        AttachNotice = notice ?? "";
    }

    /// <summary>
    /// The parts of the screen attached to this conversation, which stay with it for each question: what the History window takes over
    /// when the conversation is opened there.
    /// </summary>
    public IReadOnlyList<ImageItem> Captures => [.. _attachments.Where(image => image.IsCapture)];

    /// <summary>
    /// Starts a new conversation with no message yet and <paramref name="document"/> attached, for the user to ask about it
    /// (PROJECT_SPEC §4.2): the follow-up composer shows with the file's name above it. What the user types next is its first
    /// message, asked about the file.
    /// </summary>
    public void StartWithDocument(DocumentAttachment document)
    {
        ArgumentNullException.ThrowIfNull(document);
        AbandonCaptures();
        _startedBlank = false;
        Id = Guid.NewGuid();
        UpdatedAt = _clock.GetUtcNow();
        _answers.Cancel();
        Draft = "";
        Messages.Clear();
        _attachments.Clear();
        _texts.Clear();
        AttachNotice = "";
        _documents.Clear();
        _documents.Add(document);
    }

    /// <summary>
    /// Starts a new conversation with no message yet and the files the user sent from File Explorer attached (PROJECT_SPEC §4.4,
    /// Ask about): <paramref name="images"/> and <paramref name="documents"/> (at most <see cref="MaxDocuments"/>), with
    /// <paramref name="notice"/> saying what was left out. What the user types next is its first message, asked about them.
    /// </summary>
    public void StartWithFiles(IReadOnlyList<ImageItem> images, IReadOnlyList<DocumentAttachment> documents, string? notice)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(documents);
        AbandonCaptures();
        _startedBlank = false;
        Id = Guid.NewGuid();
        UpdatedAt = _clock.GetUtcNow();
        _answers.Cancel();
        Draft = "";
        Messages.Clear();
        _attachments.Clear();
        _texts.Clear();
        _documents.Clear();
        foreach (var image in images.Where(image => !_attachments.Any(attached => IsSame(attached, image))))
        {
            _attachments.Add(image);
        }

        foreach (var document in documents)
        {
            Attach(document);
        }

        AttachNotice = notice ?? "";
    }

    /// <summary>
    /// Starts a new conversation with nothing in it and nothing attached: the composer waits for the first message (the tray menu's New
    /// Conversation, PROJECT_SPEC §4.9). The one that was open stays as it was saved in the history.
    /// </summary>
    public void StartEmpty()
    {
        StartWithFiles([], [], null);

        // With nothing in it there would be no composer, and so no way to begin: this one has it, and what is typed starts the conversation.
        _startedBlank = true;
        ComposingChanged();
    }

    /// <summary>
    /// Attaches <paramref name="document"/> to the conversation, so the next question is asked about it, with the documents
    /// attached before it. The same file again changes nothing, and so does one more than <see cref="MaxDocuments"/>.
    /// </summary>
    /// <returns><see langword="true"/> when it was attached.</returns>
    public bool Attach(DocumentAttachment document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_documents.Count >= MaxDocuments || _documents.Any(attached => attached.IsSameFile(document)))
        {
            return false;
        }

        _documents.Add(document);
        return true;
    }

    /// <summary>
    /// Attaches <paramref name="image"/> to the conversation, so the next question is asked about it. An image that is
    /// already attached (the same file) is not attached twice.
    /// </summary>
    /// <returns><see langword="true"/> when it was attached.</returns>
    public bool Attach(ImageItem image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_attachments.Any(attached => IsSame(attached, image)))
        {
            return false;
        }

        _attachments.Add(image);
        return true;
    }

    /// <summary>
    /// Attaches <paramref name="text"/> to the conversation, so the next question is asked about it. The same text again is not
    /// attached twice.
    /// </summary>
    /// <returns><see langword="true"/> when it was attached.</returns>
    public bool Attach(TextAttachment text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_texts.Any(attached => attached.IsSameText(text)))
        {
            return false;
        }

        _texts.Add(text);
        return true;
    }

    /// <summary>
    /// Asks <paramref name="question"/> as the next message of this conversation, which the answer takes into account
    /// along with the ones before it. A conversation that has no message yet starts with it. Nothing is asked while an
    /// answer is still on its way: the user stops it first.
    /// </summary>
    /// <returns><see langword="true"/> when the question was asked.</returns>
    public bool Ask(string question) => Ask(question, spoken: false);

    /// <summary>
    /// Asks <paramref name="question"/> as <see cref="Ask(string)"/> does. A <paramref name="spoken"/> question was said with the microphone, and its answer is
    /// also read aloud as it is written (PROJECT_SPEC §4.2, step 125).
    /// </summary>
    /// <returns><see langword="true"/> when the question was asked.</returns>
    public bool Ask(string question, bool spoken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        if (Messages.Count == 0 && !HasAttachment)
        {
            // Nothing attached, even when File Explorer's files could not be: the question starts the conversation.
            StartNew(question, spoken);
            return true;
        }

        if (IsAnswering)
        {
            return false;
        }

        // Whatever the Assistant is still saying of the last answer stops: this question has the floor.
        _speech?.Stop();
        _silencedQuestion = null;

        // The images and the documents attached so far are what this question is about: they move onto the message. What "it" or "the
        // first one" means in a question that attaches nothing is for the model to work out from the whole conversation.
        // A part of the screen stays with the conversation after it is asked about, so the next question is about it too; the message of the
        // first question shows it, the later ones do not show it again, and its chip leaves the composer with the question, as every other
        // attachment's does (it was left there until 0.1.148, and read as a picture still waiting to be sent).
        UpdatedAt = _clock.GetUtcNow();
        _screens?.Touch(Id);
        var shown = _attachments.Where(image => !(image.IsCapture && image.WasAsked)).ToArray();
        var asked = new MessageViewModel(MessageRole.User, question, shown, null, _texts.ToArray(), _documents.ToArray())
        {
            CreatedAt = _clock.GetUtcNow(),
            IsSpoken = spoken,
        };
        foreach (var capture in _attachments.Where(image => image.IsCapture).ToList())
        {
            capture.WasAsked = true;
        }

        foreach (var image in _attachments.Where(image => !image.IsCapture).ToList())
        {
            _attachments.Remove(image);
        }

        _texts.Clear();
        _documents.Clear();
        AttachNotice = "";
        AttachedChanged();
        Messages.Add(asked);
        _ = ShowAnswerAsync(asked);
        return true;
    }

    /// <summary>
    /// Handles Esc: the first press turns voice input off, the next cancels an operation that is running or stops an
    /// answer that is streaming in, the next clears what was typed in the composer, the next takes off what is attached to it
    /// (the images, the texts and the documents), and otherwise the panel should close.
    /// </summary>
    /// <returns><see langword="true"/> when the panel should close.</returns>
    public bool HandleEscape()
    {
        if (Voice.IsListening)
        {
            Voice.Stop();
            return false;
        }

        // The Assistant's voice is the next thing Esc silences: it is a step of its own, as stopping the answer is.
        if (_speech?.IsSpeaking == true)
        {
            _silencedQuestion = NewestQuestion()?.Id;
            _speech.Stop();
            return false;
        }

        // Esc first cancels an operation that is running, such as a search or the model thinking, before it closes the
        // panel; an answer already streaming in is stopped where it is, and what it said stays.
        if (Activity?.Cancel() == true)
        {
            return false;
        }

        if (IsAnswering)
        {
            Stop();
            return false;
        }

        if (Draft.Length > 0)
        {
            Draft = "";
            return false;
        }

        // What is waiting to be asked about comes off; a part of the screen that was asked about stays, since the next question is about it
        // too: only its own button takes it off.
        if (HasWaitingAttachment || HasAttachNotice)
        {
            foreach (var image in _attachments.Where(image => !(image.IsCapture && image.WasAsked)).ToList())
            {
                _attachments.Remove(image);
                _screens?.Release(Id, image);
            }

            _texts.Clear();
            _documents.Clear();
            AttachNotice = "";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stops the answer that is on its way or streaming in. What it said stays, and the answer is marked as stopped,
    /// not failed; the model stops its work on it at once.
    /// </summary>
    public void Stop() => _answers.Cancel();

    // Whether anything is attached for the next question to be about.
    private bool HasAttachment => _attachments.Count > 0 || _texts.Count > 0 || _documents.Count > 0;

    // Whether anything is attached that has not been asked about yet.
    private bool HasWaitingAttachment =>
        _attachments.Any(image => !(image.IsCapture && image.WasAsked)) || _texts.Count > 0 || _documents.Count > 0;

    // The conversation is replaced by another: what it had of the screen is let go of, unless the History window has taken it over.
    private void AbandonCaptures()
    {
        foreach (var capture in _attachments.Where(image => image.IsCapture).ToList())
        {
            _screens?.Abandon(Id, capture);
        }
    }

    private bool CanSendDraft =>
        !string.IsNullOrWhiteSpace(_draft) && (Messages.Count > 0 || HasAttachment || HasAttachNotice || _startedBlank) && !IsAnswering;

    // The same file, however its path is written in case.
    private static bool IsSame(ImageItem first, ImageItem second) =>
        first.Path is { } path && string.Equals(path, second.Path, StringComparison.OrdinalIgnoreCase);

    private void SendDraft()
    {
        if (Ask(_draft))
        {
            Draft = "";
        }
    }

    // An answer that is ready at once is added before this returns; a slower one is added when it arrives, and a
    // streamed one as soon as it has something to show.
    // An answer that is stopped may still show itself, marked as stopped; one for a conversation the panel no longer
    // holds never does.
    private Task ShowAnswerAsync(MessageViewModel question)
    {
        var conversation = Id;
        return _answers.AskAsync(
            conversation, question, answer => ShowAnswer(question, answer), () => conversation == Id,
            [.. Messages.TakeWhile(message => !ReferenceEquals(message, question))]);
    }

    // An answer is added to the conversation, and, when the question was spoken, read aloud as it is written. The voice only listens to the message: the
    // text is drawn the same without it, and nothing in it waits for the voice.
    private void ShowAnswer(MessageViewModel question, MessageViewModel answer)
    {
        Messages.Add(answer);
        if (question.IsSpoken && _speech is not null && _silencedQuestion != question.Id)
        {
            _speech.Follow(answer);
        }
    }

    // A request said with the microphone. The user talking over an answer that is still coming is the user taking the floor: it is stopped, and so is
    // its voice (asking silences it), and the new request is asked in its place.
    private void AskBySpeech(string request)
    {
        if (IsAnswering)
        {
            Stop();
        }

        Ask(request, spoken: true);
    }

    private bool CanPressSpeaker => _speech is not null && (_speech.IsSpeaking || NewestAnswer() is not null);

    // Speaking: the voice stops, for this question's answer only, and the answer carries on. Otherwise the newest answer is read, from the start and as far
    // as it has got.
    private void PressSpeaker()
    {
        if (_speech is null)
        {
            return;
        }

        if (_speech.IsSpeaking)
        {
            _silencedQuestion = NewestQuestion()?.Id;
            _speech.Stop();
            return;
        }

        if (NewestAnswer() is { } answer)
        {
            _silencedQuestion = null;
            _speech.Follow(answer);
        }
    }

    private MessageViewModel? NewestAnswer() =>
        Messages.LastOrDefault(message => message.Role == MessageRole.Assistant && message.Content.OfType<TextContent>().Any(text => text.Text.Length > 0));

    private MessageViewModel? NewestQuestion() => Messages.LastOrDefault(message => message.Role == MessageRole.User);

    private void UseKeyboard()
    {
        var heard = Voice.Transcript;
        Voice.Stop();
        if (heard.Length > 0)
        {
            Draft = Draft.Length == 0 ? heard : Draft.TrimEnd() + " " + heard;
        }

        // The composer shows now that the microphone is off, and the window gives it the keyboard.
    }

    private void AnsweringChanged()
    {
        _stopCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnswering));
        OnPropertyChanged(nameof(IsStreaming));
        ComposingChanged();
    }

    // What is attached changed: the chips above the composer follow, and whether there is anything to ask about.
    private void AttachedChanged()
    {
        // A part of the screen that was asked about is in its message; it has no chip any more, though the conversation still carries it.
        List<object> attached = [.. _attachments.Where(image => !(image.IsCapture && image.WasAsked)), .. _texts, .. _documents];
        _chips.Sync(attached);
        ComposingChanged();
    }

    private void ComposingChanged()
    {
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(ShowsComposer));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(ShowQuickActions));
        OnPropertyChanged(nameof(SelectionPreview));
        OnPropertyChanged(nameof(SelectionCaption));
        OnPropertyChanged(nameof(SelectionNearbyNotice));
        OnPropertyChanged(nameof(HasSelectionNearbyContext));
        _askCommand.RaiseCanExecuteChanged();
    }

    // A quick action prepares the request in the composer and hands the keyboard to it: the user reads it, changes it if they like, and
    // sends it. It never sends. Ask Anything has nothing to prepare, so what is already typed stays.
    private void RunQuickAction(SelectionQuickAction action)
    {
        if (!ShowQuickActions)
        {
            return;
        }

        if (action.Prompt.Length > 0)
        {
            Draft = action.Prompt;
        }

        ComposerFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
