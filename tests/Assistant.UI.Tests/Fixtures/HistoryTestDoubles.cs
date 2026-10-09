using System.Runtime.ExceptionServices;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.Windows.Audio;

namespace Assistant.UI.Tests;

/// <summary>A clipboard that keeps what was copied.</summary>
internal sealed class RecordingClipboard : ITextClipboard
{
    public List<string> Copied { get; } = [];

    public bool TrySetText(string text)
    {
        Copied.Add(text);
        return true;
    }
}

/// <summary>A clock that stands still until a test moves it.</summary>
internal sealed class SteppingClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Test", start.Offset, "Test", "Test");

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A conversation history in memory: it lists, opens and searches what a test gives it, and keeps every message it is
/// asked to save, with the thread that saved it.
/// </summary>
internal sealed class FakeHistoryService : IConversationService
{
    private readonly object _gate = new();

    public List<ConversationSummary> Summaries { get; } = [];

    public Dictionary<Guid, Conversation> Conversations { get; } = [];

    public Func<string, IReadOnlyList<ConversationSearchResult>> Search { get; set; } = _ => [];

    public List<SavedMessage> Saved { get; } = [];

    /// <summary>Throws for a message, when it returns an exception.</summary>
    public Func<Message, Exception?> Failure { get; set; } = _ => null;

    /// <summary>While set, saving waits for it.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public int ListCalls { get; private set; }

    public Exception? ListFailure { get; set; }

    public sealed record SavedMessage(Guid Conversation, Message Message, DateTimeOffset ChangedAt, int ThreadId);

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        ListCalls++;
        return ListFailure is { } failure
            ? Task.FromException<IReadOnlyList<ConversationSummary>>(failure)
            : Task.FromResult<IReadOnlyList<ConversationSummary>>([.. Summaries]);
    }

    public Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversations.GetValueOrDefault(id));

    public Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(
        string query, int limit = IConversationService.DefaultSearchLimit, CancellationToken cancellationToken = default) =>
        Task.FromResult(Search(query));

    public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task SaveMessageAsync(
        Guid conversationId, Message message, DateTimeOffset changedAt, CancellationToken cancellationToken = default)
    {
        if (Hold is { } hold)
        {
            await hold.Task.ConfigureAwait(false);
        }

        if (Failure(message) is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            Saved.Add(new SavedMessage(conversationId, message, changedAt, Environment.CurrentManagedThreadId));
        }
    }

    public Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public SavedMessage[] SavedNow()
    {
        lock (_gate)
        {
            return [.. Saved];
        }
    }
}

/// <summary>A microphone that never opens, for view models that need one and never use it.</summary>
internal sealed class NoMicrophone : IMicrophoneLevelMeter
{
    public IMicrophoneLevelSession Start(Action<MicrophoneFailure> failed) => new Session();

    private sealed class Session : IMicrophoneLevelSession
    {
        public double Level => 0;

        public void Dispose()
        {
        }
    }
}

/// <summary>Runs work on a thread of its own with a UI apartment, for code that builds WPF objects.</summary>
internal static class OnSta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

/// <summary>An answer provider a test scripts: it says what to show, and when.</summary>
internal sealed class ScriptedAnswers : IAnswerProvider
{
    /// <summary>Runs for each question, with the message to show and the token that stops it.</summary>
    public Func<Guid, MessageViewModel, Action<MessageViewModel>, CancellationToken, Task> Respond { get; set; } =
        (_, _, _, _) => Task.CompletedTask;

    public List<(Guid Conversation, string Question)> Asked { get; } = [];

    public MessageViewModel? Answer(string question) => null;

    public Task StreamAnswerAsync(
        Guid conversationId, MessageViewModel question, Action<MessageViewModel> show, CancellationToken cancellationToken)
    {
        Asked.Add((conversationId, question.Text));
        return Respond(conversationId, question, show, cancellationToken);
    }
}

/// <summary>
/// A History window's source a test scripts: what it lists, what each conversation holds, what a search finds, and when
/// each call answers.
/// </summary>
internal sealed class FakeHistorySource : IHistorySource
{
    public List<HistoryConversation> Listed { get; } = [];

    public Dictionary<Guid, IReadOnlyList<MessageViewModel>?> Messages { get; } = [];

    public Func<string, IReadOnlyList<HistorySearchHit>> Found { get; set; } = _ => [];

    /// <summary>Runs before a listing answers; a test holds the answer back by returning a task that has not completed.</summary>
    public Func<int, CancellationToken, Task> BeforeList { get; set; } = (_, _) => Task.CompletedTask;

    public Func<Guid, CancellationToken, Task> BeforeLoad { get; set; } = (_, _) => Task.CompletedTask;

    public Func<string, CancellationToken, Task> BeforeSearch { get; set; } = (_, _) => Task.CompletedTask;

    public Exception? ListFailure { get; set; }

    public Exception? LoadFailure { get; set; }

    public Exception? SearchFailure { get; set; }

    public int ListCalls { get; private set; }

    public List<Guid> Loads { get; } = [];

    public List<string> Searches { get; } = [];

    public async Task<IReadOnlyList<HistoryConversation>> ListAsync(CancellationToken cancellationToken = default)
    {
        var call = ++ListCalls;

        // What the history held when it was asked, however long it takes to answer.
        var listed = ListFailure is null ? Listed.ToArray() : [];
        await BeforeList(call, cancellationToken);
        return ListFailure is { } failure ? throw failure : listed;
    }

    public async Task<IReadOnlyList<MessageViewModel>?> LoadMessagesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Loads.Add(id);
        await BeforeLoad(id, cancellationToken);
        return LoadFailure is { } failure ? throw failure : Messages.GetValueOrDefault(id);
    }

    public async Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        Searches.Add(query);
        await BeforeSearch(query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return SearchFailure is { } failure ? throw failure : Found(query);
    }
}
