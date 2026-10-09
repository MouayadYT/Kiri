using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Selection;

/// <summary>
/// Reads the selection of the control with the keyboard focus through the UI Automation client. The focused control is asked for a
/// TextPattern; when it has none (a browser's focus is often on a link or a button inside the page, whose document holds the
/// selection) the controls around it are asked, nearest first.
/// </summary>
internal static class UiaSelectionReader
{
    // How far up from the focused control to look for one that exposes text: a page's document is a few levels up from a link.
    private const int MaxAncestors = 12;

    /// <summary>Reads at most <paramref name="maxLength"/> + 1 characters. Does not throw; call it from a multithreaded apartment.</summary>
    public static SelectionProbe Read(int maxLength)
    {
        try
        {
            return ReadFrom(FocusedControl(), maxLength);
        }
        catch (Exception exception)
        {
            return new SelectionProbe(SelectionProbeOutcome.Failed, FailureType: exception.GetType().Name);
        }
    }

    // The control UI Automation says has the keyboard focus. Its notion of focus follows the focus events Windows announces, and can be
    // without one for a while after the window in front changed (it then throws, as for nothing focused). The window in front says for
    // itself which of its windows has the keyboard, so that one is asked about instead, and the controls around it as usual.
    private static AutomationElement? FocusedControl()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (InvalidOperationException)
        {
            return FocusedWindowOfForeground() is var window and not 0 ? AutomationElement.FromHandle(window) : null;
        }
    }

    // The window with the keyboard focus in the thread of the window in front, or 0.
    private static nint FocusedWindowOfForeground()
    {
        var foreground = User32.GetForegroundWindow();
        if (foreground == 0)
        {
            return 0;
        }

        var thread = User32.GetWindowThreadProcessId(foreground, out _);
        var info = new User32.GuiThreadInfo { Size = (uint)Marshal.SizeOf<User32.GuiThreadInfo>() };
        return thread != 0 && User32.GetGuiThreadInfo(thread, ref info) ? info.Focus : 0;
    }

    /// <summary>Reads the selection of <paramref name="focused"/> (or of the control around it that holds the text). Does not throw.</summary>
    internal static SelectionProbe ReadFrom(AutomationElement? focused, int maxLength)
    {
        var processId = 0;
        try
        {
            if (focused is null)
            {
                return new SelectionProbe(SelectionProbeOutcome.NoFocusedControl);
            }

            processId = focused.Current.ProcessId;
            if (focused.Current.IsPassword)
            {
                return new SelectionProbe(SelectionProbeOutcome.Protected, ControlProcessId: processId);
            }

            var walker = TreeWalker.ControlViewWalker;
            var element = focused;
            for (var depth = 0; element is not null && depth <= MaxAncestors; depth++)
            {
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                {
                    return ReadSelection((TextPattern)pattern, maxLength, processId);
                }

                element = walker.GetParent(element);
            }

            return new SelectionProbe(SelectionProbeOutcome.NoTextPattern, ControlProcessId: processId);
        }
        catch (Exception exception)
        {
            // The control going away, the application refusing (more rights than this process has) and a broken provider all end here.
            // Only the type of what failed is kept: a message could quote the text.
            return new SelectionProbe(SelectionProbeOutcome.Failed, ControlProcessId: processId, FailureType: exception.GetType().Name);
        }
    }

    private static SelectionProbe ReadSelection(TextPattern pattern, int maxLength, int processId)
    {
        if (pattern.SupportedTextSelection == SupportedTextSelection.None)
        {
            return new SelectionProbe(SelectionProbeOutcome.NoSelectionSupport, ControlProcessId: processId);
        }

        // A selection of several parts (as in Word, or a table) is one text with a line break between its parts.
        var text = new StringBuilder();
        foreach (var range in pattern.GetSelection())
        {
            var room = maxLength + 1 - text.Length - (text.Length > 0 ? 1 : 0);
            if (room <= 0)
            {
                break;
            }

            var part = range.GetText(room);
            if (part.Length == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(part);
        }

        return text.Length == 0
            ? new SelectionProbe(SelectionProbeOutcome.Empty, ControlProcessId: processId)
            : new SelectionProbe(SelectionProbeOutcome.Selected, text.ToString(), processId);
    }
}
