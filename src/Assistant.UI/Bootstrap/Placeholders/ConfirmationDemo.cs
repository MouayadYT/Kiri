using System.Text.Json;
using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.People;
using Assistant.Tools;
using Assistant.Tools.Messaging;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>The developer's test of the question the Assistant asks before it changes anything (<c>demo confirm</c>).</summary>
internal interface IConfirmationDemo
{
    /// <summary>
    /// Runs the demonstration into the conversation <paramref name="conversationId"/> (a conversation of its own when <see langword="null"/>): the Assistant says what it is about to
    /// do, the real question is asked in the answer and waits for the user, and what the user chooses is said.
    /// </summary>
    Task RunAsync(Guid? conversationId, Action<MessageViewModel> show, CancellationToken cancellationToken);
}

/// <summary>
/// <c>demo confirm</c> (PROJECT_SPEC §4.8, step 115): shows what it is like when the Assistant is about to do something that changes anything. The real thing is used from end to
/// end: the real <c>send_message</c> tool resolves a made-up person ("Omar", in a list that exists only for this test and never touches the people the user saved) to a made-up
/// messaging app (the sample, which says so and sends nothing anywhere), the real executor plans the call and asks, the real confirmation puts the question inline in the
/// answer, and the tool runs only if the user presses the button that says yes. What the user chooses is then said in words.
/// </summary>
internal sealed class ConfirmationDemo(IAppEventBus bus, IPermissionService confirmations, TimeProvider clock) : IConfirmationDemo
{
    private const string Intro =
        "Here is what it is like when I am about to do something that changes anything. I will try to send a message to a made-up person, Omar, through a made-up " +
        "messaging app. Nothing leaves this PC. Look at what I would do, then choose.";

    private const string Message = "I'm running late. Start without me.";

    /// <inheritdoc/>
    public async Task RunAsync(Guid? conversationId, Action<MessageViewModel> show, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(show);
        var conversation = conversationId ?? Guid.NewGuid();
        var answer = new MessageViewModel(MessageRole.Assistant, Intro) { Status = MessageStatus.Answering, CreatedAt = clock.GetUtcNow() };
        show(answer);

        // The question is put in this answer, the way it is in an answer the model gives.
        using var listener = new ToolConfirmationListener(bus, conversation, Dispatcher.CurrentDispatcher, answer.Content.Add);
        try
        {
            var result = await new ToolExecutor([await SendToolAsync(cancellationToken).ConfigureAwait(true)], confirmations, new Allowing())
                .ExecuteAsync(
                    new ToolCall("demo-send", "send_message", JsonSerializer.Serialize(new { recipient = "Omar", text = Message })),
                    new ToolContext(conversation, "Tell Omar I'm running late"),
                    cancellationToken)
                .ConfigureAwait(true);

            answer.Content.Add(new TextContent(Outcome(result)));
            answer.Status = MessageStatus.Complete;
        }
        catch (OperationCanceledException)
        {
            answer.Status = MessageStatus.Stopped;
            throw;
        }
    }

    // The real tool, over a list of people and a messaging app that exist for this test alone.
    private async Task<SendMessageTool> SendToolAsync(CancellationToken cancellationToken)
    {
        var store = new InMemoryPersonStore(clock);
        var now = clock.GetUtcNow();
        await store.SaveAsync(
            Person.Create("Omar", now) with { Identifiers = [new PersonIdentifier(PersonIdentifierKind.Phone, "+1 555 0100", "Messages")] }, cancellationToken)
            .ConfigureAwait(true);
        return new SendMessageTool(new MockMessagingProvider(), new PersonResolver(store));
    }

    private static string Outcome(ToolResult result) => result.Status switch
    {
        ToolResultStatus.Succeeded => "You allowed it, and the sample messaging app took it. Nothing really left this PC. A real message goes out only after this same question.",
        ToolResultStatus.Declined when result.OutputJson.Contains("no_answer", StringComparison.Ordinal) =>
            "There was no answer in time, so I did nothing. A question that is not answered is a no.",
        ToolResultStatus.Declined => "You chose Don't allow, so nothing was sent.",
        _ => "That did not work, and nothing was sent.",
    };

    // The demonstration is for the messaging tool, whatever the user's Permissions say; it reaches nothing real.
    private sealed class Allowing : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(capability, PermissionDecisionReason.Granted));
    }
}
