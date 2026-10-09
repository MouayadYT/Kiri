using System.Windows.Threading;
using Assistant.Core.Audit;

namespace Assistant.UI.Messages;

/// <summary>
/// Puts the panel of a multi-step run of the agent in front of the user, in the answer being streamed into the conversation the run answers in (PROJECT_SPEC §4.8,
/// step 117). Whoever streams an answer holds one for as long as the answer is on its way: the run's tool calls are made while it streams, so its panel belongs in
/// it, below the words the Assistant has said so far, like the question it asks before it changes anything (<see cref="ToolConfirmationListener"/>).
/// </summary>
/// <remarks>
/// A run has its panel from its first step (or from when something went wrong before one). The panel is one of the answer's workings
/// (<see cref="MessageContent.IsTucked"/>): it is put away behind the button with three dots under the answer, where the user opens it when they want to see
/// what was done. The panel is made once, and follows the run to its end. A run in another conversation is left for the
/// listener that holds that conversation. Nothing here is saved: the panel is for the moment.
/// </remarks>
internal sealed class AgentTaskListener : IDisposable
{
    private readonly IAgentTaskLog _log;
    private readonly Guid _conversationId;
    private readonly Dispatcher _dispatcher;
    private readonly Action<MessageContent> _show;
    private readonly TimeProvider? _clock;
    private readonly Action? _openActivity;
    private readonly List<Follower> _followers = [];
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>The run of this conversation that began last, or <see langword="null"/> before any has: whoever shows what a tool did can open its steps.</summary>
    public IAgentTaskView? Latest { get; private set; }

    /// <summary>Starts listening for the runs of the conversation <paramref name="conversationId"/>.</summary>
    /// <param name="log">Where the runs report themselves.</param>
    /// <param name="conversationId">The conversation whose answer this listener's holder is streaming.</param>
    /// <param name="dispatcher">The user interface's dispatcher, which the panel is created and shown on.</param>
    /// <param name="show">Puts a panel in the answer. It is called on <paramref name="dispatcher"/>'s thread.</param>
    /// <param name="clock">The time, for how long a run that goes on has taken.</param>
    /// <param name="openActivity">Opens the activity page, for the panel's link to it.</param>
    public AgentTaskListener(
        IAgentTaskLog log, Guid conversationId, Dispatcher dispatcher, Action<MessageContent> show, TimeProvider? clock = null, Action? openActivity = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(show);
        _log = log;
        _conversationId = conversationId;
        _dispatcher = dispatcher;
        _show = show;
        _clock = clock;
        _openActivity = openActivity;
        _log.TaskStarted += OnTaskStarted;
    }

    private void OnTaskStarted(object? sender, AgentTaskStartedEventArgs e)
    {
        // Only this conversation's runs belong in this answer. A run without a conversation belongs in none.
        if (e.ConversationId != _conversationId || e.ConversationId == Guid.Empty)
        {
            return;
        }

        Latest = e.Task;
        var follower = new Follower(this, e.Task);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _followers.Add(follower);
        }

        follower.Start();
    }

    /// <summary>
    /// Shows now the panel of every run that is worth showing and was not yet, from what each has done so far. Whoever streams the answer calls it when the answer
    /// ends, so that a run that only became worth showing at its end (a single step that failed, a stop) is not missed because the answer finished first. Call it on
    /// the user interface's thread.
    /// </summary>
    public void Flush()
    {
        Follower[] followers;
        lock (_gate)
        {
            followers = [.. _followers];
        }

        foreach (var follower in followers)
        {
            follower.CheckNow();
        }
    }

    /// <summary>Stops listening, and stops following the runs that were shown.</summary>
    public void Dispose()
    {
        List<Follower> followers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            followers = [.. _followers];
            _followers.Clear();
        }

        _log.TaskStarted -= OnTaskStarted;
        foreach (var follower in followers)
        {
            follower.Stop();
        }
    }

    // One run: watched until it is worth showing, then shown, then followed by its panel.
    private sealed class Follower(AgentTaskListener owner, IAgentTaskView task)
    {
        private AgentTaskContent? _panel;
        private bool _stopped;

        public void Start()
        {
            task.Changed += OnChanged;
            Check();
        }

        public void Stop()
        {
            task.Changed -= OnChanged;
            _ = owner._dispatcher.InvokeAsync(() =>
            {
                _stopped = true;
                _panel?.Dispose();
            });
        }

        private void OnChanged(object? sender, EventArgs e) => Check();

        private void Check() => _ = owner._dispatcher.InvokeAsync(CheckNow);

        // On the user interface's thread: shows the panel once the run is worth showing.
        public void CheckNow()
        {
            var snapshot = task.Snapshot;
            if (_stopped || _panel is not null || owner._disposed || (snapshot.Steps.Count == 0 && !snapshot.IsWorthShowing))
            {
                return;
            }

            // The panel follows the run from here on, so there is no more to watch for.
            task.Changed -= OnChanged;
            _panel = new AgentTaskContent(task, owner._clock, owner._openActivity);
            owner._show(_panel);
        }
    }
}
