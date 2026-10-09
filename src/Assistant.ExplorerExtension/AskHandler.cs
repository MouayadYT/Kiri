using Assistant.Core.Ipc;
using Assistant.ExplorerExtension.Forwarding;
using Assistant.ExplorerExtension.Selection;

namespace Assistant.ExplorerExtension;

/// <summary>How an <c>ask</c> ended.</summary>
/// <param name="Result">How forwarding ended, or <see langword="null"/> when this process sent nothing because another did.</param>
/// <param name="Sent">How many paths were sent.</param>
/// <param name="FromSelection">Whether they are the whole selection read from File Explorer, rather than the file this process was started for.</param>
internal sealed record AskOutcome(ForwardResult? Result, int Sent, bool FromSelection);

/// <summary>
/// What <c>Assistant.ExplorerExtension.exe ask "&lt;file&gt;"</c> does (PROJECT_SPEC §4.4). File Explorer starts it once for each
/// selected file, so it first asks File Explorer for the whole selection: when a window has the file selected, the first process
/// to claim that selection sends all of its paths, in the order the window lists them, and the others end at once. When no window
/// can be found, each process sends its own file and the app gathers them. The app checks every path itself.
/// </summary>
internal sealed class AskHandler(
    ISelectionSource selection, ISelectionGate gate, InvocationForwarder forwarder, Action<TimeSpan> wait, TimeSpan claimHold)
{
    /// <summary>How long a process keeps its claim after sending: the time Explorer takes to start the rest of its processes.</summary>
    public static readonly TimeSpan DefaultClaimHold = TimeSpan.FromSeconds(4);

    /// <summary>Handles the files File Explorer started this process for.</summary>
    public AskOutcome Run(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var clicked = arguments
            .Select(ExplorerPaths.Prepare)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (clicked.Length == 0)
        {
            // Nothing that is a path: the app is told all the same, so that it can say so.
            return Send(arguments.Take(InvocationProtocol.MaxPaths).ToArray(), fromSelection: false);
        }

        var all = selection.TryRead(clicked);
        if (all is not { Count: > 0 })
        {
            return Send(clicked, fromSelection: false);
        }

        var paths = all.Distinct(StringComparer.OrdinalIgnoreCase).Take(InvocationProtocol.MaxPaths).ToArray();
        using var claim = gate.TryClaim(paths);
        if (claim is null)
        {
            return new AskOutcome(null, 0, FromSelection: true);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = Send(paths, fromSelection: true);
        var left = claimHold - System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (left > TimeSpan.Zero)
        {
            wait(left);
        }

        return outcome;
    }

    private AskOutcome Send(string[] paths, bool fromSelection) =>
        new(forwarder.Forward(new InvocationRequest(InvocationAction.AskAboutFiles, paths)), paths.Length, fromSelection);
}
