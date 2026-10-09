using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Assistant.Windows.Selection;

/// <summary>
/// The app's <see cref="ISelectionService"/>: asks the foreground application for its selected text through Windows UI Automation
/// (the TextPattern of the focused control, or of the control that holds it), which reads the selection without touching the
/// clipboard, the focus or the selection itself (PROJECT_SPEC §4.2, capture order 1).
/// </summary>
/// <remarks>
/// <para>
/// Applications differ in what they expose: classic edit controls, Word, Notepad, Edge and Chrome (once Windows has asked them for
/// accessibility) do, many custom-drawn applications and games do not. What an application does not expose is not an error: the answer
/// is <see cref="SelectionStatus.Unsupported"/>, which tells a caller that another way of capturing may work, where
/// <see cref="SelectionStatus.NoSelection"/> tells it the application was asked and nothing is selected.
/// </para>
/// <para>
/// The question goes to the application, which can be slow or stuck, on a thread of its own and for no longer than
/// <see cref="DefaultTimeout"/>; an application that does not answer in time is <see cref="SelectionStatus.Unsupported"/>. Windows will
/// not let this process ask an application that runs with more rights (an elevated one): that too is
/// <see cref="SelectionStatus.Unsupported"/>. Neither the selected text nor a window title is ever logged.
/// </para>
/// </remarks>
public sealed class SelectionService : ISelectionService
{
    /// <summary>The most characters of a selection that are returned, 200 000 (about 40 pages); a longer one is cut and marked <see cref="SelectionResult.IsTruncated"/>.</summary>
    public const int MaxTextLength = 200_000;

    /// <summary>How long an application has to answer.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    // The host that shows modern (UWP) apps owns their foreground window, while the app itself is another process.
    private const string FrameHostProcess = "ApplicationFrameHost";

    private readonly ISelectionNativeMethods _native;
    private readonly TimeSpan _timeout;
    private readonly ILogger<SelectionService> _logger;

    /// <summary>Creates the service over the real desktop.</summary>
    public SelectionService(ILogger<SelectionService> logger)
        : this(logger, new SelectionNativeMethods(), DefaultTimeout)
    {
    }

    internal SelectionService(ILogger<SelectionService> logger, ISelectionNativeMethods native, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(native);
        _logger = logger;
        _native = native;
        _timeout = timeout;
    }

    /// <inheritdoc/>
    public async Task<SelectionResult> GetSelectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = Stopwatch.GetTimestamp();

        var app = Foreground();
        if (app is null)
        {
            SelectionLog.NoForeground(_logger);
            return SelectionResult.NoForegroundApp();
        }

        var probe = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        var result = Resolve(app, probe);
        SelectionLog.Answered(
            _logger, result.App?.ProcessName ?? app.ProcessName, result.Status, result.Text?.Length ?? 0, result.IsTruncated,
            (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return result;
    }

    private SelectionResult Resolve(ForegroundApp app, SelectionProbe probe)
    {
        if (probe.Outcome == SelectionProbeOutcome.Failed)
        {
            SelectionLog.ProbeFailed(_logger, app.ProcessName, probe.FailureType ?? "unknown");
        }

        // The focused control must be the foreground application's: focus can sit in a window behind (or in a process that is
        // not the one in front), and its selection is not what the user is looking at. A modern app is the exception: its
        // foreground window is the frame host's, and the control is the app's, which is then the application to name.
        if (probe.ControlProcessId != 0 && probe.ControlProcessId != app.ProcessId)
        {
            if (!app.ProcessName.Equals(FrameHostProcess, StringComparison.OrdinalIgnoreCase))
            {
                SelectionLog.FocusElsewhere(_logger, app.ProcessName);
                return SelectionResult.Unsupported(app);
            }

            app = Describe(probe.ControlProcessId, app.WindowHandle) ?? app;
        }

        switch (probe.Outcome)
        {
            case SelectionProbeOutcome.Selected when !string.IsNullOrWhiteSpace(probe.Text):
                var text = probe.Text;
                var isTruncated = text.Length > MaxTextLength;
                if (isTruncated)
                {
                    text = CutAt(text, MaxTextLength);
                }

                return SelectionResult.Selected(app, text, isTruncated);

            // A control that exposes its selection and has only blanks (or the caret) selected has nothing to ask about.
            case SelectionProbeOutcome.Selected:
            case SelectionProbeOutcome.Empty:
                return SelectionResult.NoSelection(app);

            default:
                SelectionLog.Unsupported(_logger, app.ProcessName, probe.Outcome);
                return SelectionResult.Unsupported(app);
        }
    }

    // Runs the question on a thread of its own, which is left behind when the application does not answer: UI Automation calls
    // cannot be cancelled, and a stuck one must not hold a thread-pool thread.
    private async Task<SelectionProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<SelectionProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                answer.TrySetResult(_native.ReadSelection(MaxTextLength));
            }
            catch (Exception exception)
            {
                answer.TrySetResult(new SelectionProbe(SelectionProbeOutcome.Failed, FailureType: exception.GetType().Name));
            }
        })
        {
            IsBackground = true,
            Name = "Selection probe",
        };

        // UI Automation is called from a multithreaded apartment, so that it never waits on a message pump.
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        try
        {
            return await answer.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new SelectionProbe(SelectionProbeOutcome.TimedOut);
        }
    }

    private ForegroundApp? Foreground()
    {
        try
        {
            return _native.GetForegroundApp();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ForegroundApp? Describe(int processId, nint window)
    {
        try
        {
            return _native.DescribeProcess(processId, window);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Cuts the text to the length without leaving half a surrogate pair at its end.
    private static string CutAt(string text, int length) =>
        char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];
}
