using System.Diagnostics;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Selection;

/// <summary>The real thing: Win32 names the foreground application, and UI Automation reads its selection.</summary>
internal sealed unsafe class SelectionNativeMethods : ISelectionNativeMethods
{
    // The longest path Windows has (32 767 characters) and its end.
    private const int MaxPath = 32768;

    public ForegroundApp? GetForegroundApp()
    {
        var window = User32.GetForegroundWindow();
        if (window == 0)
        {
            return null;
        }

        User32.GetWindowThreadProcessId(window, out var processId);
        return processId == 0 ? null : Describe((int)processId, window);
    }

    public ForegroundApp? DescribeProcess(int processId, nint window) => processId <= 0 ? null : Describe(processId, window);

    public SelectionProbe ReadSelection(int maxLength) => UiaSelectionReader.Read(maxLength);

    // The path comes from a limited-rights handle, which Windows gives for elevated processes too; the name is the file's, as the
    // Task Manager's is.
    private static ForegroundApp Describe(int processId, nint window)
    {
        var path = ImagePath(processId);
        var name = path is not null ? Path.GetFileNameWithoutExtension(path) : NameOf(processId);
        return new ForegroundApp(processId, name, path, window);
    }

    private static string? ImagePath(int processId)
    {
        var process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[MaxPath];
            var size = (uint)buffer.Length;
            fixed (char* path = buffer)
            {
                return Kernel32.QueryFullProcessImageName(process, 0, path, ref size) && size > 0 ? new string(path, 0, (int)size) : null;
            }
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    private static string NameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
