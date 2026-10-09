using Assistant.UI.Messages;

namespace Assistant.UI.ViewModels;

/// <summary>
/// Runs one answer at a time for a view model that shows a conversation, and keeps it stoppable: the floating
/// conversation and the History window both ask questions through one (PROJECT_SPEC §4.2, §4.3). The answer streams in
/// through an <see cref="IAnswerProvider"/>; stopping it, or asking another question, ends its wait or its stream and
/// keeps what it said. Each question and each answer is recorded as it is asked and as it ends, so the conversation is
/// saved a message at a time (<see cref="IConversationRecorder"/>).
/// </summary>
/// <remarks>
/// An answer can hold a part that, once the user has acted on it, asks the conversation to go on with a request that was set aside
/// (<see cref="IContinuingContent"/>: the approval panel of an integration, step 110). The coordinator listens for it on every answer it shows
/// and runs what it asks as the next answer, the same way as any: its own Stop button and Searching chip, and recorded as it ends. It waits for an
/// answer that is on its way, and never stops one that the user asked for in the meantime.
/// </remarks>
internal sealed class AnswerCoordinator(IAnswerProvider answers, IConversationRecorder? recorder = null)
{
    private CancellationTokenSource? _pending;
    private bool _isStreaming;

    /// <summary>Raised when <see cref="IsAnswering"/> or <see cref="IsStreaming"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether an answer is still on its way or still streaming in.</summary>
    public bool IsAnswering => _pending is not null;

    /// <summary>Whether an answer is shown and still streaming in, as opposed to still on its way.</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (_isStreaming != value)
            {
                _isStreaming = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Stops the answer on its way or streaming in, if any. What it said stays, and the answer is marked as stopped.
    /// </summary>
    public void Cancel()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            pending.Cancel();
            IsStreaming = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Asks <paramref name="question"/>, the user's message that is the next question of the conversation <paramref name="conversationId"/>, and
    /// hands the answer to <paramref name="show"/> once it has something to show. An answer that is stopped may still
    /// show itself, marked as stopped; <paramref name="isCurrent"/> says whether the conversation is still the one the
    /// view model holds, and an answer for one it no longer holds is never shown. <paramref name="earlier"/> is what the
    /// conversation held before the question, which the provider may use to remember it.
    /// </summary>
    public Task AskAsync(
        Guid conversationId, MessageViewModel question, Action<MessageViewModel> show, Func<bool> isCurrent,
        IReadOnlyList<MessageViewModel>? earlier = null) =>
        RunAsync(
            conversationId,
            question,
            show,
            isCurrent,
            earlier,
            (shown, token) => answers.StreamAnswerAsync(conversationId, question, shown, token));

    // Runs one answer: the question's, or, without one, what an earlier answer set aside. Whichever it is, the answer is the one that is
    // stoppable now, is shown through show while the conversation is the current one, and is saved as it stands however it ends.
    private async Task RunAsync(
        Guid conversationId, MessageViewModel? question, Action<MessageViewModel> show, Func<bool> isCurrent,
        IReadOnlyList<MessageViewModel>? earlier, Func<Action<MessageViewModel>, CancellationToken, Task> stream)
    {
        Cancel();
        var pending = _pending = new CancellationTokenSource();
        IsStreaming = false;
        Changed?.Invoke(this, EventArgs.Empty);

        // What the conversation held before is told to a provider that does not remember it (one opened from the saved history).
        if (earlier is { Count: > 0 })
        {
            answers.Resume(conversationId, earlier);
        }

        // The question is saved as it is asked, so it is there even if the answer never comes.
        if (question is not null)
        {
            recorder?.Record(conversationId, question);
        }

        MessageViewModel? reply = null;
        try
        {
            await stream(
                answer =>
                {
                    // Every answer is saved, also one for a conversation the view model no longer holds: it belongs to
                    // the conversation it was asked in.
                    reply ??= answer;
                    ListenForContinuations(answer, conversationId, show, isCurrent);
                    if (isCurrent())
                    {
                        show(answer);
                        IsStreaming = ReferenceEquals(_pending, pending);
                    }
                },
                pending.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Stopped: what was shown stays.
        }
        finally
        {
            // However the answer ended, it is saved as it stands: whole, stopped or failed. The question is saved again,
            // in case the provider attached something to it, such as the image it asked about.
            if (recorder is not null)
            {
                if (question is not null)
                {
                    recorder.Record(conversationId, question);
                }

                if (reply is not null)
                {
                    recorder.Record(conversationId, reply);
                }
            }

            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
                IsStreaming = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            pending.Dispose();
        }
    }

    // The parts of the answer that may ask for the conversation to go on (the approval panel of an integration): those it has now, and any it is given
    // later, since the panel comes after the Assistant's words.
    private void ListenForContinuations(MessageViewModel answer, Guid conversationId, Action<MessageViewModel> show, Func<bool> isCurrent)
    {
        void Listen(MessageContent content)
        {
            if (content is IContinuingContent continuing)
            {
                continuing.ContinuationRequested += (_, continuation) => _ = ContinueAsync(conversationId, continuation, show, isCurrent);
            }
        }

        foreach (var content in answer.Content)
        {
            Listen(content);
        }

        answer.Content.CollectionChanged += (_, change) =>
        {
            if (change.NewItems is not null)
            {
                foreach (var content in change.NewItems.OfType<MessageContent>())
                {
                    Listen(content);
                }
            }
        };
    }

    // Goes on with a request that was set aside. An answer the user asked for in the meantime is waited for, never stopped: it is theirs, and this is only
    // the end of something they started earlier.
    private async Task ContinueAsync(Guid conversationId, PendingContinuation continuation, Action<MessageViewModel> show, Func<bool> isCurrent)
    {
        try
        {
            await WhenIdleAsync().ConfigureAwait(true);
            await RunAsync(
                conversationId,
                null,
                show,
                isCurrent,
                null,
                (shown, token) => answers.StreamContinuationAsync(conversationId, continuation, shown, token)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing that goes wrong in going on with a request is more than that answer not coming.
        }
    }

    // Completes when no answer is on its way (at once when none is).
    private Task WhenIdleAsync()
    {
        if (_pending is null)
        {
            return Task.CompletedTask;
        }

        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs args)
        {
            if (_pending is null)
            {
                Changed -= Check;
                idle.TrySetResult();
            }
        }

        Changed += Check;
        return idle.Task;
    }
}
