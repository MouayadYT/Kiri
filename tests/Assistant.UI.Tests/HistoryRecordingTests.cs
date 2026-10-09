using System.IO;
using Assistant.Core.Domain;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The conversation is saved to the history a message at a time, as it is asked and answered, without holding up the
/// conversation or being held up by it.
/// </summary>
public sealed class HistoryRecordingTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeHistoryService _history = new();
    private readonly SteppingClock _clock = new(Start);
    private readonly ScriptedAnswers _answers = new();
    private readonly ConversationRecorder _recorder;

    public HistoryRecordingTests()
    {
        _recorder = new ConversationRecorder(
            _history, new MessageMapper(new RecordingClipboard()), _clock, NullLogger<ConversationRecorder>.Instance);
    }

    public async ValueTask DisposeAsync() => await _recorder.DisposeAsync();

    private ConversationViewModel Panel() =>
        new(new VoiceInputViewModel(new NoMicrophone()), _answers, _clock, activity: null, recorder: _recorder);

    private async Task<FakeHistoryService.SavedMessage[]> SavedAsync()
    {
        Assert.True(await _recorder.FlushAsync(Wait), "The recorder did not finish writing.");
        return _history.SavedNow();
    }

    // A stopped answer ends a moment after it is stopped, when its provider has wound up; it is recorded then.
    private async Task<FakeHistoryService.SavedMessage[]> SavedOnceAsync(Func<FakeHistoryService.SavedMessage, bool> recorded)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!_history.SavedNow().Any(recorded))
        {
            Assert.True(DateTime.UtcNow < deadline, "The message was not recorded in time.");
            await Task.Delay(5);
        }

        return await SavedAsync();
    }

    // An answer that shows its words at once and then ends the way the test says.
    private static Func<Guid, MessageViewModel, Action<MessageViewModel>, CancellationToken, Task> Says(
        string words, MessageStatus status = MessageStatus.Complete) =>
        (_, _, show, _) =>
        {
            var answer = new MessageViewModel(MessageRole.Assistant, words) { Status = status };
            show(answer);
            return Task.CompletedTask;
        };

    [Fact]
    public async Task AQuestionIsRecordedAsItIsAsked_BeforeItsAnswerExists()
    {
        var never = new TaskCompletionSource();
        _answers.Respond = (_, _, _, _) => never.Task;
        var panel = Panel();

        panel.StartNew("What is 9+10?");

        var saved = await SavedAsync();
        var question = Assert.Single(saved);
        Assert.Equal(panel.Id, question.Conversation);
        Assert.Equal(MessageRole.User, question.Message.Role);
        Assert.Equal("What is 9+10?", question.Message.Text);
        Assert.Equal(panel.Messages[0].Id, question.Message.Id);
        Assert.Equal(Start, question.ChangedAt);
        never.SetResult();
    }

    [Fact]
    public async Task TheAnswerIsRecordedWhenItEnds_AfterTheQuestion()
    {
        _answers.Respond = Says("It is 19.");
        var panel = Panel();

        panel.StartNew("What is 9+10?");

        var saved = await SavedAsync();
        Assert.Equal(panel.Messages.Select(message => message.Id).ToArray(), saved.Select(item => item.Message.Id).Distinct().ToArray());
        Assert.Equal(MessageRole.User, saved[0].Message.Role);
        var answer = saved[^1].Message;
        Assert.Equal(MessageRole.Assistant, answer.Role);
        Assert.Equal("It is 19.", answer.Text);
        Assert.Equal(MessageOutcome.Complete, answer.Outcome);
        Assert.All(saved, item => Assert.Equal(panel.Id, item.Conversation));
    }

    [Fact]
    public async Task ATurnIsRecordedAsTheTimesItHappenedAt()
    {
        _answers.Respond = Says("Sure.");
        var panel = Panel();
        panel.StartNew("First");
        await SavedAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));

        panel.Ask("Second");

        var saved = await SavedAsync();
        var second = saved.Last(item => item.Message.Text == "Second");
        Assert.Equal(Start.AddMinutes(5), second.ChangedAt);
        Assert.Equal(Start.AddMinutes(5), second.Message.CreatedAt);
        Assert.Equal(Start, saved.First(item => item.Message.Text == "First").Message.CreatedAt);
    }

    [Fact]
    public async Task AFollowUpIsRecordedInTheSameConversation()
    {
        _answers.Respond = Says("Answer");
        var panel = Panel();
        panel.StartNew("First question");
        var conversation = panel.Id;

        Assert.True(panel.Ask("Second question"));

        var saved = await SavedAsync();
        Assert.Equal(["First question", "Answer", "Second question", "Answer"],
            saved.Select(item => item.Message).DistinctBy(message => message.Id).Select(message => message.Text));
        Assert.All(saved, item => Assert.Equal(conversation, item.Conversation));
    }

    [Fact]
    public async Task AStoppedAnswerIsRecordedAsStopped_WithWhatItSaidBeforeThat()
    {
        var shown = new TaskCompletionSource();
        _answers.Respond = async (_, _, show, cancellation) =>
        {
            var answer = new MessageViewModel(MessageRole.Assistant, "Once upon") { Status = MessageStatus.Answering };
            show(answer);
            shown.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            catch (OperationCanceledException)
            {
                answer.Status = MessageStatus.Stopped;
                throw;
            }
        };
        var panel = Panel();
        panel.StartNew("Tell me a story");
        await shown.Task;

        panel.Stop();

        var answered = (await SavedOnceAsync(item => item.Message.Role == MessageRole.Assistant)).Last().Message;
        Assert.Equal(MessageRole.Assistant, answered.Role);
        Assert.Equal("Once upon", answered.Text);
        Assert.Equal(MessageOutcome.Stopped, answered.Outcome);
    }

    [Fact]
    public async Task AnAnswerStoppedBeforeItsFirstWordsIsRecordedAsAStoppedMessageWithNoText()
    {
        _answers.Respond = async (_, _, show, cancellation) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            catch (OperationCanceledException)
            {
                show(new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Stopped });
                throw;
            }
        };
        var panel = Panel();
        panel.StartNew("Hello");

        panel.Stop();

        var answered = (await SavedOnceAsync(item => item.Message.Role == MessageRole.Assistant)).Last().Message;
        Assert.Equal(MessageRole.Assistant, answered.Role);
        Assert.Equal(string.Empty, answered.Text);
        Assert.Equal(MessageOutcome.Stopped, answered.Outcome);
    }

    [Fact]
    public async Task AFailedAnswerIsRecordedAsFailed_WithTheWordsThatSayWhy()
    {
        _answers.Respond = Says("The model could not finish.", MessageStatus.Failed);
        var panel = Panel();

        panel.StartNew("Hello");

        var answered = (await SavedAsync()).Last().Message;
        Assert.Equal(MessageOutcome.Failed, answered.Outcome);
        Assert.Equal("The model could not finish.", answered.Text);
    }

    [Fact]
    public async Task AnAnswerForAConversationThePanelNoLongerHoldsIsStillSavedInTheOneItBelongsTo()
    {
        _answers.Respond = async (conversation, _, show, cancellation) =>
        {
            if (_answers.Asked.Count == 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                catch (OperationCanceledException)
                {
                    // Stopped by the new conversation: the answer is shown as it stops, after the panel has moved on.
                    show(new MessageViewModel(MessageRole.Assistant, "First answer, cut off") { Status = MessageStatus.Stopped });
                    throw;
                }
            }
        };
        var panel = Panel();
        panel.StartNew("First conversation");
        var first = panel.Id;

        // A new conversation starts while the first is still answering.
        panel.StartNew("Second conversation");
        var second = panel.Id;

        var saved = await SavedOnceAsync(item => item.Message.Text == "First answer, cut off");
        Assert.NotEqual(first, second);
        var cutOff = Assert.Single(saved, item => item.Message.Text == "First answer, cut off");
        Assert.Equal(first, cutOff.Conversation);
        Assert.Equal(MessageOutcome.Stopped, cutOff.Message.Outcome);
        Assert.Contains(saved, item => item.Conversation == second && item.Message.Text == "Second conversation");

        // The panel itself does not show the old conversation's answer.
        Assert.DoesNotContain(panel.Messages, message => message.Text == "First answer, cut off");
    }

    [Fact]
    public async Task WhatTheProviderAttachesToTheQuestionIsSavedWithIt()
    {
        _answers.Respond = (_, question, show, _) =>
        {
            question.Attach(new ImageItem("sign.jpg", @"C:\Pictures\sign.jpg"));
            show(new MessageViewModel(MessageRole.Assistant, "It says stop."));
            return Task.CompletedTask;
        };
        var panel = Panel();

        panel.StartNew("What does the sign say?");

        var question = (await SavedAsync()).Last(item => item.Message.Role == MessageRole.User).Message;
        var item = Assert.Single(question.ContextItems);
        Assert.Equal(ContextItemType.Image, item.Type);
        Assert.Equal(@"C:\Pictures\sign.jpg", item.FilePath);
    }

    [Fact]
    public async Task WhenNothingIsShown_OnlyTheQuestionIsSaved()
    {
        _answers.Respond = (_, _, _, _) => Task.CompletedTask;
        var panel = Panel();

        panel.StartNew("demo orb");

        var saved = await SavedAsync();
        Assert.All(saved, item => Assert.Equal(MessageRole.User, item.Message.Role));
    }

    [Fact]
    public async Task Saving_HappensOffTheThreadThatAsked()
    {
        _answers.Respond = Says("Answer");
        var panel = Panel();

        panel.StartNew("Hello");

        var saved = await SavedAsync();
        Assert.All(saved, item => Assert.NotEqual(Environment.CurrentManagedThreadId, item.ThreadId));
    }

    [Fact]
    public async Task TheConversationIsNotHeldUpBySavingThatIsSlow()
    {
        _history.Hold = new TaskCompletionSource();
        _answers.Respond = Says("Answer");
        var panel = Panel();

        panel.StartNew("Hello");

        // The question and the answer are on screen although nothing has been written yet.
        Assert.Equal(["Hello", "Answer"], panel.Messages.Select(message => message.Text));
        Assert.Empty(_history.SavedNow());
        _history.Hold.SetResult();
        Assert.Equal(3, (await SavedAsync()).Length);
    }

    [Fact]
    public async Task MessagesAreSavedInTheOrderTheyWereRecorded()
    {
        _history.Hold = new TaskCompletionSource();
        var conversation = Guid.NewGuid();
        var messages = Enumerable.Range(0, 12)
            .Select(index => new MessageViewModel(index % 2 == 0 ? MessageRole.User : MessageRole.Assistant, $"Message {index}"))
            .ToArray();

        foreach (var message in messages)
        {
            _recorder.Record(conversation, message);
        }

        _history.Hold.SetResult();
        Assert.Equal(messages.Select(message => message.Id), (await SavedAsync()).Select(item => item.Message.Id));
    }

    [Fact]
    public async Task AMessageIsSavedAsItWasWhenItWasRecorded_NotAsItGrewWhileItWaited()
    {
        _history.Hold = new TaskCompletionSource();
        var conversation = Guid.NewGuid();
        var answer = new MessageViewModel(MessageRole.Assistant, "Early words");

        _recorder.Record(conversation, answer);
        ((TextContent)answer.Content[0]).Text = "Early words and later ones";
        _history.Hold.SetResult();

        Assert.Equal("Early words", Assert.Single(await SavedAsync()).Message.Text);
    }

    [Fact]
    public async Task AMessageThatCannotBeSavedNeverReachesTheConversation_AndTheNextOneIsStillTried()
    {
        var attempts = 0;
        _history.Failure = message => message.Text == "Doomed" && Interlocked.Increment(ref attempts) > 0
            ? new IOException("The disk is full.")
            : null;
        _answers.Respond = Says("Answer");
        var panel = Panel();

        panel.StartNew("Doomed");
        panel.Ask("Fine");

        var saved = await SavedAsync();
        Assert.True(attempts > 0);
        Assert.Equal(["Doomed", "Answer", "Fine", "Answer"], panel.Messages.Select(message => message.Text));
        Assert.Contains(saved, item => item.Message.Text == "Fine");
        Assert.DoesNotContain(saved, item => item.Message.Text == "Doomed");
    }

    [Fact]
    public async Task FlushGivesUpAtItsTimeout_WhenWritingNeverEnds()
    {
        _history.Hold = new TaskCompletionSource();
        _recorder.Record(Guid.NewGuid(), new MessageViewModel(MessageRole.User, "Hello"));

        Assert.False(await _recorder.FlushAsync(TimeSpan.FromMilliseconds(100)));
        _history.Hold.SetResult();
    }

    [Fact]
    public async Task TheHistoryWindowRecordsWhatIsSaidInIt_InTheConversationItContinues()
    {
        _answers.Respond = Says("Continued.");
        var history = new HistoryViewModel(_clock, null, _answers, _recorder);
        var id = Guid.NewGuid();
        var open = history.Open(id, [new MessageViewModel(MessageRole.User, "Earlier"), new MessageViewModel(MessageRole.Assistant, "Earlier answer")], Start);

        Assert.True(history.Send("And now?"));

        var saved = await SavedAsync();
        Assert.All(saved, item => Assert.Equal(id, item.Conversation));
        Assert.Equal(["And now?", "Continued."], saved.Select(item => item.Message).DistinctBy(message => message.Id).Select(message => message.Text));
        Assert.Equal(open.Messages[^1].Id, saved[^1].Message.Id);
    }
}
