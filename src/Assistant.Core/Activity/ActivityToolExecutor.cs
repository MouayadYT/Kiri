using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Core.Activity;

/// <summary>
/// Reports every tool call made through another <see cref="IToolExecutor"/> to the <see cref="IActivityTracker"/>, for
/// as long as it runs. Neither the tool's name nor its arguments are passed on. While the call waits for the user to answer a
/// question about it (it holds the run's clock, <see cref="ToolContext.RunPause"/>, PROJECT_SPEC §4.8, step 115) the activity says
/// <see cref="WaitingForYouText"/> instead of "Working", since the Assistant is not working then, the user is being waited for.
/// </summary>
public sealed class ActivityToolExecutor(IToolExecutor inner, IActivityTracker tracker) : IToolExecutor
{
    /// <summary>What the activity says while the user is being asked something.</summary>
    public const string WaitingForYouText = "Waiting for you";

    // The tools that look something up on the web: while one runs the Assistant is "Looking into it", as the reference says, rather than "Working".
    // Only which kind of tool it is decides the words; nothing of the call is passed on.
    private static readonly HashSet<string> WebLookups = new(StringComparer.Ordinal) { "search_web" };

    /// <inheritdoc/>
    public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
        ExecuteAsync(call, new ToolContext(Guid.Empty), cancellationToken);

    /// <inheritdoc/>
    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var kind = WebLookups.Contains(call.ToolName) ? ActivityKind.WebSearch : ActivityKind.Tool;
        using var activity = tracker.Begin(kind, cancellationToken: cancellationToken);
        return await inner.ExecuteAsync(call, context with { RunPause = new WaitingPause(context.RunPause, activity, kind) }, activity.CancellationToken)
            .ConfigureAwait(false);
    }

    // The run's own pause, which also tells the activity that what is being waited for is the user.
    private sealed class WaitingPause(IRunPause? run, IActivityScope activity, ActivityKind kind) : IRunPause
    {
        /// <inheritdoc/>
        public IDisposable Pause()
        {
            activity.Update(WaitingForYouText);
            return new Hold(run?.Pause(), activity, kind);
        }
    }

    private sealed class Hold(IDisposable? run, IActivityScope activity, ActivityKind kind) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                run?.Dispose();
                activity.Update(ActivityStatus.DefaultText(kind));
            }
        }
    }
}
