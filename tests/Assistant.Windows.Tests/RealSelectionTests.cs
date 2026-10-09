using System.Runtime.InteropServices;
using System.Windows.Automation;
using Assistant.Windows.Selection;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// The real UI Automation reader and the real Windows calls behind <see cref="SelectionService"/>, against real windows: Win32 edit
/// controls this test creates, which hold text and a selection, and the process this test runs in. The reader is given the control
/// itself, as UI Automation hands it over when that control has the keyboard focus, so no test depends on which window is in front.
/// What the tests read is their own text, and they keep nothing.
/// </summary>
public sealed class RealSelectionTests
{
    private const uint EmSetSel = 0x00B1;
    private const int Limit = SelectionService.MaxTextLength;

    private static readonly ISelectionNativeMethods Native = new SelectionNativeMethods();

    [Fact]
    public void TheSelectedPartOfAnEditControl_IsRead()
    {
        using var edit = new EditWindow("Hello selected world", multiline: false);
        edit.Select(6, 14);

        var probe = Read(edit);

        Assert.Equal(SelectionProbeOutcome.Selected, probe.Outcome);
        Assert.Equal("selected", probe.Text);
        Assert.Equal(Environment.ProcessId, probe.ControlProcessId);
    }

    [Fact]
    public void ACaretWithNothingSelected_IsEmpty()
    {
        using var edit = new EditWindow("Hello world", multiline: false);
        edit.Select(3, 3);

        var probe = Read(edit);

        Assert.Equal(SelectionProbeOutcome.Empty, probe.Outcome);
        Assert.Null(probe.Text);
    }

    [Fact]
    public void ASelectionAcrossLines_IsReadWithItsLineBreak()
    {
        using var edit = new EditWindow("first line\r\nsecond line\r\nthird line", multiline: true);
        edit.Select(6, 23);

        var probe = Read(edit);

        Assert.Equal(SelectionProbeOutcome.Selected, probe.Outcome);
        Assert.Equal("line\r\nsecond line", probe.Text);
    }

    [Fact]
    public void ASelectionLongerThanAsked_IsReadOnlyOneCharacterBeyondTheLimit()
    {
        using var edit = new EditWindow(new string('x', 5000), multiline: true);
        edit.Select(0, 5000);

        var probe = UiaSelectionReader.ReadFrom(AutomationElement.FromHandle(edit.Handle), 100);

        Assert.Equal(SelectionProbeOutcome.Selected, probe.Outcome);
        Assert.Equal(101, probe.Text!.Length);
    }

    [Fact]
    public void ThePasswordBoxsSelection_IsNeverRead()
    {
        using var edit = new EditWindow("hunter2", multiline: false, password: true);
        edit.Select(0, 7);

        var probe = Read(edit);

        Assert.Equal(SelectionProbeOutcome.Protected, probe.Outcome);
        Assert.Null(probe.Text);
    }

    [Fact]
    public void AControlThatExposesNoText_HasNoTextPattern()
    {
        using var button = new EditWindow("Press me", multiline: false, className: "BUTTON");

        var probe = Read(button);

        Assert.Equal(SelectionProbeOutcome.NoTextPattern, probe.Outcome);
        Assert.Null(probe.Text);
    }

    [Fact]
    public void NothingFocused_IsNoFocusedControl()
    {
        Assert.Equal(SelectionProbeOutcome.NoFocusedControl, UiaSelectionReader.ReadFrom(null, Limit).Outcome);
    }

    [Fact]
    public void AControlThatIsGone_FailsQuietly_WithTheTypeOfTheFailureOnly()
    {
        AutomationElement element;
        using (var edit = new EditWindow("short lived", multiline: false))
        {
            element = AutomationElement.FromHandle(edit.Handle);
        }

        var probe = UiaSelectionReader.ReadFrom(element, Limit);

        Assert.True(probe.Outcome is SelectionProbeOutcome.Failed);
        Assert.False(string.IsNullOrEmpty(probe.FailureType));
        Assert.Null(probe.Text);
    }

    [Fact]
    public void TheCurrentProcess_IsDescribedByItsRealPathAndName()
    {
        var app = Native.DescribeProcess(Environment.ProcessId, 0x1234);

        Assert.NotNull(app);
        Assert.Equal(Environment.ProcessId, app.ProcessId);
        Assert.Equal(Environment.ProcessPath, app.ExecutablePath, ignoreCase: true);
        Assert.Equal(Path.GetFileNameWithoutExtension(Environment.ProcessPath), app.ProcessName);
        Assert.Equal(0x1234, app.WindowHandle);
    }

    [Fact]
    public void AProcessThatDoesNotExist_IsStillDescribed_WithNoPathOrName()
    {
        // Process ids are multiples of four, so this one is no process; the answer does not throw.
        var app = Native.DescribeProcess(0x7FFFFFFD, 0);

        Assert.NotNull(app);
        Assert.Null(app.ExecutablePath);
        Assert.Equal(string.Empty, app.ProcessName);
    }

    [DesktopFact]
    public void TheForegroundApplication_IsNamedByTheRealDesktop()
    {
        var app = Native.GetForegroundApp();

        // Another window can take the foreground between the attribute's check and this call, so none is as good as one.
        if (app is null)
        {
            return;
        }

        Assert.True(app.ProcessId > 0);
        Assert.NotEqual(0, app.WindowHandle);
        Assert.NotEmpty(app.ProcessName);
        Assert.True(app.ExecutablePath is null || File.Exists(app.ExecutablePath));
    }

    [DesktopFact]
    public async Task TheRealService_NeverThrows_WhateverIsInFront()
    {
        var service = new SelectionService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SelectionService>.Instance);

        var result = await service.GetSelectionAsync();

        Assert.True(Enum.IsDefined(result.Status));
        Assert.Equal(result.Status == SelectionStatus.Selected, result.Text is not null);
        Assert.Equal(result.Status == SelectionStatus.NoForegroundApp, result.App is null);
    }

    private static SelectionProbe Read(EditWindow window) =>
        UiaSelectionReader.ReadFrom(AutomationElement.FromHandle(window.Handle), Limit);

    /// <summary>A top-level window of a system class (an edit control by default) on a thread of its own that pumps its messages.</summary>
    private sealed class EditWindow : IDisposable
    {
        private const uint WsOverlappedWindow = 0x00CF0000;
        private const uint EsMultiline = 0x0004;
        private const uint EsPassword = 0x0020;
        private const uint WmClose = 0x0010;
        private const uint WmQuit = 0x0012;

        private readonly Thread _thread;

        public EditWindow(string text, bool multiline, bool password = false, string className = "EDIT")
        {
            var created = new ManualResetEventSlim();
            nint handle = 0;
            _thread = new Thread(() =>
            {
                var style = WsOverlappedWindow | (multiline ? EsMultiline : 0) | (password ? EsPassword : 0);
                handle = CreateWindowEx(0, className, text, style, 100, 100, 400, 200, 0, 0, 0, 0);
                created.Set();
                while (GetMessage(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            })
            {
                IsBackground = true,
                Name = "Real selection test window",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(created.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotEqual(0, handle);
            Handle = handle;
        }

        public nint Handle { get; }

        public void Select(int start, int end) => SendMessage(Handle, EmSetSel, start, end);

        public void Dispose()
        {
            // WM_CLOSE destroys the window, and destroying the thread's last window ends nothing by itself: the loop is told to quit.
            var thread = GetWindowThreadProcessId(Handle, out _);
            SendMessage(Handle, WmClose, 0, 0);
            PostThreadMessage(thread, WmQuit, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public nint Window;
            public uint Message;
            public nint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(
            uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu,
            nint instance, nint parameter);

        [DllImport("user32.dll", EntryPoint = "GetMessageW")]
        private static extern int GetMessage(out Msg message, nint window, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        private static extern nint DispatchMessage(ref Msg message);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
        private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}
