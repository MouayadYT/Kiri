using System.IO;
using Assistant.Core.Domain;
using Assistant.Core.Storage;
using Assistant.Data;
using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Assistant.UI.Bootstrap;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The whole way from a question in the floating conversation, through the recorder into a real SQLite database, to the
/// History window's list, its messages and its search, as the app puts them together, with a restart in between.
/// </summary>
public sealed class SavedHistoryEndToEndTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-ui-history-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemorySettingsService _settings = new();
    private readonly SteppingClock _clock = new(Start);
    private readonly RecordingClipboard _clipboard = new();
    private readonly List<IAsyncDisposable> _recorders = [];

    private DatabaseOptions Options => new(
        Path.Combine(_root, "data", DatabaseOptions.DatabaseFileName), Path.Combine(_root, "data", DatabaseOptions.BackupsFolderName));

    // The history as the app has it after it starts: a database that may already hold what an earlier run saved.
    private (SqliteConversationService Service, ConversationRecorder Recorder, ConversationHistorySource Source) StartApp()
    {
        var options = Options;
        var factory = new DatabaseConnectionFactory(options);
        var backups = new DatabaseBackup(options, _clock, NullLogger<DatabaseBackup>.Instance);
        var migrator = new DatabaseMigrator(
            MigrationCatalog.LoadDefault(), new SchemaVersionRepository(), backups, _clock, NullLogger<DatabaseMigrator>.Instance);
        var conversations = new ConversationRepository();
        var service = new SqliteConversationService(
            factory,
            new DatabaseInitializer(factory, migrator),
            conversations,
            new MessageRepository(),
            new ConversationSearchRepository(conversations),
            backups,
            _settings,
            NullLogger<SqliteConversationService>.Instance);
        var mapper = new MessageMapper(_clipboard);
        var recorder = new ConversationRecorder(service, mapper, _clock, NullLogger<ConversationRecorder>.Instance);
        _recorders.Add(recorder);
        return (service, recorder, new ConversationHistorySource(service, mapper));
    }

    private ConversationViewModel Panel(ScriptedAnswers answers, ConversationRecorder recorder) =>
        new(new VoiceInputViewModel(new NoMicrophone()), answers, _clock, activity: null, recorder: recorder);

    private HistoryViewModel Window(ConversationHistorySource source, ConversationRecorder recorder, ScriptedAnswers? answers = null) =>
        new(_clock, source, answers, recorder, TimeSpan.Zero);

    private static ScriptedAnswers Says(string words, params MessageContent[] parts)
    {
        var answers = new ScriptedAnswers();
        answers.Respond = (_, _, show, _) =>
        {
            var answer = new MessageViewModel(MessageRole.Assistant, words);
            foreach (var part in parts)
            {
                answer.Content.Add(part);
            }

            show(answer);
            return Task.CompletedTask;
        };
        return answers;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var recorder in _recorders)
        {
            await recorder.DisposeAsync();
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }
    }

    [Fact]
    public async Task AConversationAskedInThePanelIsThereAfterARestart_WithItsTitlePreviewAndMessages()
    {
        var (_, recorder, _) = StartApp();
        var panel = Panel(Says("It is 19.", new CalculationResult("9 + 10", "19")), recorder);
        panel.StartNew("What is 9+10?");
        var question = panel.Messages[0];
        var answer = panel.Messages[1];
        Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));

        // A new run of the app: nothing is remembered but what the database holds.
        var (_, restartedRecorder, source) = StartApp();
        var history = Window(source, restartedRecorder);
        await history.RefreshAsync();

        var card = Assert.Single(history.Conversations);
        Assert.Equal(panel.Id, card.Id);
        Assert.Equal("What is 9+10?", card.Title);
        Assert.Equal("It is 19.", card.Preview);
        Assert.False(card.IsLoaded);
        Assert.Equal(Start, card.UpdatedAt);

        history.Selected = card;
        await WaitUntil(() => card.IsLoaded);
        Assert.Equal([question.Id, answer.Id], card.Messages.Select(message => message.Id));
        Assert.Equal("What is 9+10?", card.Messages[0].Text);
        Assert.Equal(
            [typeof(TextContent), typeof(CalculationResult)],
            card.Messages[1].Content.Select(part => part.GetType()));
        Assert.Equal("19", ((CalculationResult)card.Messages[1].Content[1]).Result);
        Assert.Equal(MessageStatus.Complete, card.Messages[1].Status);
    }

    [Fact]
    public async Task AConversationContinuedInTheHistoryWindowIsSavedInTheSameConversation_AndComesBackWhole()
    {
        var (_, recorder, source) = StartApp();
        var panel = Panel(Says("First answer."), recorder);
        panel.StartNew("First question");
        Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));

        var history = Window(source, recorder, Says("Second answer."));
        await history.RefreshAsync();
        history.Selected = history.Conversations[0];
        await WaitUntil(() => history.Selected!.IsLoaded);
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(history.Send("Second question"));
        await WaitUntil(() => !history.IsAnswering);
        Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));

        var (_, _, restartedSource) = StartApp();
        var loaded = await restartedSource.LoadMessagesAsync(panel.Id);
        Assert.NotNull(loaded);
        Assert.Equal(["First question", "First answer.", "Second question", "Second answer."], loaded.Select(message => message.Text));
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.User, MessageRole.Assistant],
            loaded.Select(message => message.Role));
        var listed = Assert.Single(await restartedSource.ListAsync());
        Assert.Equal(Start.AddMinutes(30), listed.UpdatedAt);
    }

    [Fact]
    public async Task TheSearchFindsWhatWasSaved_ShowsTheWordsThatMatched_AndOpensTheConversationAtThem()
    {
        var (_, recorder, source) = StartApp();
        var weatherAnswers = Says("Saturday looks sunny with a high of 22 degrees, and Sunday brings light rain.");
        var pastaAnswers = Says("Toss the spinach and garlic with the pasta and lemon.");
        var weather = Panel(weatherAnswers, recorder);
        weather.StartNew("What is the weather this weekend?");
        _clock.Advance(TimeSpan.FromMinutes(20));
        var pasta = Panel(pastaAnswers, recorder);
        pasta.StartNew("What can I cook with pasta?");
        Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));
        var history = Window(source, recorder);
        await history.RefreshAsync();

        history.SearchText = "sunny";
        await history.WhenSearchSettledAsync();

        var shown = history.Conversations.Where(conversation => conversation.IsShown).ToArray();
        var result = Assert.Single(shown);
        Assert.Equal("What is the weather this weekend?", result.Title);
        Assert.Contains("sunny", result.Preview, StringComparison.Ordinal);
        var match = Assert.Single(result.PreviewMatches);
        Assert.Equal("sunny", result.Preview.Substring(match.Start, match.Length));
        Assert.Equal(weather.Messages[1].Id, result.MatchedMessageId);

        // Words from the question and from the answer together, and a word that begins what was typed.
        history.SearchText = "weath sunny";
        await history.WhenSearchSettledAsync();
        Assert.Equal([result.Id], history.Conversations.Where(conversation => conversation.IsShown).Select(conversation => conversation.Id));

        history.SearchText = "";
        await history.WhenSearchSettledAsync();
        Assert.Equal(2, history.Conversations.Count(conversation => conversation.IsShown));
    }

    [Fact]
    public async Task WithHistoryOff_NothingIsWritten_AndTheWindowStillShowsTheConversationOpenedInIt()
    {
        _settings.HistoryEnabled = false;
        var (service, recorder, source) = StartApp();
        var panel = Panel(Says("Private answer."), recorder);

        panel.StartNew("A private question");
        Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));
        var history = Window(source, recorder);
        history.Open(panel.Id, panel.Messages, Start);
        await history.RefreshAsync();

        // Nothing of the conversation is in the database, which a restart would read.
        Assert.Empty(await service.ListAsync());
        Assert.Empty(await StartApp().Service.ListAsync());
        var card = Assert.Single(history.Conversations);
        Assert.Equal("A private question", card.Title);
        Assert.Equal("Private answer.", card.Preview);
    }

    [Fact]
    public async Task AnAttachedImageIsSavedAsAPath_AndTheCardShowsItAfterARestart()
    {
        var (_, recorder, _) = StartApp();
        var answers = new ScriptedAnswers
        {
            Respond = (_, question, show, _) =>
            {
                question.Attach(new ImageItem("lake.jpg", @"C:\Pictures\lake.jpg"));
                show(new MessageViewModel(MessageRole.Assistant, "A glacial lake."));
                return Task.CompletedTask;
            },
        };
        var panel = Panel(answers, recorder);
        panel.StartNew("What is this?");
        Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));

        var (_, restartedRecorder, source) = StartApp();
        var history = Window(source, restartedRecorder);
        await history.RefreshAsync();

        var card = Assert.Single(history.Conversations);
        Assert.Equal("lake.jpg", card.Image?.Name);
        Assert.Equal(@"C:\Pictures\lake.jpg", card.Image?.Path);
        Assert.Equal("A glacial lake.", card.Preview);
    }

    [Fact]
    public async Task AnAttachedDocumentIsSavedAsItsNameAndPlaceOnly_AndComesBackOnItsMessageAfterARestart()
    {
        var (_, recorder, _) = StartApp();
        var answers = Says("It says hello.");
        var panel = Panel(answers, recorder);
        panel.StartWithDocument(new DocumentAttachment("Notes.txt", @"C:\Docs\Notes.txt"));
        panel.Ask("What does it say?");
        Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));

        var (restarted, _, _) = StartApp();
        var saved = await restarted.GetAsync(Assert.Single(await restarted.ListAsync()).Id);

        var question = Assert.Single(saved!.Messages, message => message.Role == MessageRole.User);
        var descriptor = Assert.Single(question.ContextItems);
        Assert.Equal((ContextItemType.File, "Notes.txt", @"C:\Docs\Notes.txt"), (descriptor.Type, descriptor.DisplayName, descriptor.FilePath));
        Assert.Null(descriptor.Text);

        var back = new MessageMapper(_clipboard).ToViewModel(question);
        Assert.Equal(("Notes.txt", @"C:\Docs\Notes.txt"), (back.Document!.Name, back.Document.Path));
        Assert.Empty(back.Attachments);
    }

    [Fact]
    public async Task AStoppedAnswerComesBackAsStopped_WithTheWordsItHad()
    {
        var (_, recorder, _) = StartApp();
        var shown = new TaskCompletionSource();
        var answers = new ScriptedAnswers
        {
            Respond = async (_, _, show, cancellation) =>
            {
                var partial = new MessageViewModel(MessageRole.Assistant, "Once upon a") { Status = MessageStatus.Answering };
                show(partial);
                shown.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                catch (OperationCanceledException)
                {
                    partial.Status = MessageStatus.Stopped;
                    throw;
                }
            },
        };
        var panel = Panel(answers, recorder);
        panel.StartNew("Tell me a story");
        await shown.Task;
        panel.Stop();

        // The stopped answer is recorded a moment after it is stopped, when its provider has wound up.
        var (_, _, source) = StartApp();
        IReadOnlyList<MessageViewModel>? messages = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (messages is not { Count: 2 })
        {
            Assert.True(DateTime.UtcNow < deadline, "The stopped answer was not saved in time.");
            Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));
            messages = await source.LoadMessagesAsync(panel.Id);
            await Task.Delay(5);
        }

        Assert.Equal(MessageStatus.Stopped, messages[1].Status);
        Assert.Equal("Once upon a", messages[1].Text);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeOpenedLeavesTheWindowWithWhatItHas_AndTheConversationUnharmed()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "data"), "a file where the data folder belongs");
        var (_, recorder, source) = StartApp();
        var panel = Panel(Says("An answer."), recorder);

        panel.StartNew("A question");
        Assert.True(await recorder.FlushAsync(TimeSpan.FromSeconds(10)));
        var history = Window(source, recorder);
        await history.RefreshAsync();

        Assert.Equal(["A question", "An answer."], panel.Messages.Select(message => message.Text));
        Assert.Empty(history.Conversations);
    }

    [Fact]
    public void TheAppWiresTheHistory_ToTheRealDatabaseThroughTheRecorderAndTheWindowsSource()
    {
        using var host = AppHost.Create();

        Assert.IsType<SqliteConversationService>(host.Services.GetRequiredService<Assistant.Core.Contracts.IConversationService>());
        Assert.IsType<ConversationRecorder>(host.Services.GetRequiredService<IConversationRecorder>());
        Assert.IsType<ConversationHistorySource>(host.Services.GetRequiredService<IHistorySource>());
        Assert.NotNull(host.Services.GetRequiredService<HistoryViewModel>());
        Assert.NotNull(host.Services.GetRequiredService<ConversationViewModel>());

        // The panel and the window record through the same recorder, so their messages are saved in the order they happen.
        Assert.Same(
            host.Services.GetRequiredService<ConversationRecorder>(),
            host.Services.GetRequiredService<IConversationRecorder>());
    }

    [Fact]
    public async Task TheApplicationOpensTheHistoryWhenItStarts_AndWritesTheLastMessagesBeforeItStops()
    {
        // The application's own container, over a data folder of the test's, started and stopped as the bootstrapper does.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(_root));
        using var host = builder.Build();

        await host.StartAsync();
        var conversation = Guid.NewGuid();
        var recorder = host.Services.GetRequiredService<IConversationRecorder>();
        recorder.Record(conversation, new MessageViewModel(MessageRole.User, "Written just before the application stops"));
        await host.StopAsync();
        host.Dispose();

        // A new run reads what the first one left.
        var (_, _, source) = StartApp();
        var saved = await source.LoadMessagesAsync(conversation);
        Assert.NotNull(saved);
        Assert.Equal(["Written just before the application stops"], saved.Select(message => message.Text));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }
}
