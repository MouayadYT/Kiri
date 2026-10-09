using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.Core.Events;

namespace Assistant.UI.Messages;

/// <summary>
/// Puts the question the Assistant asks before it changes anything (<see cref="ToolConfirmationRequested"/>) in front of the user, in the answer being streamed into
/// the conversation it was asked in (PROJECT_SPEC §4.8, step 115). Whoever streams an answer holds one for as long as the answer is on its way: the answer's tool calls are
/// made while it streams, so the question belongs in it, below the words the Assistant has said so far.
/// </summary>
/// <remarks>
/// A request counts as shown only once its panel is really in the answer (<see cref="ToolConfirmationRequested.MarkShown"/>): one for another conversation is left for the
/// listener that holds that conversation, and one that arrives when nothing holds its conversation is never shown, so it is never approved. When the listener is disposed
/// (the answer is over) a question that is still waiting is withdrawn, so no panel is left that could be pressed to approve a call that is no longer being made.
/// </remarks>
internal sealed class ToolConfirmationListener : IDisposable
{
    private readonly Guid _conversationId;
    private readonly Dispatcher _dispatcher;
    private readonly Action<MessageContent> _show;
    private readonly TimeSpan? _armDelay;
    private readonly IDisposable _subscription;
    private readonly List<(ToolConfirmationRequested Request, ToolConfirmationContent Panel)> _panels = [];
    private bool _disposed;

    /// <summary>Starts listening for the questions of the conversation <paramref name="conversationId"/>.</summary>
    /// <param name="bus">Where the questions are published.</param>
    /// <param name="conversationId">The conversation whose answer this listener's holder is streaming.</param>
    /// <param name="dispatcher">The user interface's dispatcher, which the panel is created and shown on.</param>
    /// <param name="show">Puts a panel in the answer. It is called on <paramref name="dispatcher"/>'s thread.</param>
    /// <param name="armDelay">How long the panels hold back the button that says yes; their default when not given.</param>
    public ToolConfirmationListener(IAppEventBus bus, Guid conversationId, Dispatcher dispatcher, Action<MessageContent> show, TimeSpan? armDelay = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(show);
        _conversationId = conversationId;
        _dispatcher = dispatcher;
        _show = show;
        _armDelay = armDelay;
        _subscription = bus.Subscribe<ToolConfirmationListener, ToolConfirmationRequested>(this, static (listener, request, _) => listener.OnRequestedAsync(request));
    }

    private async Task OnRequestedAsync(ToolConfirmationRequested request)
    {
        // Only this conversation's questions belong in this answer. A question without a conversation (a call made outside a turn) belongs in none.
        if (request.ConversationId != _conversationId || request.ConversationId == Guid.Empty)
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || request.State != ToolConfirmationState.Pending)
            {
                return;
            }

            var panel = new ToolConfirmationContent(request, _armDelay);
            _panels.Add((request, panel));
            _show(panel);

            // Only now is it in front of the user.
            request.MarkShown();
        });
    }

    /// <summary>Stops listening, and withdraws the questions that are still waiting.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
        foreach (var (request, panel) in _panels)
        {
            // The panel follows the request, so it says that the question was taken back, and then stops following.
            request.Withdraw();
            panel.Dispose();
        }

        _panels.Clear();
    }
}
