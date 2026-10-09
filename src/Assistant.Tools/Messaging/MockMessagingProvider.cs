using Assistant.Core.Messaging;
using Assistant.Core.People;

namespace Assistant.Tools.Messaging;

/// <summary>
/// An <see cref="IMessagingProvider"/> that sends to no one (PROJECT_SPEC §4.8, step 113): for tests and for trying the Assistant before a messaging app is connected. It
/// keeps what it was asked to send in memory, so a test can see exactly what would have gone out, and says that it is a sample, so the model tells the user nothing reached anyone.
/// A person can be reached by it when they have at least one number, address or username saved, as a real provider needs one to find a chat; and it can be made to fail the way a real
/// one does (<see cref="FailWith"/>). The app registers none, so the tools that use one are never offered.
/// </summary>
public sealed class MockMessagingProvider : IMessagingProvider
{
    private readonly object _gate = new();
    private readonly List<SentMessage> _sent = [];
    private readonly List<OutgoingMessage> _drafted = [];
    private MessagingFailure? _failure;
    private string[] _options = [];
    private MessageDeliveryStatus _status = MessageDeliveryStatus.Sent;

    /// <inheritdoc/>
    public string Name => "Sample Messages";

    /// <inheritdoc/>
    public bool IsSample => true;

    /// <summary>What was sent, in order.</summary>
    public IReadOnlyList<SentMessage> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>What was drafted, in order: drafting sends nothing.</summary>
    public IReadOnlyList<OutgoingMessage> Drafted
    {
        get
        {
            lock (_gate)
            {
                return [.. _drafted];
            }
        }
    }

    /// <summary>From now on every draft and every send fails for <paramref name="failure"/>; <see langword="null"/> makes them work again.</summary>
    /// <param name="failure">Why it fails, or <see langword="null"/> for no failure.</param>
    /// <param name="options">For <see cref="MessagingFailure.Ambiguous"/>, the services of the chats that fit.</param>
    public void FailWith(MessagingFailure? failure, params string[] options)
    {
        lock (_gate)
        {
            _failure = failure;
            _options = options;
        }
    }

    /// <summary>From now on a send is reported as <paramref name="status"/>.</summary>
    public void ReportSendsAs(MessageDeliveryStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }
    }

    /// <inheritdoc/>
    public Task<MessageDraft> CreateDraftAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_failure is { } failure)
            {
                throw new MessagingProviderException(failure, _options);
            }

            if (message.Recipient.Identifiers.Count == 0)
            {
                throw new MessagingProviderException(MessagingFailure.RecipientNotFound);
            }

            _drafted.Add(message);
            var identifier = message.Recipient.Identifiers[0];
            var service = identifier.Service.Length > 0 ? identifier.Service : "Sample";
            return Task.FromResult(new MessageDraft(message, $"{service} chat with {message.Recipient.DisplayName}", identifier.Value));
        }
    }

    /// <inheritdoc/>
    public Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_failure is { } failure)
            {
                throw new MessagingProviderException(failure, _options);
            }

            _sent.Add(new SentMessage(draft.Message, draft.Route));
            return Task.FromResult(new MessageSendResult(_status, draft.Route));
        }
    }

    /// <summary>One message the sample was asked to send.</summary>
    /// <param name="Message">The message.</param>
    /// <param name="Route">Where it was said to go.</param>
    public sealed record SentMessage(OutgoingMessage Message, string Route);
}
