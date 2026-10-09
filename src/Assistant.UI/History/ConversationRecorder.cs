using System.Threading.Channels;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.History;

/// <summary>
/// Saves the conversation's messages to the local history as they are asked and answered, one at a time and in the order
/// they were recorded, without ever holding up the conversation (PROJECT_SPEC §3.5).
/// </summary>
/// <remarks>
/// A message is read on the caller's thread (the UI thread), where its parts are safe to read, and written on a thread of
/// its own, so the UI never waits for the disk. Writing goes through <see cref="IConversationService"/>, which writes
/// nothing while history is off. A message that cannot be saved is logged by the type of the failure alone and skipped:
/// the conversation goes on, and the next message is tried.
/// </remarks>
internal sealed partial class ConversationRecorder : IConversationRecorder, IAsyncDisposable, IDisposable
{
    private readonly IConversationService _history;
    private readonly MessageMapper _mapper;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    // How long disposing waits for messages still to be written.
    private static readonly TimeSpan DisposeTime = TimeSpan.FromSeconds(3);

    public ConversationRecorder(
        IConversationService history, MessageMapper mapper, TimeProvider clock, ILogger<ConversationRecorder> logger)
    {
        _history = history;
        _mapper = mapper;
        _clock = clock;
        _logger = logger;
        _writer = Task.Run(WriteAsync);
    }

    /// <inheritdoc/>
    public void Record(Guid conversationId, MessageViewModel message)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            // Read now: the message may grow while it waits its turn.
            _jobs.Writer.TryWrite(new Job(conversationId, _mapper.ToDomain(message), _clock.GetUtcNow(), null));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whatever is wrong with the message, the conversation on screen is untouched.
            LogNotRecorded(_logger, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Completes when every message recorded before the call has been written, or has failed to be. It waits no longer than
    /// <paramref name="timeout"/>, and returns whether it did not have to give up.
    /// </summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_jobs.Writer.TryWrite(new Job(Guid.Empty, null, default, done)))
        {
            return true;
        }

        try
        {
            await done.Task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _jobs.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    /// <summary>
    /// Stops taking messages and waits, for a moment, for the ones already taken to be written. The container calls this
    /// when the application ends.
    /// </summary>
    public void Dispose()
    {
        _jobs.Writer.TryComplete();
        _writer.Wait(DisposeTime);
    }

    private async Task WriteAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (job.Message is null)
            {
                job.Done?.TrySetResult();
                continue;
            }

            try
            {
                await _history.SaveMessageAsync(job.Conversation, job.Message, job.ChangedAt).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Whatever went wrong, the conversation on screen is untouched, and the next message is tried.
                LogNotSaved(_logger, exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(EventId = 2400, Level = LogLevel.Warning, Message = "A message could not be recorded: {ExceptionType}")]
    private static partial void LogNotRecorded(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2401, Level = LogLevel.Warning, Message = "A message could not be saved to the history: {ExceptionType}")]
    private static partial void LogNotSaved(ILogger logger, string exceptionType);

    private sealed record Job(Guid Conversation, Message? Message, DateTimeOffset ChangedAt, TaskCompletionSource? Done);
}
