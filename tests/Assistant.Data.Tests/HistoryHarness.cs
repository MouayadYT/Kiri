using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Assistant.Data.Tests;

/// <summary>Settings that a test changes, such as turning history off.</summary>
internal sealed class TestSettings : ISettingsService
{
    public AppSettings Current { get; set; } = new();

    public bool HistoryEnabled
    {
        set => Current = Current with { Privacy = Current.Privacy with { HistoryEnabled = value } };
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The conversation history over a real SQLite database in a folder of its own, with the logs it wrote and small builders
/// for the messages a test says.
/// </summary>
internal sealed class HistoryHarness : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    public HistoryHarness()
    {
        Service = CreateService();
    }

    public TestDatabase Database { get; } = new();

    public TestSettings Settings { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public SqliteConversationService Service { get; }

    /// <summary>A moment <paramref name="minutes"/> after the first of the test's day.</summary>
    public static DateTimeOffset At(int minutes) => Start.AddMinutes(minutes);

    public static Message User(string text, int minute, params ContextItem[] context) =>
        new(Guid.NewGuid(), MessageRole.User, text, At(minute)) { ContextItems = context };

    public static Message Answer(string text, int minute, MessageOutcome outcome = MessageOutcome.Complete, params CardMetadata[] cards) =>
        new(Guid.NewGuid(), MessageRole.Assistant, text, At(minute)) { Outcome = outcome, Cards = cards };

    /// <summary>A service over the same database, as the app has after it restarts.</summary>
    public SqliteConversationService CreateService()
    {
        var conversations = new ConversationRepository();
        return new SqliteConversationService(
            Database.Connections,
            Database.CreateInitializer(),
            conversations,
            new MessageRepository(),
            new ConversationSearchRepository(conversations),
            Database.CreateBackup(),
            Settings,
            Logs.CreateFactory().CreateLogger<SqliteConversationService>());
    }

    /// <summary>Saves each message in turn, the conversation changing when the message was made.</summary>
    public async Task SayAsync(Guid conversation, params Message[] messages)
    {
        foreach (var message in messages)
        {
            await Service.SaveMessageAsync(conversation, message, message.CreatedAt);
        }
    }

    public SqliteConnection Open() => Database.Open();

    /// <summary>Everything in the database's files: the database, its write-ahead log and its shared-memory file.</summary>
    public byte[][] Files() =>
    [
        .. new[] { string.Empty, "-wal", "-shm" }
            .Select(suffix => Database.Options.DatabasePath + suffix)
            .Where(File.Exists)
            .Select(ReadShared),
    ];

    /// <summary>Whether <paramref name="text"/>, written the way SQLite writes text (UTF-8), is anywhere in the database's files.</summary>
    public bool FilesContain(string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        return Files().Any(file => file.AsSpan().IndexOf(needle) >= 0);
    }

    public void Dispose() => Database.Dispose();

    // SQLite may have the file open; it allows readers, so this one does too.
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
