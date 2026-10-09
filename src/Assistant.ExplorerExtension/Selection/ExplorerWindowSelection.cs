using System.Reflection;
using System.Runtime.InteropServices;

namespace Assistant.ExplorerExtension.Selection;

/// <summary>
/// Asks File Explorer for the selection, from outside it (PROJECT_SPEC §4.4): the open Explorer windows and the desktop are
/// listed by <c>Shell.Application</c>, each shows its selected items through <c>SelectedItems</c>, and the window whose selection
/// holds the file the menu was opened on is the one. Explorer starts this process once for each selected file, whatever the file's
/// type, so the first of them reads what all of them were started for. The calls are late bound, so that no interop assembly is
/// loaded and the entry point stays small; a window that does not answer is skipped, never waited for.
/// </summary>
internal sealed class ExplorerWindowSelection(Func<nint>? foregroundWindow = null) : ISelectionSource
{
    private const BindingFlags Call = BindingFlags.GetProperty | BindingFlags.InvokeMethod;
    private const int DesktopFolder = 0;
    private const int DesktopWindowClass = 8;
    private const int NeedDispatch = 1;
    private readonly Func<nint> _foreground = foregroundWindow ?? (() => NativeMethods.GetForegroundWindow());

    /// <inheritdoc/>
    public IReadOnlyList<string>? TryRead(IReadOnlyList<string> clicked)
    {
        ArgumentNullException.ThrowIfNull(clicked);
        if (clicked.Count == 0)
        {
            return null;
        }

        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type || Activator.CreateInstance(type) is not { } shell)
            {
                return null;
            }

            var windows = Get(shell, "Windows");
            return windows is null ? null : Find(windows, clicked);
        }
        catch (Exception exception) when (IsComFailure(exception))
        {
            return null;
        }
    }

    private IReadOnlyList<string>? Find(object windows, IReadOnlyList<string> clicked)
    {
        var foreground = _foreground();
        IReadOnlyList<string>? first = null;
        var count = Convert.ToInt32(Get(windows, "Count") ?? 0);
        for (var index = 0; index < count; index++)
        {
            var selection = ReadSelection(Get(windows, "Item", index), clicked, out var handle);
            if (selection is null)
            {
                continue;
            }

            // Several windows can show the same selection; the one in front is the one the user used.
            if (handle == foreground)
            {
                return selection;
            }

            first ??= selection;
        }

        return first ?? DesktopSelection(windows, clicked);
    }

    // The desktop is not in the list of windows: it is asked for by its own folder.
    private static IReadOnlyList<string>? DesktopSelection(object windows, IReadOnlyList<string> clicked)
    {
        try
        {
            object?[] arguments = [DesktopFolder, null, DesktopWindowClass, 0, NeedDispatch];
            var modifiers = new ParameterModifier(arguments.Length);
            modifiers[3] = true;
            var desktop = windows.GetType().InvokeMember(
                "FindWindowSW", BindingFlags.InvokeMethod, null, windows, arguments, [modifiers], null, null);
            return ReadSelection(desktop, clicked, out _);
        }
        catch (Exception exception) when (IsComFailure(exception))
        {
            return null;
        }
    }

    // The paths selected in one window, written as the clicked ones are, if that window is a file view and has every clicked path
    // selected. Items that are not files on a drive or a share (a library, This PC) have no path and are left out.
    private static IReadOnlyList<string>? ReadSelection(object? window, IReadOnlyList<string> clicked, out nint handle)
    {
        handle = 0;
        if (window is null)
        {
            return null;
        }

        try
        {
            handle = (nint)Convert.ToInt64(Get(window, "HWND") ?? 0);
            var items = Get(Get(window, "Document"), "SelectedItems");
            if (items is null)
            {
                return null;
            }

            var paths = new List<string>();
            var count = Convert.ToInt32(Get(items, "Count") ?? 0);
            for (var index = 0; index < count; index++)
            {
                if (ExplorerPaths.Prepare(Get(Get(items, "Item", index), "Path") as string) is { } path)
                {
                    paths.Add(path);
                }
            }

            return clicked.All(file => paths.Contains(file, StringComparer.OrdinalIgnoreCase)) ? paths : null;
        }
        catch (Exception exception) when (IsComFailure(exception))
        {
            // A window that is closing, or is not a folder view (Internet Explorer's, a control panel's), has no selection.
            return null;
        }
    }

    private static object? Get(object? target, string name, params object[] arguments) =>
        target?.GetType().InvokeMember(name, Call, null, target, arguments);

    private static bool IsComFailure(Exception exception) =>
        exception is COMException or InvalidComObjectException or MissingMemberException or TargetInvocationException
            or InvalidCastException or ArgumentException or NotSupportedException or FormatException or OverflowException;
}
