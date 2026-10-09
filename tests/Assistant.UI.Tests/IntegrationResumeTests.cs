using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Tools.Integrations;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Going back to a request that was set aside for an integration (PROJECT_SPEC section 4.8, step 110) ----------------

    private static PendingRequest SetAside() => new(Guid.NewGuid(), Guid.NewGuid(), "I didn't install the Todoist integration, so I didn't create a task in Todoist.");

    private static IntegrationOfferContent PanelFor(PendingRequest? pending, Func<IProgress<InstallProgress>, CancellationToken, Task<InstallOutcome>>? install = null) =>
        new(OfferFor(), install ?? ((_, _) => Task.FromResult(InstalledOutcome())), () => { }, null, pending);

    [Fact]
    public void ThePanelAsksTheConversationToGoBackToItsRequestWhenTheIntegrationIsInstalled() => RunSta(() =>
    {
        var pending = SetAside();
        var panel = PanelFor(pending);
        var asked = new List<PendingContinuation>();
        panel.ContinuationRequested += (_, continuation) => asked.Add(continuation);

        Assert.IsAssignableFrom<IContinuingContent>(panel);
        WaitForTask(panel.InstallAsync());

        var continuation = Assert.Single(asked);
        Assert.Same(pending, continuation.Pending);
        Assert.Equal(PendingOutcome.Installed, continuation.Outcome);
    });

    [Fact]
    public void ThePanelSaysNotInstalledWhenTheUserTurnsItDownCancelsWhileInstallingOrItFails() => RunSta(() =>
    {
        // Turned down before installing.
        var declined = PanelFor(SetAside());
        var declinedAsked = new List<PendingContinuation>();
        declined.ContinuationRequested += (_, continuation) => declinedAsked.Add(continuation);
        declined.CancelCommand.Execute(null);
        declined.CancelCommand.Execute(null);
        Assert.Equal(PendingOutcome.NotInstalled, Assert.Single(declinedAsked).Outcome);

        // Cancelled while it installs: the installer stops and answers that it was cancelled.
        var cancelled = PanelFor(SetAside(), async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                return InstallOutcome.Cancel();
            }

            return InstalledOutcome();
        });
        var cancelledAsked = new List<PendingContinuation>();
        cancelled.ContinuationRequested += (_, continuation) => cancelledAsked.Add(continuation);
        cancelled.InstallCommand.Execute(null);
        Assert.Empty(cancelledAsked);
        cancelled.CancelCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => cancelled.State == IntegrationOfferState.Cancelled, "It did not stop.");
        Assert.Equal(PendingOutcome.NotInstalled, Assert.Single(cancelledAsked).Outcome);

        // Failed.
        var failed = PanelFor(SetAside(), (_, _) => Task.FromResult(InstallOutcome.Fail(InstallFailure.DownloadFailed, "I could not download it.")));
        var failedAsked = new List<PendingContinuation>();
        failed.ContinuationRequested += (_, continuation) => failedAsked.Add(continuation);
        WaitForTask(failed.InstallAsync());
        Assert.Equal(PendingOutcome.NotInstalled, Assert.Single(failedAsked).Outcome);
    });

    [Fact]
    public void ThePanelAsksOnlyOnceAndOnlyWhenItWasMadeForARequest() => RunSta(() =>
    {
        var once = PanelFor(SetAside());
        var count = 0;
        once.ContinuationRequested += (_, _) => count++;
        WaitForTask(once.InstallAsync());
        WaitForTask(once.InstallAsync());
        once.CancelCommand.Execute(null);
        Assert.Equal(1, count);

        var none = PanelFor(null);
        var asked = 0;
        none.ContinuationRequested += (_, _) => asked++;
        WaitForTask(none.InstallAsync());
        Assert.Equal(0, asked);
        Assert.Equal(IntegrationOfferState.Installed, none.State);
    });

    // A provider whose answers the test writes: it shows what it is told to, and records what it is asked to go on with.
    private sealed class ContinuationProvider : IAnswerProvider
    {
        public Func<Action<MessageViewModel>, CancellationToken, Task> OnQuestion { get; set; } = (_, _) => Task.CompletedTask;

        public Func<PendingContinuation, Action<MessageViewModel>, CancellationToken, Task> OnContinue { get; set; } = (_, _, _) => Task.CompletedTask;

        public List<PendingContinuation> Continued { get; } = [];

        public int Asked { get; set; }

        public MessageViewModel? Answer(string question) => null;

        public Task StreamAnswerAsync(Guid conversationId, MessageViewModel question, Action<MessageViewModel> show, CancellationToken cancellationToken) =>
            OnQuestion(show, cancellationToken);

        public Task StreamContinuationAsync(Guid conversationId, PendingContinuation continuation, Action<MessageViewModel> show, CancellationToken cancellationToken)
        {
            Continued.Add(continuation);
            return OnContinue(continuation, show, cancellationToken);
        }
    }

    private sealed class ContinuationRecorder : IConversationRecorder
    {
        public List<(Guid Conversation, Guid Message, MessageRole Role)> Recorded { get; } = [];

        public void Record(Guid conversationId, MessageViewModel message) => Recorded.Add((conversationId, message.Id, message.Role));
    }

    private static ConversationViewModel ConversationOver(ContinuationProvider provider, ContinuationRecorder? recorder = null) =>
        new(new VoiceInputViewModel(new FakeMicrophone()), provider, recorder: recorder);

    // The assistant's message that offers an integration for the request: its words first, then the panel.
    private static MessageViewModel OfferingMessage(IntegrationOfferContent panel, bool panelAfterShowing)
    {
        var message = new MessageViewModel(MessageRole.Assistant, "I found one.");
        if (!panelAfterShowing)
        {
            message.Content.Add(panel);
        }

        return message;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhatAnAnswerSetAsideGoesOnAsTheNextAnswerOfTheConversationWithoutAskingAgainAndIsSaved(bool panelComesAfterTheWords) => RunSta(() =>
    {
        var pending = SetAside();
        var panel = PanelFor(pending);
        var provider = new ContinuationProvider
        {
            OnQuestion = (show, _) =>
            {
                var message = OfferingMessage(panel, panelComesAfterTheWords);
                show(message);

                // The approval panel comes after the Assistant's words, once they are shown.
                if (panelComesAfterTheWords)
                {
                    message.Content.Add(panel);
                }

                return Task.CompletedTask;
            },
            OnContinue = (continuation, show, _) =>
            {
                show(new MessageViewModel(MessageRole.Assistant, "Carried out: " + continuation.Outcome));
                return Task.CompletedTask;
            },
        };
        var recorder = new ContinuationRecorder();
        var conversation = ConversationOver(provider, recorder);

        conversation.Ask("Add milk to Todoist");
        WaitUntilFor(TimeSpan.FromSeconds(5), () => conversation.Messages.Count == 2 && !conversation.IsAnswering, "The offer was not shown.");
        Assert.Empty(provider.Continued);

        panel.InstallCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => conversation.Messages.Count == 3 && !conversation.IsAnswering, "The request was not gone back to.");

        Assert.Equal(PendingOutcome.Installed, Assert.Single(provider.Continued).Outcome);
        Assert.Same(pending, provider.Continued[0].Pending);
        Assert.Equal([MessageRole.User, MessageRole.Assistant, MessageRole.Assistant], conversation.Messages.Select(message => message.Role));
        Assert.Equal("Carried out: Installed", conversation.Messages[2].Text);

        // Every message was saved, and the user's question once: no question of theirs was added for the request that was gone back to.
        Assert.Single(recorder.Recorded.Where(item => item.Role == MessageRole.User).Select(item => item.Message).Distinct());
        Assert.Contains(recorder.Recorded, item => item.Message == conversation.Messages[2].Id);
        Assert.All(recorder.Recorded, item => Assert.Equal(conversation.Id, item.Conversation));
    });

    [Fact]
    public void TheContinuationIsAnAnswerLikeAnyOtherSoItCanBeStoppedAndKeepsWhatItSaid() => RunSta(() =>
    {
        var panel = PanelFor(SetAside());
        var release = new TaskCompletionSource();
        var seen = new CancellationTokenSource();
        var provider = new ContinuationProvider
        {
            OnQuestion = (show, _) =>
            {
                show(OfferingMessage(panel, false));
                return Task.CompletedTask;
            },
            OnContinue = async (_, show, token) =>
            {
                var message = new MessageViewModel(MessageRole.Assistant, "Working on it") { Status = MessageStatus.Answering };
                show(message);
                try
                {
                    await release.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    seen.Cancel();
                    message.Status = MessageStatus.Stopped;
                    throw;
                }
            },
        };
        var conversation = ConversationOver(provider);
        conversation.Ask("Add milk to Todoist");
        WaitUntilFor(TimeSpan.FromSeconds(5), () => conversation.Messages.Count == 2 && !conversation.IsAnswering, "The offer was not shown.");

        panel.InstallCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => conversation.Messages.Count == 3, "The request was not gone back to.");

        // The conversation is answering: the composer is gone, Stop is there, and Stop stops it.
        Assert.True(conversation.IsAnswering);
        Assert.True(conversation.IsStreaming);
        Assert.False(conversation.CanCompose);
        Assert.True(conversation.StopCommand.CanExecute(null));
        conversation.StopCommand.Execute(null);
        WaitUntilFor(TimeSpan.FromSeconds(5), () => seen.IsCancellationRequested, "The continuation was not stopped.");
        Assert.False(conversation.IsAnswering);
        Assert.Equal(MessageStatus.Stopped, conversation.Messages[2].Status);
        Assert.Equal("Working on it", conversation.Messages[2].Text);
        Assert.True(conversation.CanCompose);
    });

    [Fact]
    public void AnAnswerTheUserAskedForWhileItInstalledIsWaitedForAndNeverStopped() => RunSta(() =>
    {
        var panel = PanelFor(SetAside());
        var release = new TaskCompletionSource();
        var stopped = false;
        var provider = new ContinuationProvider();
        provider.OnQuestion = (show, token) =>
        {
            if (provider.Asked++ == 0)
            {
                show(OfferingMessage(panel, false));
                return Task.CompletedTask;
            }

            // The user's own next question takes its time.
            return Task.Run(async () =>
            {
                try
                {
                    await release.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    throw;
                }
            });
        };
        provider.OnContinue = (_, show, _) =>
        {
            show(new MessageViewModel(MessageRole.Assistant, "Carried out"));
            return Task.CompletedTask;
        };
        var conversation = ConversationOver(provider);
        conversation.Ask("Add milk to Todoist");
        WaitUntilFor(TimeSpan.FromSeconds(5), () => conversation.Messages.Count == 2 && !conversation.IsAnswering, "The offer was not shown.");

        // The user asks something else while the integration installs, and the installation ends while that answer is still on its way.
        Assert.True(conversation.Ask("What time is it?"));
        Assert.True(conversation.IsAnswering);
        WaitForTask(panel.InstallAsync());
        Pump();

        Assert.False(stopped);
        Assert.Empty(provider.Continued);
        release.SetResult();
        WaitUntilFor(TimeSpan.FromSeconds(5), () => provider.Continued.Count == 1 && !conversation.IsAnswering, "The request was not gone back to after the other answer.");
        Assert.False(stopped);
        Assert.Equal("Carried out", conversation.Messages[^1].Text);
    });

    [Fact]
    public void ForAConversationThatWasReplacedTheContinuationIsSavedAndNotShown() => RunSta(() =>
    {
        var panel = PanelFor(SetAside());
        var provider = new ContinuationProvider();
        provider.OnQuestion = (show, _) =>
        {
            // Only the first question is answered with the offer.
            show(provider.Asked++ == 0 ? OfferingMessage(panel, false) : new MessageViewModel(MessageRole.Assistant, "Something else answered."));
            return Task.CompletedTask;
        };
        provider.OnContinue = (_, show, _) =>
        {
            show(new MessageViewModel(MessageRole.Assistant, "Carried out"));
            return Task.CompletedTask;
        };
        var recorder = new ContinuationRecorder();
        var conversation = ConversationOver(provider, recorder);
        conversation.Ask("Add milk to Todoist");
        WaitUntilFor(TimeSpan.FromSeconds(5), () => conversation.Messages.Count == 2 && !conversation.IsAnswering, "The offer was not shown.");
        var asked = conversation.Id;

        // A new conversation replaces it before the integration is installed.
        conversation.StartNew("Something else");
        WaitUntilFor(TimeSpan.FromSeconds(5), () => !conversation.IsAnswering, "The new conversation's answer did not end.");
        var messages = conversation.Messages.ToList();
        WaitForTask(panel.InstallAsync());
        WaitUntilFor(TimeSpan.FromSeconds(5), () => provider.Continued.Count == 1 && !conversation.IsAnswering, "The request was not gone back to.");

        Assert.Equal(messages, conversation.Messages.ToList());
        Assert.Contains(recorder.Recorded, item => item.Conversation == asked && item.Role == MessageRole.Assistant && item.Message != messages[0].Id);
    });

    [Fact]
    public void AProviderThatSetsNothingAsideGoesBackToNothing() => RunSta(() =>
    {
        IAnswerProvider provider = new FakeAnswers();

        WaitForTask(provider.StreamContinuationAsync(Guid.NewGuid(), new PendingContinuation(SetAside(), PendingOutcome.Installed), _ => Assert.Fail("Nothing is shown."), CancellationToken.None));
    });

    // ---- The provider that goes back to the request -----------------------------------------------------------------

    private sealed class ContinuingOrchestrator : IAssistantOrchestrator
    {
        public List<(ConversationSession Session, PendingRequest Pending, PendingOutcome Outcome)> Continued { get; } = [];

        public Exception? Fails { get; init; }

        public async IAsyncEnumerable<AssistantResponseChunk> AskAsync(
            ConversationSession session, string prompt, IReadOnlyList<ContextItem>? contextItems = null, string? instructions = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return AssistantResponseChunk.ForTextDelta("I found one.");
            yield return AssistantResponseChunk.ForIntegrationOffer(OfferFor(), SetAsideFor(session));
        }

        public static PendingRequest SetAsideFor(ConversationSession session) => new(Guid.NewGuid(), Guid.NewGuid(), "Not installed.");

        public async IAsyncEnumerable<AssistantResponseChunk> ContinueAsync(
            ConversationSession session, PendingRequest pending, PendingOutcome outcome, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Continued.Add((session, pending, outcome));
            await Task.CompletedTask;
            if (Fails is not null)
            {
                throw Fails;
            }

            yield return AssistantResponseChunk.ForTextDelta(outcome == PendingOutcome.Installed ? "Done! I added it." : pending.NotInstalledText);
        }
    }

    [Fact]
    public void TheModelProviderStreamsTheContinuationAsAnAnswerOfItsOwnInTheSameConversation() => RunSta(() =>
    {
        var orchestrator = new ContinuingOrchestrator();
        var answers = new ModelAnswerProvider(orchestrator, new FixedClock(Now), offers: new RecordingBroker());
        var conversation = Guid.NewGuid();
        var offered = new List<MessageViewModel>();
        WaitForTask(answers.StreamAnswerAsync(conversation, "Add milk to Todoist", offered.Add, CancellationToken.None));
        var panel = Assert.IsType<IntegrationOfferContent>(offered[0].Content.Last());

        // The panel the answer drew carries the request that was set aside, which the conversation hands back.
        PendingContinuation? asked = null;
        panel.ContinuationRequested += (_, continuation) => asked = continuation;
        WaitForTask(panel.InstallAsync());
        Assert.NotNull(asked);
        var shown = new List<MessageViewModel>();
        WaitForTask(answers.StreamContinuationAsync(conversation, asked!, shown.Add, CancellationToken.None));

        var message = Assert.Single(shown);
        Assert.Equal("Done! I added it.", message.Text);
        Assert.Equal(MessageStatus.Complete, message.Status);
        var (session, pending, outcome) = Assert.Single(orchestrator.Continued);
        Assert.Same(answers.SessionOf(conversation), session);
        Assert.Same(asked!.Pending, pending);
        Assert.Equal(PendingOutcome.Installed, outcome);
    });

    [Fact]
    public void ANotInstalledContinuationIsTheAssistantsWordsAndAFailureIsSaidInPlainWords() => RunSta(() =>
    {
        var conversation = Guid.NewGuid();
        var said = new List<MessageViewModel>();
        var notInstalled = new PendingContinuation(SetAside(), PendingOutcome.NotInstalled);
        WaitForTask(new ModelAnswerProvider(new ContinuingOrchestrator(), new FixedClock(Now)).StreamContinuationAsync(conversation, notInstalled, said.Add, CancellationToken.None));
        Assert.Equal(notInstalled.Pending.NotInstalledText, Assert.Single(said).Text);

        var failed = new List<MessageViewModel>();
        var failing = new ModelAnswerProvider(new ContinuingOrchestrator { Fails = new Assistant.Core.ModelHosting.ModelHostException(Assistant.Core.ModelHosting.ModelHostErrorCode.ContextExceeded) }, new FixedClock(Now));
        WaitForTask(failing.StreamContinuationAsync(conversation, new PendingContinuation(SetAside(), PendingOutcome.Installed), failed.Add, CancellationToken.None));
        Assert.Equal(MessageStatus.Failed, Assert.Single(failed).Status);

        var none = new List<MessageViewModel>();
        var withoutModel = new ModelAnswerProvider(new ContinuingOrchestrator { Fails = new ModelNotSetUpException() }, new FixedClock(Now));
        WaitForTask(withoutModel.StreamContinuationAsync(conversation, new PendingContinuation(SetAside(), PendingOutcome.Installed), none.Add, CancellationToken.None));
        Assert.Equal(ModelAnswerProvider.NoModelText, Assert.Single(none).Text);
    });

    // ---- The sample's decorators (demo integration request) -----------------------------------------------------------------

    private sealed class FakeSampleDemo : ISampleIntegrationDemo
    {
        public ConnectedAppReply? Reply { get; set; }

        public Exception? Fails { get; set; }

        public HashSet<string> Offered { get; } = ["sample-offer"];

        public List<string> Accepted { get; } = [];

        public List<string> Declined { get; } = [];

        public Task<MessageViewModel> OfferAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageViewModel> StartRequestDemoAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConnectedAppReply?> TryReplyAsync(string? request, CancellationToken cancellationToken) =>
            Fails is not null ? throw Fails : Task.FromResult(Reply);

        public bool Owns(string offerId) => Offered.Contains(offerId);

        public Task<InstallOutcome> AcceptAsync(string offerId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
        {
            Accepted.Add(offerId);
            return Task.FromResult(InstalledOutcome());
        }

        public void Decline(string offerId) => Declined.Add(offerId);
    }

    private sealed class InnerHandler(ConnectedAppReply? reply) : IConnectedAppRequestHandler
    {
        public int Asked { get; private set; }

        public Task<ConnectedAppReply?> TryAnswerAsync(ToolContext context, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public void TheSampleOffersItselfForTheRequestWhenTheDemoIsOnAndEveryOtherRequestIsTheAssistantsOwn() => RunSta(() =>
    {
        var ours = new ConnectedAppReply("sample", ConnectedAppReplyKind.InstallOffered, OfferFor(), "no");
        var theirs = new ConnectedAppReply("theirs", ConnectedAppReplyKind.DiscoveryBlocked);
        var demo = new FakeSampleDemo { Reply = ours };
        var inner = new InnerHandler(theirs);
        var handler = new SampleAwareRequestHandler(inner, demo);

        Assert.Same(ours, handler.TryAnswerAsync(new ToolContext(Guid.NewGuid(), "List my notes in the Sample Notes app")).GetAwaiter().GetResult());
        Assert.Equal(0, inner.Asked);

        demo.Reply = null;
        Assert.Same(theirs, handler.TryAnswerAsync(new ToolContext(Guid.NewGuid(), "Add milk to Todoist")).GetAwaiter().GetResult());
        Assert.Equal(1, inner.Asked);

        // A demo that cannot offer the sample is a request for the Assistant's own handler, as always.
        demo.Fails = new InvalidOperationException("The sample is not here.");
        Assert.Same(theirs, handler.TryAnswerAsync(new ToolContext(Guid.NewGuid(), "List my notes in the Sample Notes app")).GetAwaiter().GetResult());
        Assert.Equal(2, inner.Asked);

        // A stop is not swallowed.
        demo.Fails = new OperationCanceledException();
        Assert.Throws<OperationCanceledException>(() => handler.TryAnswerAsync(new ToolContext(Guid.NewGuid(), "x")).GetAwaiter().GetResult());
    });

    [Fact]
    public void TheSamplesOffersAreAcceptedAndDeclinedByTheDemoAndEveryOtherOfferIsTheRealBrokers() => RunSta(() =>
    {
        var demo = new FakeSampleDemo();
        var real = new RecordingBroker();
        var offers = new SampleAwareOffers(real, demo);

        WaitForTask(offers.AcceptAsync("sample-offer"));
        WaitForTask(offers.AcceptAsync("real-offer"));
        offers.Decline("sample-offer");
        offers.Decline("real-offer");

        Assert.Equal(["sample-offer"], demo.Accepted);
        Assert.Equal(["real-offer"], real.Accepted);
        Assert.Equal(["sample-offer"], demo.Declined);
        Assert.Equal(["real-offer"], real.Declined);
    });
}
