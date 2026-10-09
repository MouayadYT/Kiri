using Assistant.Core.Domain;

namespace Assistant.Core.Orchestration;

/// <summary>
/// The in-memory conversation of one chat, which the orchestrator updates as a turn goes on (PROJECT_SPEC §5.5).
/// </summary>
/// <remarks>
/// A <see cref="Domain.Conversation"/> never changes, so the session holds the current one and replaces it whenever a
/// message is added or grows. Read <see cref="Conversation"/> any time for what the conversation holds now. Saving it
/// is a separate concern (<see cref="Contracts.IConversationService"/>).
/// </remarks>
public sealed class ConversationSession
{
    private readonly object _gate = new();
    private Conversation _conversation;
    private bool _turnRunning;
    private TaskCompletionSource? _turnEnded;

    /// <summary>Creates a session that continues <paramref name="conversation"/>.</summary>
    public ConversationSession(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        _conversation = conversation;
    }

    /// <summary>The conversation as it is now. It is a snapshot: it does not change after it is read.</summary>
    public Conversation Conversation
    {
        get
        {
            lock (_gate)
            {
                return _conversation;
            }
        }
    }

    /// <summary>
    /// A task that completes when the turn running now has ended, or that is already complete when none is running. A
    /// turn that was stopped takes a moment to wind up, so a caller that asks the next question straight after
    /// stopping waits for this first.
    /// </summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _turnEnded?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>Starts a session with a new, empty conversation.</summary>
    public static ConversationSession Start(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        return new ConversationSession(new Conversation(Guid.NewGuid(), string.Empty, now, now));
    }

    /// <summary>
    /// Gives a session with no messages the messages of the conversation it continues, such as one opened from the saved history, so
    /// that the next question is answered with what was said before. A session that already has messages, or is answering, is left
    /// as it is.
    /// </summary>
    /// <returns><see langword="true"/> when the messages were taken.</returns>
    public bool Resume(IEnumerable<Message> earlier, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        lock (_gate)
        {
            if (_turnRunning || _conversation.Messages.Count > 0)
            {
                return false;
            }

            _conversation = _conversation with { Messages = [.. earlier], UpdatedAt = now };
            return true;
        }
    }

    /// <summary>
    /// Adds a question and an answer that the app made with a tool call of its own, such as the files it found for a request to find
    /// them: the question, the call, its result and what was said, as the model would have made them, so that what the user says next
    /// ("the first one", "3 not 4") is understood in what went before and the model sees how the tool is used. Nothing is added
    /// while a turn is running.
    /// </summary>
    /// <returns><see langword="true"/> when the exchange was added.</returns>
    public bool AddToolExchange(string question, ToolCall call, ToolResult result, string answer, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(answer);
        lock (_gate)
        {
            if (_turnRunning)
            {
                return false;
            }

            var messages = new List<Message>(_conversation.Messages.Count + 4);
            messages.AddRange(_conversation.Messages);
            messages.Add(new Message(Guid.NewGuid(), MessageRole.User, question, now));
            messages.Add(new Message(Guid.NewGuid(), MessageRole.Assistant, string.Empty, now) { ToolCalls = [call] });
            messages.Add(new Message(Guid.NewGuid(), MessageRole.Tool, result.OutputJson, now) { ToolResult = result });
            if (answer.Length > 0)
            {
                messages.Add(new Message(Guid.NewGuid(), MessageRole.Assistant, answer, now));
            }

            _conversation = _conversation with { Messages = messages, UpdatedAt = now };
            return true;
        }
    }

    /// <summary>
    /// Lets go of the pixels of the context item <paramref name="itemId"/> (a screenshot the user took off the conversation) in every
    /// message that carries it, so the memory can be freed. The messages keep the item's descriptor, since the question was asked about
    /// it. Works while a turn is running too: that turn has its prompt already.
    /// </summary>
    /// <returns><see langword="true"/> when some message held the item's image.</returns>
    public bool ReleaseImage(Guid itemId)
    {
        lock (_gate)
        {
            var released = false;
            var messages = new List<Message>(_conversation.Messages.Count);
            foreach (var held in _conversation.Messages)
            {
                var message = held;
                if (message.ContextItems.Any(item => item.Id == itemId && !item.ImageData.IsEmpty))
                {
                    released = true;
                    message = message with
                    {
                        ContextItems = [.. message.ContextItems.Select(item => item.Id == itemId ? item with { ImageData = default } : item)],
                    };
                }

                messages.Add(message);
            }

            if (released)
            {
                _conversation = _conversation with { Messages = messages };
            }

            return released;
        }
    }

    // A session answers one message at a time: two turns at once would interleave their messages.
    internal bool TryBeginTurn()
    {
        lock (_gate)
        {
            if (_turnRunning)
            {
                return false;
            }

            _turnRunning = true;
            _turnEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    internal void EndTurn()
    {
        TaskCompletionSource? ended;
        lock (_gate)
        {
            _turnRunning = false;
            ended = _turnEnded;
            _turnEnded = null;
        }

        ended?.TrySetResult();
    }

    // Adds the message, or replaces the last one when it is the same message grown.
    internal void Upsert(Message message, DateTimeOffset now)
    {
        lock (_gate)
        {
            var messages = _conversation.Messages;
            var replaces = messages.Count > 0 && messages[^1].Id == message.Id;
            var changed = new List<Message>(replaces ? messages.Count : messages.Count + 1);
            changed.AddRange(replaces ? messages.Take(messages.Count - 1) : messages);
            changed.Add(message);
            _conversation = _conversation with { Messages = changed, UpdatedAt = now };
        }
    }

    // Changes the messages the conversation holds as a whole: the orchestrator decides what the model remembers of it, such as the offer of an
    // integration that the user has since answered (step 110). It is not for the answer that is streaming in, which Upsert grows.
    internal void Edit(Func<IReadOnlyList<Message>, IReadOnlyList<Message>> change, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            _conversation = _conversation with { Messages = [.. change(_conversation.Messages)], UpdatedAt = now };
        }
    }
}
