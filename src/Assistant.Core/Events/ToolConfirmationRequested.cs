using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Core.Events;

/// <summary>Where a question to the user ("may I do this?") stands (PROJECT_SPEC §4.8, step 115).</summary>
public enum ToolConfirmationState
{
    /// <summary>Shown, waiting for the user to say yes or no. Nothing has been done.</summary>
    Pending = 0,

    /// <summary>The user said yes.</summary>
    Approved = 1,

    /// <summary>The user said no.</summary>
    Declined = 2,

    /// <summary>The user did not answer in time. Nothing was done.</summary>
    Expired = 3,

    /// <summary>The question was taken back, because the answer it was for was stopped. Nothing was done.</summary>
    Withdrawn = 4,
}

/// <summary>
/// A <see cref="RiskLevel.SideEffect"/> tool call is waiting for the user's approval (PROJECT_SPEC §4.8, step 115). The <see cref="Assistant.Core.Confirmation.ConfirmationBroker"/>
/// publishes it, and whichever surface shows the conversation <see cref="ConversationId"/> displays the question inline, with exactly what the call would do
/// (<see cref="Confirmation"/>), and says so (<see cref="MarkShown"/>); the user's click calls <see cref="Approve"/> or <see cref="Decline"/>. A request that nothing
/// showed is never approved: the call is not made.
/// </summary>
/// <remarks>
/// The first answer wins and is final: a request is answered once, so an approval cannot be used again, for another call or later. It carries the call and what the user
/// was shown for it, and nothing that could approve anything else. It holds the user's content, so none of it reaches <see cref="object.ToString"/>.
/// </remarks>
public sealed class ToolConfirmationRequested
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<ToolConfirmationState> _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ToolConfirmationState _state;
    private bool _shown;
    private bool _always;

    /// <summary>Creates the request to confirm <paramref name="call"/> of <paramref name="tool"/>, in the conversation <paramref name="conversationId"/>.</summary>
    /// <param name="canAlwaysAllow">
    /// Whether the user may answer "always allow", which says yes to this call and to every later call of the tool as it is now. Whoever asks decides it
    /// by <see cref="StandingApprovals.MayBeKept"/>: never for a message.
    /// </param>
    public ToolConfirmationRequested(ToolDefinition tool, ToolCall call, Guid conversationId, ToolConfirmation confirmation, bool canAlwaysAllow = false)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(confirmation);
        Tool = tool;
        Call = call;
        ConversationId = conversationId;
        Confirmation = confirmation;
        CanAlwaysAllow = canAlwaysAllow;
    }

    /// <summary>Whether "always allow" is one of the answers to this question.</summary>
    public bool CanAlwaysAllow { get; }

    /// <summary>Whether the user's yes was "always allow": the call is made, and the tool as it is now is not asked about again.</summary>
    public bool IsAlways
    {
        get
        {
            lock (_gate)
            {
                return _always;
            }
        }
    }

    /// <summary>Raised, from the thread that changed it, when <see cref="State"/> changes: the question that was shown can then say how it ended.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The tool that would run.</summary>
    public ToolDefinition Tool { get; }

    /// <summary>The call, as it would be made.</summary>
    public ToolCall Call { get; }

    /// <summary>The conversation the call was made in; the question belongs in that conversation, and nowhere else.</summary>
    public Guid ConversationId { get; }

    /// <summary>What the user is asked: exactly what the call would do.</summary>
    public ToolConfirmation Confirmation { get; }

    /// <summary>Where the question stands.</summary>
    public ToolConfirmationState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Whether a surface has put the question in front of the user.</summary>
    public bool IsShown
    {
        get
        {
            lock (_gate)
            {
                return _shown;
            }
        }
    }

    /// <summary>Completes when the question is answered, expires or is withdrawn, with how it ended.</summary>
    public Task<ToolConfirmationState> Settled => _settled.Task;

    /// <summary>Says that the question is in front of the user. Only a surface that really shows it may say so.</summary>
    public void MarkShown()
    {
        lock (_gate)
        {
            _shown = true;
        }
    }

    /// <summary>The user said yes. Returns whether this was the answer: one that came after another is ignored.</summary>
    public bool Approve() => Settle(ToolConfirmationState.Approved);

    /// <summary>
    /// The user said yes, now and every time after. Returns whether this was the answer; a question that does not offer it (<see cref="CanAlwaysAllow"/>)
    /// is not answered by it at all.
    /// </summary>
    public bool ApproveAlways() => CanAlwaysAllow && Settle(ToolConfirmationState.Approved, always: true);

    /// <summary>The user said no. Returns whether this was the answer.</summary>
    public bool Decline() => Settle(ToolConfirmationState.Declined);

    /// <summary>The time for an answer is up. Returns whether it ended the question.</summary>
    public bool Expire() => Settle(ToolConfirmationState.Expired);

    /// <summary>Takes the question back, because what it was for was stopped. Returns whether it ended the question.</summary>
    public bool Withdraw() => Settle(ToolConfirmationState.Withdrawn);

    private bool Settle(ToolConfirmationState state, bool always = false)
    {
        lock (_gate)
        {
            if (_state != ToolConfirmationState.Pending)
            {
                return false;
            }

            _state = state;
            _always = always;
        }

        _settled.TrySetResult(state);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Keeps the call's arguments and the question (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    /// <inheritdoc/>
    public override string ToString() => $"{nameof(ToolConfirmationRequested)} {{ State = {State} }}";
}
