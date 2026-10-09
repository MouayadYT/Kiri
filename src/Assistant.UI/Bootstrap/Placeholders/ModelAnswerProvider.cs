using System.Windows.Threading;
using Assistant.Core.Audit;
using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.Tools;
using Assistant.Core.ModelHosting;
using Assistant.Core.Orchestration;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>
/// Streams the orchestrator's answer (PROJECT_SPEC §5.5) into the conversation as it comes. The orchestrator builds the
/// prompt; this class keeps a <see cref="ConversationSession"/> for each conversation it is asked in, so a follow-up is
/// answered with the earlier turns of the same conversation (only what the model itself was asked and said: sample
/// answers, and the notes about a model that is missing or failed, are not part of what it is shown). No tools go with a
/// question yet. The pictures and the documents the user attached to a question go with it (the documents as the passages of them
/// that the question needs, or notes on them when they are too long to read at once, <see cref="AttachedDocuments"/>), and the
/// developer's image test (<c>demo image</c>) sends an image
/// of its own. The model loads on the first question, and the
/// Searching chip says "Thinking" until the first words arrive (the model service reports itself). Without a model it
/// says how to set one up; when the model cannot answer it says why, in words that never hold a path.
/// </summary>
/// <remarks>
/// <para>
/// Stopping the answer, by cancelling it or from the Searching chip, stops the model's work on it, keeps what it said
/// and marks the message <see cref="MessageStatus.Stopped"/>, never <see cref="MessageStatus.Failed"/>; an answer
/// stopped before its first words is shown as a stopped message with nothing in it.
/// </para>
/// <para>
/// The context a question carries goes through the <see cref="IContextService"/>, which merges duplicates and orders it by
/// rank; when the model's window cannot hold all of it, a warning (<see cref="ContextWarningContent"/>) leads the answer.
/// </para>
/// <para>
/// The question the Assistant asks before it does something that changes anything (step 115) is put in the answer as it streams, where the user is reading
/// (<see cref="ToolConfirmationContent"/>): the tool is waiting for the answer, and does nothing until the user allows it there.
/// </para>
/// <para>
/// A run of the agent that takes several steps, or in which something went wrong, is shown in the answer as a panel (<see cref="AgentTaskContent"/>, step 117): each
/// tool as it is called, whether it was allowed, how it ended, a button that stops the run, and where it could not go on. It is for the moment; the activity page keeps
/// what was done.
/// </para>
/// </remarks>
internal sealed class ModelAnswerProvider(
    IAssistantOrchestrator orchestrator, TimeProvider clock, IPermissionPolicy? permissions = null,
    AttachedDocuments? documents = null, IContextService? contexts = null, IConversationFiles? files = null,
    ITextClipboard? clipboard = null, Assistant.Tools.Integrations.IIntegrationOffers? offers = null,
    Assistant.UI.Windowing.ISettingsLauncher? settings = null, IAppEventBus? bus = null, IAgentTaskLog? tasks = null,
    Assistant.Core.Home.IHomeAssistant? home = null) : IAnswerProvider
{
    // Who the context that goes through here is supplied by, as its provenance says.
    private const string ContextOrigin = "composer";

    // How many conversations are remembered; the least recently changed is forgotten first.
    private const int RememberedConversations = 32;

    private readonly Dictionary<Guid, ConversationSession> _sessions = [];
    private readonly object _gate = new();
    private readonly AttachedImages _attached = new(permissions);
    private readonly IContextService _contexts =
        contexts ?? new ContextService(new ContextBudgeter(new HeuristicTokenEstimator()), clock);

    // Without a way to read documents, a question that has one says so and asks nothing.
    private readonly AttachedDocuments _documents = documents ?? new AttachedDocuments(permissions, null);

    /// <summary>What it says when the model used tools and then said nothing.</summary>
    internal const string NoWordsAfterToolsText = "I couldn't put an answer together from that. Please ask again, maybe in other words.";

    /// <summary>What it says when no model is set up.</summary>
    internal const string NoModelText =
        "No local model is set up yet. Ask “demo model” to choose a model file (.gguf) and load it, then ask again.";

    /// <inheritdoc/>
    /// <remarks>The model's answers take time, so there is never one at once.</remarks>
    public MessageViewModel? Answer(string question) => null;

    /// <inheritdoc/>
    /// <remarks>Without a conversation to belong to, the question is asked in one of its own.</remarks>
    public Task StreamAnswerAsync(string question, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
        StreamAnswerAsync(ConversationSession.Start(clock), question, [], null, show, cancellationToken, []);

    /// <inheritdoc/>
    public Task StreamAnswerAsync(
        Guid conversationId, string question, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
        StreamAnswerAsync(SessionFor(conversationId), question, [], null, show, cancellationToken, []);

    /// <inheritdoc/>
    /// <remarks>
    /// The images and the document the user attached to the question go to the model with it, read from their files while the
    /// Files permission is on (the document as the passages of it the question needs); when one cannot be read the user is told,
    /// and nothing is asked.
    /// </remarks>
    public Task StreamAnswerAsync(
        Guid conversationId, MessageViewModel question, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
        StreamAnswerAsync((Guid?)conversationId, question, show, cancellationToken);

    /// <summary>
    /// Asks the user's <paramref name="question"/> with the images and the document attached to it, in the conversation with the
    /// given id, or in one of its own without one.
    /// </summary>
    internal async Task StreamAnswerAsync(
        Guid? conversationId, MessageViewModel question, Action<MessageViewModel> show, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(show);
        var attached = await _attached.ReadAsync(question.Attachments, cancellationToken).ConfigureAwait(true);
        if (attached.Problem is { } problem)
        {
            show(new MessageViewModel(MessageRole.Assistant, problem) { CreatedAt = clock.GetUtcNow() });
            return;
        }

        // The attached files are known to the conversation from now on, so that the model can read one again by name when a later
        // question is about it.
        if (question.Documents.Count > 0 && conversationId is { } known)
        {
            files?.Offer(
                known, question.Documents.Select(attachedFile => new SearchResultItem(SearchResultItemType.File, attachedFile.Name, attachedFile.Path)));
        }

        // The files are read for this question: the passages of them that the question's words point to, or, when they are too long
        // to read at once, the notes the model takes on each piece of them.
        AttachedDocumentResult document;
        try
        {
            document = await _documents.ReadAsync(question.Documents, question.Text, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Stopped from the Searching chip (Esc, or pressing it) while the files were read: said as a stopped answer, like the
            // model's, so the conversation does not just go quiet.
            show(new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Stopped, CreatedAt = clock.GetUtcNow() });
            return;
        }
        catch (ModelNotSetUpException)
        {
            show(new MessageViewModel(MessageRole.Assistant, NoModelText) { CreatedAt = clock.GetUtcNow() });
            return;
        }
        catch (ModelHostException failure)
        {
            // The model could not take notes on the files, so it cannot answer from them either.
            show(new MessageViewModel(MessageRole.Assistant, ModelErrorText.Describe(failure))
            {
                Status = MessageStatus.Failed,
                CreatedAt = clock.GetUtcNow(),
            });
            return;
        }

        if (document.Problem is { } documentProblem)
        {
            show(new MessageViewModel(MessageRole.Assistant, documentProblem) { CreatedAt = clock.GetUtcNow() });
            return;
        }

        // The text the user attached goes as it is, as what they selected for this question.
        var texts = question.TextAttachments.Select(text => new ContextItem(Guid.NewGuid(), ContextItemType.Selection, text.Name)
        {
            Text = text.Text,
            Source = ContextSource.UserSelected,
            WebPage = text.WebPage,
        }).ToList();

        // A selection from a page that came with the page's text around it (step 88) has that text as context of its own, a page item that
        // ranks after the selection, so a conversation that is short of room gives it up first.
        foreach (var text in question.TextAttachments)
        {
            if (text.WebPage is { HasNearbyContext: true } page && NearbyPageText.Describe(page) is { } nearby)
            {
                texts.Add(new ContextItem(Guid.NewGuid(), ContextItemType.Page, NearbyPageText.ContextName)
                {
                    Text = nearby,
                    Source = ContextSource.CurrentScreen,
                    WebPage = page,
                });
            }
        }

        await StreamAnswerAsync(
            conversationId, question.Text, [.. attached.Items, .. texts, .. document.Items], null, show, cancellationToken,
            document.Notices)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Asks <paramref name="question"/> with <paramref name="context"/>, such as an image, in the conversation with the
    /// given id, or in one of its own without one. The answer starts with <paramref name="lead"/>, content for the user
    /// that the model is never shown (such as the image asked about), when there is one, and, with the answer's first words, the
    /// <paramref name="notices"/> about how much of the context was used, which are also for the user alone.
    /// </summary>
    internal Task StreamAnswerAsync(
        Guid? conversationId,
        string question,
        IReadOnlyList<ContextItem> context,
        MessageContent? lead,
        Action<MessageViewModel> show,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? notices = null) =>
        StreamAnswerAsync(
            conversationId is { } id ? SessionFor(id) : ConversationSession.Start(clock),
            question, context, lead, show, cancellationToken, notices ?? []);

    private Task StreamAnswerAsync(
        ConversationSession session,
        string question,
        IReadOnlyList<ContextItem> context,
        MessageContent? lead,
        Action<MessageViewModel> show,
        CancellationToken cancellationToken,
        IReadOnlyList<string> notices) =>
        StreamAsync(
            session,
            context,
            lead,
            show,
            cancellationToken,
            notices,
            () => orchestrator.AskAsync(session, question, _contexts.PendingItems(session.Conversation.Id), cancellationToken: cancellationToken));

    /// <inheritdoc/>
    /// <remarks>
    /// The request that was set aside for an integration is the user's message already in the conversation, so none is added: the orchestrator takes the
    /// offer's words out of what the model remembers and answers the request with the integration that is installed now, or says that it was not installed.
    /// </remarks>
    public Task StreamContinuationAsync(
        Guid conversationId, PendingContinuation continuation, Action<MessageViewModel> show, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var session = SessionFor(conversationId);
        return StreamAsync(
            session,
            [],
            null,
            show,
            cancellationToken,
            [],
            () => orchestrator.ContinueAsync(session, continuation.Pending, continuation.Outcome, cancellationToken));
    }

    // Streams the chunks of one turn into the conversation as an answer, whichever turn it is: a question's, or the going back to a request.
    private async Task StreamAsync(
        ConversationSession session,
        IReadOnlyList<ContextItem> context,
        MessageContent? lead,
        Action<MessageViewModel> show,
        CancellationToken cancellationToken,
        IReadOnlyList<string> notices,
        Func<IAsyncEnumerable<AssistantResponseChunk>> chunks)
    {
        ArgumentNullException.ThrowIfNull(show);
        var answer = new StreamedAnswer(show, Dispatcher.CurrentDispatcher, clock.GetUtcNow());
        if (lead is not null)
        {
            answer.Add(lead);
        }

        // While the answer streams, the questions of its tool calls ("may I send this?") are put in it, and a question still waiting when it ends is withdrawn.
        using var confirmations = bus is null
            ? null
            : new ToolConfirmationListener(bus, session.Conversation.Id, Dispatcher.CurrentDispatcher, answer.Add);

        // A run of the agent that takes several steps is shown in the answer as it goes (step 117), with the button that stops it and, when it cannot go on, where.
        using var taskPanels = tasks is null
            ? null
            : new AgentTaskListener(
                tasks, session.Conversation.Id, Dispatcher.CurrentDispatcher, answer.Add, clock,
                settings is null ? null : () => settings.Show(Assistant.UI.Settings.SettingsSection.Activity));

        // A device of the home that was switched is shown as the device itself. A message that was sent is shown as it went, after the Assistant's words
        // about it ("It's sent."), as in the reference. What was done to get there (the run's steps, the questions the user answered) is put away behind
        // the button with three dots under the answer.
        var sent = new List<MessageContent>();

        // What the user is told about the context goes in front of the answer, once the model has begun: a question the model
        // never takes (none is set up, or it cannot load) has no notice about context it did not read.
        var pending = new Queue<string>(notices);

        // The context goes through the context service, which merges what is the same and puts it in rank order; the items this
        // question supplied and the conversation did not take (no model was set up, say) are taken back afterwards, so they
        // do not wait for the next question.
        var conversationId = session.Conversation.Id;
        var supplied = new List<Guid>(context.Count);
        foreach (var item in context)
        {
            var added = _contexts.Add(conversationId, item, ContextOrigin);
            if (added.Outcome is ContextAddOutcome.Added or ContextAddOutcome.Merged)
            {
                supplied.Add(added.Item.Id);
            }
        }

        var warnings = new List<string>();
        var toolsRan = false;
        var said = false;
        try
        {
            // The answer before it may still be winding up after it was stopped.
            await session.WhenIdleAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
            await foreach (var chunk in chunks().ConfigureAwait(true))
            {
                while (pending.Count > 0)
                {
                    answer.AddParagraph(pending.Dequeue());
                }

                // The warnings come first and together: one part of the answer says all that did not fit.
                if (chunk.Type == AssistantResponseChunkType.ContextWarning)
                {
                    warnings.Add(chunk.Text!);
                    continue;
                }

                ShowWarnings(answer, warnings);
                switch (chunk.Type)
                {
                    case AssistantResponseChunkType.TextDelta:
                        said = true;
                        answer.Append(chunk.Text!);
                        break;
                    case AssistantResponseChunkType.Notice:
                        answer.AddParagraph(chunk.Text!);
                        break;
                    case AssistantResponseChunkType.ToolResult:
                        toolsRan = true;
                        if (ShowAction(answer, sent, chunk.ToolResult!, taskPanels?.Latest))
                        {
                            break;
                        }

                        ShowToolResult(answer, conversationId, chunk.ToolResult!);
                        break;
                    case AssistantResponseChunkType.IntegrationOffer:
                        ShowOffer(answer, chunk.Offer!, chunk.Pending);
                        break;
                }
            }

            ShowWarnings(answer, warnings);

            // The model looked, and then said nothing (it may have used up its words thinking): the user is not left with a silence.
            if (toolsRan && !said)
            {
                answer.AddParagraph(NoWordsAfterToolsText);
            }

            answer.End(MessageStatus.Complete);
        }
        catch (ModelNotSetUpException)
        {
            answer.AddParagraph(NoModelText);
            answer.End(MessageStatus.Complete);
        }
        catch (ModelHostException failure)
        {
            answer.AddParagraph(ModelErrorText.Describe(failure));
            answer.End(MessageStatus.Failed);
        }
        catch (OperationCanceledException)
        {
            // Stopped: by cancelling, or from the Searching chip (Esc, or pressing it) while the model loaded or thought.
            answer.End(MessageStatus.Stopped);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        finally
        {
            foreach (var id in supplied)
            {
                _contexts.Remove(conversationId, id);
            }

            // The message that was sent comes after the words about it, however the answer ended: it was sent.
            foreach (var card in sent)
            {
                answer.Add(card);
            }

            // A run that only became worth showing as it ended (a single step that failed, a stop) is shown before the answer is.
            taskPanels?.Flush();
            answer.Flush();
        }
    }

    // The approval panel of an integration the Assistant found and reviewed: nothing is downloaded or run until the user clicks Install on it, and that
    // click is the only thing that accepts the offer. Without a way to install there is no panel, and so no offer that could be mistaken for one.
    // When the offer was made for a request, what the user decides goes back to it (step 110): the panel asks the conversation to go on, and the
    // request is carried out once the integration is installed.
    private void ShowOffer(StreamedAnswer answer, IntegrationOffer offer, PendingRequest? pending)
    {
        if (offers is null)
        {
            return;
        }

        answer.Add(new IntegrationOfferContent(
            offer,
            (progress, cancellationToken) => offers.AcceptAsync(offer.OfferId, progress, cancellationToken),
            () => offers.Decline(offer.OfferId),
            settings is null ? null : () => settings.Show(Assistant.UI.Settings.SettingsSection.Integrations),
            pending));
    }

    // What did not fit the model's window, said once, in front of the answer's words.
    private static void ShowWarnings(StreamedAnswer answer, List<string> warnings)
    {
        if (warnings.Count > 0)
        {
            answer.Add(new ContextWarningContent(warnings));
            warnings.Clear();
        }
    }

    // What the Assistant did, shown as the thing itself: a device of the home it switched, which goes into the answer at once, and a message it sent,
    // which is kept to follow the answer's words. Says whether the result was one of these.
    private bool ShowAction(StreamedAnswer answer, List<MessageContent> sent, ToolResult result, IAgentTaskView? run)
    {
        if (result.Status != ToolResultStatus.Succeeded)
        {
            return false;
        }

        if (result.ToolName == HomeToolResults.ControlHomeDevice && HomeToolResults.TryReadControlled(result.OutputJson, out var device))
        {
            answer.Add(new HomeDeviceContent(device, home, run));
            return true;
        }

        if (result.ToolName == MessagingToolResults.SendMessage && MessagingToolResults.TryReadSent(result.OutputJson, out var message))
        {
            // The button under the answer copies what was sent, so the card has none of its own.
            sent.Add(new SentMessageContent(message));
            return true;
        }

        return false;
    }

    /// <summary>
    /// What the user is shown of a tool the model called: the files a search found, as the list of files (or the gallery, for pictures),
    /// in the conversation's answer, how much of a file was read when it was not all of it, and a sum the calculator worked out, as the
    /// calculation card with a button that copies the value. The model's own words follow.
    /// </summary>
    private void ShowToolResult(StreamedAnswer answer, Guid conversationId, ToolResult result)
    {
        if (FileToolResults.TryReadFound(result, out var ids, out var pictures))
        {
            var found = files?.Get(conversationId, ids) ?? [];
            if (found.Count > 0)
            {
                answer.Add(pictures
                    ? new ImageCollection(found.Select(file => new ImageItem(file.Item.DisplayName, file.Path)))
                    : FileCollection.From(found.Select(file => file.Item), clock));
            }
        }
        else if (FileToolResults.ReadNotice(result) is { Length: > 0 } notice)
        {
            answer.AddParagraph(notice);
        }
        else if (result is { ToolName: CalculationToolResults.Calculate, Status: ToolResultStatus.Succeeded }
            && CalculationToolResults.TryRead(result.OutputJson, out var calculation))
        {
            answer.Add(new CalculationResult(
                calculation.Expression,
                calculation.Result,
                clipboard is null ? null : new CopyTextCommand(clipboard, calculation.Result),
                calculation.Secondary));
        }
    }

    /// <summary>
    /// Tells the model's memory what a conversation held before, when this is the first time it is asked in it (it was opened from the
    /// saved history): what was said, and the files that were found for it, as the model's own tool call and what it returned.
    /// </summary>
    public void Resume(Guid conversationId, IReadOnlyList<MessageViewModel> earlier)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(conversationId, out var known) && known.Conversation.Messages.Count > 0)
            {
                return;
            }
        }

        SessionFor(conversationId).Resume(ConversationReplay.Rebuild(earlier, conversationId, files), clock.GetUtcNow());
    }

    // The conversation's memory of what it was asked, for a test to look into.
    internal ConversationSession? SessionOf(Guid conversationId)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(conversationId);
        }
    }

    /// <inheritdoc/>
    public void ReleaseContext(Guid conversationId, Guid contextItemId)
    {
        ConversationSession? session;
        lock (_gate)
        {
            _sessions.TryGetValue(conversationId, out session);
        }

        session?.ReleaseImage(contextItemId);
    }

    /// <summary>
    /// Whether the conversation has a part of the screen attached that every question of it is asked about (PROJECT_SPEC §4.6), so
    /// that what the user says ("this", "the second row") is about that, whatever its words.
    /// </summary>
    internal bool HasScreenContext(Guid conversationId) =>
        _contexts.PendingItems(conversationId).Any(item => item is { Retained: true, Type: ContextItemType.Screenshot });

    /// <summary>Whether the conversation has any file that was found or attached in it, which the model can read.</summary>
    internal bool HasFileContext(Guid conversationId) => files?.Has(conversationId) == true;

    /// <summary>
    /// Tells the model's memory of the conversation about a request to find files that the app answered itself, as the model's own
    /// call of the tool that finds files and what it returned, so that what the user says next ("the first one") is understood in it.
    /// </summary>
    internal void RememberFound(Guid conversationId, string question, ToolCall call, ToolResult result, string answer) =>
        SessionFor(conversationId).AddToolExchange(question, call, result, answer, clock.GetUtcNow());

    // The conversation's session: the one it has, or a new one, forgetting the least recently changed of many.
    private ConversationSession SessionFor(Guid conversationId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(conversationId, out var existing))
            {
                return existing;
            }

            if (_sessions.Count >= RememberedConversations)
            {
                var oldest = _sessions.MinBy(pair => pair.Value.Conversation.UpdatedAt).Key;
                _sessions.Remove(oldest);

                // What the context service remembers of the conversation was sent with messages that are gone with the session.
                _contexts.Forget(oldest);
            }

            var session = new ConversationSession(
                new Conversation(conversationId, string.Empty, clock.GetUtcNow(), clock.GetUtcNow()));
            _sessions.Add(conversationId, session);
            return session;
        }
    }

    // The Assistant's message as it streams in: shown with its first words, prose streamed into its last paragraph, and
    // marked with how it ended.
    private sealed class StreamedAnswer(Action<MessageViewModel> show, Dispatcher dispatcher, DateTimeOffset createdAt)
    {
        private readonly MessageViewModel _message = new(MessageRole.Assistant)
        {
            Status = MessageStatus.Answering,
            CreatedAt = createdAt,
        };
        private StreamingText? _prose;
        private bool _shown;

        public void Append(string text)
        {
            if (_prose is not null)
            {
                _prose.Append(text);
                return;
            }

            var content = new TextContent();
            _message.Content.Add(content);
            _prose = new StreamingText(content, dispatcher);
            _prose.Append(text);

            // The first words are there as the message appears.
            _prose.Flush();
            Show();
        }

        // A paragraph of its own, such as a notice; words that follow it start another.
        public void AddParagraph(string text) => Add(new TextContent(text));

        // A part of its own, such as a notice or an image; words that follow it start another paragraph. A part that is put away (the steps of what is
        // being done) does not bring the answer into the conversation by itself: there is nothing to read in it yet.
        public void Add(MessageContent content)
        {
            Flush();
            _prose = null;
            _message.Content.Add(content);
            if (!content.IsTucked)
            {
                Show();
            }
        }

        public void Flush() => _prose?.Flush();

        // Shows the last words and how the answer ended; a stopped answer is shown even with nothing in it, so the
        // conversation says it was stopped.
        public void End(MessageStatus status)
        {
            Flush();
            _message.Status = status;
            if (status == MessageStatus.Stopped || _message.Content.Count > 0)
            {
                Show();
            }
        }

        private void Show()
        {
            if (!_shown)
            {
                _shown = true;
                show(_message);
            }
        }
    }
}
