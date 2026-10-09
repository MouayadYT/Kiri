using System.IO;
using Assistant.UI.Explorer;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;
using Assistant.UI.Voice;
using Assistant.Windows.Placement;

namespace Assistant.UI.Windowing;

/// <summary>
/// Decides what the Assistant's one window shows, <see cref="AssistantWindowState.Compact"/> or
/// <see cref="AssistantWindowState.FloatingConversation"/>, and moves it from one to the other. The hotkey opens the
/// bar, or brings back the conversation while one is open; asking from the bar makes the same surface grow into the
/// floating conversation, with the question as the first message and nothing else appearing (PROJECT_SPEC §4.1). The
/// typed question stays on screen until the bar has gone, and voice input the user had turned on carries on in the
/// conversation. Where the user drags the window is where it opens again while the Assistant stays up; once it is
/// hidden, the position is forgotten and the next opening is at the default place on the active monitor.
/// </summary>
internal sealed class AssistantWindowStateController
{
    private readonly IAssistantWindow _window;
    private readonly SearchOrAskViewModel _bar;
    private readonly ConversationViewModel _conversation;

    // The top center of the surface the user last dragged, in physical pixels, until the Assistant is dismissed.
    private ScreenPoint? _draggedTo;

    // Whether the bar still holds a question that has been asked, until the bar has gone.
    private bool _questionAsked;

    private readonly ISpokenAnswers? _speech;

    // Whether the window is up (it was opened through this controller and has not been hidden), and whether the wake word is what opened it, so that a
    // bar that was woken and then heard nothing goes away again instead of waiting for a keystroke that is not coming.
    private bool _shown;
    private bool _openedByWakeWord;

    public AssistantWindowStateController(
        IAssistantWindow window, SearchOrAskViewModel bar, ConversationViewModel conversation,
        AttachRequests? attachments = null, QuickSearchActionRunner? runner = null, ISpokenAnswers? speech = null)
    {
        _window = window;
        _bar = bar;
        _conversation = conversation;
        _speech = speech;
        bar.Voice.UtteranceEnded += OnVoiceEnded;
        conversation.Voice.UtteranceEnded += OnVoiceEnded;

        // What was chosen from the results: the bar goes when it was run, says so when it could not be, and lists again when the list changed.
        if (runner is not null)
        {
            runner.Finished += (_, _) =>
            {
                // What was run is done with: the bar goes, and what was typed goes with it once it has faded (not before, or the list
                // would turn into the categories while it leaves).
                _questionAsked = true;
                _window.Dismiss();
            };
            runner.Failed += (_, message) => _bar.Results.ShowNotice(message);
            runner.ListChanged += (_, _) => _bar.Results.Refresh();
        }

        bar.AskRequested += OnAskRequested;
        if (attachments is not null)
        {
            attachments.Requested += OnAttachRequested;
            attachments.DocumentRequested += OnDocumentAttachRequested;
            attachments.TextRequested += OnTextAttachRequested;
        }

        window.PicturesPasted += OnPicturesPasted;
        window.Moved += (_, _) => _draggedTo = window.SurfaceTop;
        window.Expanded += (_, _) => ClearAskedQuestion();
        window.Hidden += OnHidden;
        window.AssistantStateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>What the window is showing, or heading for.</summary>
    public AssistantWindowState State => _window.State;

    /// <summary>The top center, in physical pixels, of the surface the user last dragged while the Assistant is up.</summary>
    internal ScreenPoint? DraggedTo => _draggedTo;

    /// <summary>
    /// The Search or Ask shortcut (Alt+A), which opens and closes the Assistant as the reference's does: pressed while the Assistant is up, whatever
    /// it shows (the bar, the Working pill, the conversation), it puts it away, keeping what was typed; pressed while it is not, it opens it as
    /// <see cref="Invoke"/> does, with what was typed before selected, so that typing replaces it and the right arrow carries on after it. A question
    /// the Assistant is still working on when it is put away is stopped, as Stop would stop it: nothing goes on out of sight.
    /// </summary>
    public void Toggle()
    {
        if (_window.IsShowing)
        {
            StopWork();
            _window.Dismiss();
            return;
        }

        Invoke();
    }

    // Stops what the Assistant is doing for the question that was asked: the answer on its way, and what the bar or the conversation is looking up for it.
    private void StopWork()
    {
        if (_conversation.IsAnswering)
        {
            _conversation.Activity?.Cancel();
            _conversation.Stop();
        }

        if (_bar.Activity is { IsVisible: true } working)
        {
            working.Cancel();
        }
    }

    /// <summary>
    /// Invokes the Assistant: brings the floating conversation back to the front while one is open, and otherwise opens the bar.
    /// </summary>
    public void Invoke()
    {
        // A bar that is opened starts as the user chose (Files first, or everything), not where the last visit left the chips.
        if (!_shown && _window.State == AssistantWindowState.Compact && _bar.Query.Length == 0)
        {
            _bar.Results.ResetScope();
        }

        _shown = true;
        _draggedTo = null;
        _window.ShowAtPointer();
    }

    /// <summary>
    /// The wake word was heard (PROJECT_SPEC §4.2, step 125): the Assistant stops talking, opens where Alt+A would open it if it is not up (a window that is
    /// up is not opened again, nor moved), and turns the microphone on for the request that follows, in whichever surface is showing: <paramref name="handoff"/>
    /// has the words around the wake word, which the request starts with. Call it on the UI thread.
    /// </summary>
    public void BeginVoiceFromWakeWord(WakeHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        // Saying its name takes the floor from whatever it is saying.
        _speech?.Stop();
        var wasShown = _shown;
        if (wasShown)
        {
            // Already up and idle: it listens where it is, brought to the front so that it can be seen.
            _window.TakeForeground();
        }
        else
        {
            Invoke();
            _openedByWakeWord = true;
        }

        (_window.State == AssistantWindowState.Compact ? _bar.Voice : _conversation.Voice).Start(handoff);
    }

    /// <summary>
    /// Opens the floating conversation with the files the user sent from File Explorer attached (PROJECT_SPEC §4.4, Ask about),
    /// as a new conversation with no message yet, whatever the window was showing: a hidden window opens as the conversation,
    /// the bar grows into it, and a conversation that shows starts again with them. What could not be attached is said above
    /// the composer.
    /// </summary>
    public void OpenWithFiles(ExplorerFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var pictures = files.Pictures.Select(path => new ImageItem(Path.GetFileName(path), path)).ToArray();
        var documents = files.Documents.Select(path => new DocumentAttachment(Path.GetFileName(path), path)).ToArray();
        // What was typed in the bar is not what the files are about: it stays there for the next time the bar opens.
        _bar.Voice.Stop();
        _conversation.StartWithFiles(pictures, documents, files.Notice);
        _shown = true;
        _window.ShowConversation(_draggedTo);
    }

    /// <summary>
    /// Opens the floating conversation as a new, empty one, whatever the window was showing (the tray menu's New Conversation, PROJECT_SPEC
    /// §4.9): a hidden window opens as the conversation, the bar grows into it, and a conversation that shows starts again. The composer
    /// waits for the first message; the conversation that was open is not lost, it is in the history as far as it was asked.
    /// </summary>
    public void OpenNewConversation()
    {
        // What was typed in the bar is not what the conversation is about: it stays there for the next time the bar opens.
        _bar.Voice.Stop();
        _conversation.StartEmpty();
        _shown = true;
        _window.ShowConversation(_draggedTo);
    }

    /// <summary>
    /// Shows the floating conversation as it is, whatever the window was showing, for a way in that has already put what it wants shown in
    /// it: a part of the screen that was captured and attached to a conversation of its own, with the composer waiting for the question
    /// (PROJECT_SPEC §4.6), or a note about why nothing was captured. A hidden window opens as the conversation, the bar grows into it.
    /// </summary>
    public void ShowConversation()
    {
        // What was typed in the bar is not what the conversation is about: it stays there for the next time the bar opens.
        _bar.Voice.Stop();
        _shown = true;
        _window.ShowConversation(_draggedTo);
    }

    /// <summary>
    /// Shows the floating conversation beside another application's window, as it is: for text selected in a browser, which opens the
    /// panel inside the browser's window, against its right edge, rather than at the default place. Without a window to open beside
    /// (the browser is not in front any more), it is <see cref="ShowConversation"/>.
    /// </summary>
    /// <param name="target">The browser's window as it was when the selection arrived, or <see langword="null"/>.</param>
    public void ShowConversationNear(NearWindowTarget? target)
    {
        if (target is null)
        {
            ShowConversation();
            return;
        }

        _bar.Voice.Stop();
        _shown = true;
        _window.ShowConversationNear(target);
    }

    /// <summary>
    /// Makes the window that was just shown the foreground one, with the keyboard in it. A way in that had to wait for an answer from
    /// another application before it showed anything, such as the selected-text shortcut, asks for this after
    /// <see cref="ShowConversation"/>.
    /// </summary>
    /// <returns>Whether the window is in front and has the keyboard.</returns>
    public bool TakeForeground() => _window.TakeForeground();

    // The question becomes the first message of a new conversation, and the bar it was typed in grows into the panel
    // where it is. Only the bar can ask: while it is already growing, another Enter has nothing left to ask.
    private void OnAskRequested(object? sender, string question)
    {
        if (_window.State != AssistantWindowState.Compact)
        {
            return;
        }

        var keepListening = _bar.Voice.IsListening;
        _questionAsked = true;
        _openedByWakeWord = false;
        _conversation.StartNew(question, spoken: _bar.AskingBySpeech);
        _bar.Voice.Stop();

        // As in the reference: the bar folds into the Working pill while the answer is on its way, and the pill opens into the conversation when
        // it comes. An answer that is there at once (a sum) has nothing to wait for, and the bar grows straight into it.
        _window.AwaitAnswer();
        if (keepListening)
        {
            _conversation.Voice.Start();
        }
    }

    // An image attached from the bar's results starts a conversation that has it attached and no message yet, and the bar grows
    // into it like it does for a question: the user types what they want to know about the picture. Only the bar attaches.
    private void OnAttachRequested(object? sender, ImageItem image) => GrowWithAttachment(() => _conversation.StartWithAttachment(image));

    // The same for a document: the conversation starts with its file name above the composer, to ask about it.
    private void OnDocumentAttachRequested(object? sender, DocumentAttachment document) =>
        GrowWithAttachment(() => _conversation.StartWithDocument(document));

    // Text from the clipboard history starts a conversation that has it attached, as a selection does, with the quick actions to pick from.
    private void OnTextAttachRequested(object? sender, TextAttachment text) =>
        GrowWithAttachment(() => _conversation.StartWithSelection(text));

    // Pictures pasted into the bar start a conversation that has them attached, with what was typed so far in its composer to finish there.
    private void OnPicturesPasted(object? sender, IReadOnlyList<ImageItem> pictures)
    {
        if (pictures.Count == 0)
        {
            return;
        }

        var typed = _bar.Query;
        GrowWithAttachment(() =>
        {
            _conversation.StartWithAttachment(pictures[0]);
            foreach (var picture in pictures.Skip(1))
            {
                _conversation.Attach(picture);
            }

            if (!string.IsNullOrWhiteSpace(typed))
            {
                _conversation.Draft = typed;
            }
        });
    }

    private void GrowWithAttachment(Action start)
    {
        if (_window.State != AssistantWindowState.Compact)
        {
            return;
        }

        var keepListening = _bar.Voice.IsListening;
        _questionAsked = true;
        start();
        _bar.Voice.Stop();
        _window.ExpandToConversation();
        if (keepListening)
        {
            _conversation.Voice.Start();
        }
    }

    private void OnHidden(object? sender, EventArgs e)
    {
        ClearAskedQuestion();
        _draggedTo = null;
        _shown = false;
        _openedByWakeWord = false;

        // Dismissing the Assistant silences it, whatever way it went.
        _speech?.Stop();
    }

    // Voice input ended. A bar that the wake word opened, and that heard nothing, has nothing to wait for and goes.
    private void OnVoiceEnded(object? sender, VoiceUtterance utterance)
    {
        if (utterance.HasRequest)
        {
            _openedByWakeWord = false;
            return;
        }

        if (utterance.FromWakeWord && _openedByWakeWord && _window.State == AssistantWindowState.Compact && _bar.Query.Length == 0)
        {
            _openedByWakeWord = false;
            _window.Dismiss();
        }
    }

    // The bar's draft is cleared once it is asked, but not before the text it shows has faded with the bar.
    private void ClearAskedQuestion()
    {
        if (_questionAsked)
        {
            _questionAsked = false;
            _bar.Query = "";
            _bar.Results.ResetScope();
        }
    }
}
