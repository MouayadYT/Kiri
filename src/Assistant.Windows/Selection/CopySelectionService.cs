using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Selection;

/// <summary>How long the copy fallback waits for each thing it depends on.</summary>
/// <param name="KeyRelease">How long the user has to let go of the keys of the shortcut before Copy is pressed.</param>
/// <param name="CopyAppears">How long the application has to put its copy on the clipboard.</param>
/// <param name="Settle">How long the clipboard must stay unchanged after the copy before it is read: some applications write it in several steps.</param>
/// <param name="SettleLimit">The most time spent waiting for the clipboard to settle.</param>
/// <param name="Poll">How often the keys and the clipboard are looked at while waiting.</param>
/// <param name="Overall">The most time the whole thing may take, after which it is given up on.</param>
internal sealed record CopySelectionTimings(
    TimeSpan KeyRelease, TimeSpan CopyAppears, TimeSpan Settle, TimeSpan SettleLimit, TimeSpan Poll, TimeSpan Overall)
{
    /// <summary>The timings of the real thing.</summary>
    public static CopySelectionTimings Default { get; } = new(
        TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(15), TimeSpan.FromSeconds(10));
}

/// <summary>
/// The app's <see cref="ICopySelectionService"/> (PROJECT_SPEC §4.5, capture order 2): for an application whose selection UI Automation cannot
/// read, it presses Copy in that application, reads the clipboard and puts the user's previous clipboard back.
/// </summary>
/// <remarks>
/// <para>
/// Pressing a key in another application can change its state, so this runs only when a caller asks for it, and refuses where that is not
/// safe: in a terminal, where Ctrl+C stops the running program, in a password box, while the user still holds keys of the shortcut (which
/// would turn Copy into another shortcut), and when the window in front is not the one that was in front when it began.
/// </para>
/// <para>
/// The clipboard is saved before Copy is pressed, and Copy is not pressed at all when the clipboard cannot be saved exactly (an object another
/// application renders on demand, a very large one): the user's clipboard is never put at risk to read a selection. After the copy it is put back
/// only if nothing else changed it in between (its change counter is the one the copy left), so a newer clipboard is never written over. The
/// restored contents are marked so that Windows' clipboard history and cloud sync do not take them as news; the copied text itself, written by
/// the other application, is not something this code can keep out of clipboard history. Neither the text nor a window title is ever logged.
/// </para>
/// <para>
/// All of it runs on a thread of its own, since the clipboard and other applications can be slow or stuck, and is given up on after
/// <see cref="CopySelectionTimings.Overall"/>.
/// </para>
/// </remarks>
public sealed class CopySelectionService : ICopySelectionService
{
    // Programs where Ctrl+C is not Copy: in a console it interrupts what is running, and Windows Terminal and the others do the same when
    // nothing is selected, which this code cannot tell.
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "conhost", "powershell", "powershell_ise", "pwsh", "windowsterminal", "wt", "openconsole", "wsl", "wslhost", "bash",
        "ubuntu", "debian", "kali-linux", "mintty", "conemu", "conemu64", "alacritty", "wezterm-gui", "kitty", "putty", "terminus",
        "hyper", "tabby", "fluent-terminal",
    };

    private static readonly HashSet<string> TerminalWindowClasses = new(StringComparer.Ordinal)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty", "VirtualConsoleClass", "PuTTY",
    };

    private readonly IClipboardNativeMethods _native;
    private readonly CopySelectionTimings _timings;
    private readonly ILogger<CopySelectionService> _logger;
    private readonly int _ownProcessId;

    /// <summary>Creates the service over the real desktop and clipboard.</summary>
    public CopySelectionService(ILogger<CopySelectionService> logger)
        : this(logger, new ClipboardNativeMethods(), CopySelectionTimings.Default)
    {
    }

    internal CopySelectionService(
        ILogger<CopySelectionService> logger, IClipboardNativeMethods native, CopySelectionTimings timings, int? ownProcessId = null)
    {
        _ownProcessId = ownProcessId ?? Environment.ProcessId;
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(timings);
        _logger = logger;
        _native = native;
        _timings = timings;
    }

    /// <inheritdoc/>
    public async Task<CopySelectionResult> CopySelectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = Stopwatch.GetTimestamp();

        // What this copies is only a selection that the user asked about, not something they chose to copy: the clipboard history, when
        // the user has allowed it, never takes it in (and never what is put back).
        using var apart = Clipboard.ClipboardSuppression.Begin();
        var answer = new TaskCompletionSource<CopySelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                answer.TrySetResult(Run(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                answer.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                CopySelectionLog.Failed(_logger, exception.GetType().Name);
                answer.TrySetResult(CopySelectionResult.Of(CopySelectionStatus.Failed, null));
            }
        })
        {
            IsBackground = true,
            Name = "Selection copy",
        };

        // The clipboard and UI Automation are used from a multithreaded apartment, so that nothing waits on a message pump.
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        CopySelectionResult result;
        try
        {
            result = await answer.Task.WaitAsync(_timings.Overall, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CopySelectionLog.TimedOut(_logger);
            result = CopySelectionResult.Of(CopySelectionStatus.Failed, null);
        }

        CopySelectionLog.Answered(
            _logger, result.App?.ProcessName ?? string.Empty, result.Status, result.Restore, result.Text?.Length ?? 0, result.IsTruncated,
            (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return result;
    }

    private CopySelectionResult Run(CancellationToken cancellationToken)
    {
        var app = _native.GetForegroundApp();
        if (app is null)
        {
            return CopySelectionResult.Of(CopySelectionStatus.NoForegroundApp, null);
        }

        // The Assistant's own window has no other application's selection, and Copy pressed in it would be pressed in the Assistant.
        if (app.ProcessId == _ownProcessId)
        {
            return CopySelectionResult.Of(CopySelectionStatus.OwnWindow, app);
        }

        if (IsTerminal(app, _native.WindowClassOf(app.WindowHandle)))
        {
            return CopySelectionResult.Of(CopySelectionStatus.UnsafeApp, app);
        }

        // The shortcut's own keys are most likely still down: Copy pressed over them would be another shortcut altogether.
        if (!WaitUntil(() => !_native.AreKeysHeld(), _timings.KeyRelease))
        {
            return CopySelectionResult.Of(CopySelectionStatus.KeysHeld, app);
        }

        if (!StillForeground(app))
        {
            return CopySelectionResult.Of(CopySelectionStatus.ForegroundChanged, app);
        }

        if (_native.IsFocusedControlProtected())
        {
            return CopySelectionResult.Of(CopySelectionStatus.ProtectedControl, app);
        }

        // The user's clipboard is saved before anything is pressed, and nothing is pressed when it cannot be saved exactly.
        var failure = _native.TrySnapshot(out var snapshot);
        if (failure != SnapshotFailure.None || snapshot is null)
        {
            CopySelectionLog.NotSaved(_logger, failure);
            return CopySelectionResult.Of(
                failure == SnapshotFailure.Busy ? CopySelectionStatus.ClipboardBusy : CopySelectionStatus.ClipboardNotSaved, app);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!StillForeground(app))
        {
            return CopySelectionResult.Of(CopySelectionStatus.ForegroundChanged, app);
        }

        // From here the clipboard may change, and whatever happens it is put back; cancellation is no longer looked at.
        var before = _native.SequenceNumber();
        if (!_native.SendCopy())
        {
            CopySelectionLog.NotSent(_logger);
            return CopySelectionResult.Of(CopySelectionStatus.Failed, app);
        }

        if (!WaitUntil(() => _native.SequenceNumber() != before, _timings.CopyAppears))
        {
            // The application put nothing on the clipboard: nothing is selected, or it did not take the key press.
            return CopySelectionResult.Of(CopySelectionStatus.NothingCopied, app);
        }

        var copied = WaitForSettled();
        var outcome = _native.ReadText(SelectionService.MaxTextLength + 1, out var text);
        var restore = RestoreIfUnchanged(snapshot, copied);

        switch (outcome)
        {
            case ClipboardTextOutcome.Busy:
                return CopySelectionResult.Of(CopySelectionStatus.ClipboardBusy, app, restore);
            case ClipboardTextOutcome.NoText:
                return CopySelectionResult.Of(CopySelectionStatus.NotText, app, restore);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return CopySelectionResult.Of(CopySelectionStatus.NothingCopied, app, restore);
        }

        var isTruncated = text.Length > SelectionService.MaxTextLength;
        if (isTruncated)
        {
            text = char.IsHighSurrogate(text[SelectionService.MaxTextLength - 1])
                ? text[..(SelectionService.MaxTextLength - 1)]
                : text[..SelectionService.MaxTextLength];
        }

        return CopySelectionResult.Copied(app, text, isTruncated, restore);
    }

    // The previous clipboard goes back only over the copy this code made: when the change counter is not the one the copy left, someone else
    // has written to the clipboard since, and that newer content is not to be replaced with an older one.
    private ClipboardRestoreOutcome RestoreIfUnchanged(ClipboardSnapshot snapshot, uint copied)
    {
        if (_native.SequenceNumber() != copied)
        {
            CopySelectionLog.ClipboardChangedMeanwhile(_logger);
            return ClipboardRestoreOutcome.ChangedByOther;
        }

        if (_native.Restore(snapshot))
        {
            return ClipboardRestoreOutcome.Restored;
        }

        CopySelectionLog.NotRestored(_logger);
        return ClipboardRestoreOutcome.Failed;
    }

    // Some applications put their copy on the clipboard in more than one step, so it is read once the counter has stopped moving.
    private uint WaitForSettled()
    {
        var sequence = _native.SequenceNumber();
        var deadline = Stopwatch.GetTimestamp() + (long)(_timings.SettleLimit.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(_timings.Settle);
            var now = _native.SequenceNumber();
            if (now == sequence)
            {
                break;
            }

            sequence = now;
        }

        return sequence;
    }

    private bool StillForeground(ForegroundApp app) =>
        _native.GetForegroundApp() is { } now && now.WindowHandle == app.WindowHandle && now.ProcessId == app.ProcessId;

    private bool WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            Thread.Sleep(_timings.Poll);
        }
    }

    /// <summary>Whether Ctrl+C in this application would not be Copy: a terminal or console.</summary>
    internal static bool IsTerminal(ForegroundApp app, string windowClass) =>
        TerminalProcesses.Contains(app.ProcessName) || TerminalWindowClasses.Contains(windowClass);
}
