using Assistant.Windows.Selection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// The copy fallback for the selection (PROJECT_SPEC §4.5, step 89): Copy is pressed only where that is safe and only after the user's clipboard is
/// saved, what the application copied is read, and the previous clipboard goes back when nothing else has changed it. The desktop and the
/// clipboard are a fake here; the real calls are checked in <see cref="RealCopySelectionTests"/>.
/// </summary>
public sealed class CopySelectionServiceTests
{
    private static readonly ForegroundApp Editor = new(4120, "customeditor", @"C:\Apps\customeditor.exe", 0x1A2B);
    private static readonly byte[] OldText = [(byte)'o', 0, (byte)'l', 0, (byte)'d', 0, 0, 0];

    private readonly FakeClipboard _clipboard = new() { App = Editor };
    private readonly TestLogger _logger = new();

    private static CopySelectionTimings Quick { get; } = new(
        KeyRelease: TimeSpan.FromMilliseconds(80), CopyAppears: TimeSpan.FromMilliseconds(120), Settle: TimeSpan.FromMilliseconds(10),
        SettleLimit: TimeSpan.FromMilliseconds(200), Poll: TimeSpan.FromMilliseconds(2), Overall: TimeSpan.FromSeconds(5));

    private CopySelectionService Service(CopySelectionTimings? timings = null) =>
        new(_logger, _clipboard, timings ?? Quick, ownProcessId: 1);

    // ---- The copy ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Text_IsCopiedRead_AndThePreviousClipboardIsPutBack()
    {
        _clipboard.Contents = [new ClipboardFormatData(13, OldText), new ClipboardFormatData(49999, [1, 2, 3])];
        _clipboard.OnCopy = () => _clipboard.Write("Hello selected world");

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.Copied, result.Status);
        Assert.True(result.HasText);
        Assert.Equal("Hello selected world", result.Text);
        Assert.False(result.IsTruncated);
        Assert.Equal(Editor, result.App);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.Restore);

        // The clipboard is saved before Copy is pressed, and put back after the text was read, once, with exactly what was there.
        Assert.Equal(["snapshot", "send", "read", "restore"], _clipboard.Calls);
        Assert.Equal([new ClipboardFormatData(13, OldText), new ClipboardFormatData(49999, [1, 2, 3])], _clipboard.Contents, new FormatComparer());
    }

    [Fact]
    public async Task AnEmptyClipboard_IsLeftEmptyAfterwards()
    {
        _clipboard.Contents = [];
        _clipboard.OnCopy = () => _clipboard.Write("copied");

        var result = await Service().CopySelectionAsync();

        Assert.Equal("copied", result.Text);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.Restore);
        Assert.Empty(_clipboard.Contents);
    }

    [Fact]
    public async Task AnApplicationThatCopiesInSeveralSteps_IsReadOnceTheClipboardHasSettled()
    {
        // The first write is what Copy leaves at once; the rest arrives while the service is waiting for the counter to stop moving.
        _clipboard.OnCopy = () => _clipboard.Write("first");
        _clipboard.DelayedWrite = (Poll: 3, Text: "the complete text");

        var result = await Service().CopySelectionAsync();

        Assert.Equal("the complete text", result.Text);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.Restore);
    }

    [Fact]
    public async Task TextLongerThanTheLimit_IsCutWithoutSplittingASurrogatePair_AndSaidToBeTruncated()
    {
        var text = new string('a', SelectionService.MaxTextLength - 1) + "\U0001F98A" + "tail";
        _clipboard.OnCopy = () => _clipboard.Write(text);

        var result = await Service().CopySelectionAsync();

        Assert.True(result.IsTruncated);
        Assert.Equal(SelectionService.MaxTextLength - 1, result.Text!.Length);
        Assert.Equal(SelectionService.MaxTextLength + 1, _clipboard.MaxLengthAsked);
    }

    // ---- What happens when nothing is copied -------------------------------------------------------------------------------------

    [Fact]
    public async Task AnApplicationThatCopiesNothing_IsNothingCopied_AndTheClipboardWasNeverTouched()
    {
        _clipboard.Contents = [new ClipboardFormatData(13, OldText)];

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.NothingCopied, result.Status);
        Assert.Null(result.Text);
        Assert.Equal(ClipboardRestoreOutcome.NotNeeded, result.Restore);
        Assert.Equal(["snapshot", "send"], _clipboard.Calls);
    }

    [Fact]
    public async Task CopiedBlanks_AreNothingCopied_AndThePreviousClipboardIsBack()
    {
        _clipboard.Contents = [new ClipboardFormatData(13, OldText)];
        _clipboard.OnCopy = () => _clipboard.Write("  \r\n ");

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.NothingCopied, result.Status);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.Restore);
        Assert.Equal([new ClipboardFormatData(13, OldText)], _clipboard.Contents, new FormatComparer());
    }

    [Fact]
    public async Task SomethingThatIsNotText_IsNotText_AndThePreviousClipboardIsBack()
    {
        _clipboard.Contents = [new ClipboardFormatData(13, OldText)];
        _clipboard.OnCopy = () => _clipboard.WriteNonText();

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.NotText, result.Status);
        Assert.Equal(ClipboardRestoreOutcome.Restored, result.Restore);
        Assert.Equal([new ClipboardFormatData(13, OldText)], _clipboard.Contents, new FormatComparer());
    }

    // ---- Where Copy is not pressed ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("cmd", "")]
    [InlineData("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("pwsh", "")]
    [InlineData("powershell", "ConsoleWindowClass")]
    [InlineData("someotherterminal", "ConsoleWindowClass")]
    [InlineData("ConEmu64", "VirtualConsoleClass")]
    public async Task InATerminal_NothingIsSent_BecauseCtrlCThereStopsTheRunningProgram(string process, string windowClass)
    {
        _clipboard.App = new ForegroundApp(77, process, null, 0x55);
        _clipboard.WindowClass = windowClass;
        _clipboard.OnCopy = () => _clipboard.Write("must never be asked for");

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.UnsafeApp, result.Status);
        Assert.Empty(_clipboard.Calls);
    }

    [Fact]
    public async Task TheAssistantsOwnWindow_IsNeverSentCopy()
    {
        _clipboard.App = new ForegroundApp(1, "assistant.ui", null, 0x77);

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.OwnWindow, result.Status);
        Assert.Empty(_clipboard.Calls);
    }

    [Fact]
    public async Task WhileTheShortcutsKeysAreStillHeld_NothingIsSent()
    {
        _clipboard.KeysHeld = int.MaxValue;

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.KeysHeld, result.Status);
        Assert.Empty(_clipboard.Calls);
    }

    [Fact]
    public async Task KeysThatAreLetGoOfInTime_AreWaitedFor()
    {
        _clipboard.KeysHeld = 5;
        _clipboard.OnCopy = () => _clipboard.Write("text");

        var result = await Service().CopySelectionAsync();

        Assert.Equal("text", result.Text);
    }

    [Fact]
    public async Task APasswordBox_IsNeverSentCopy()
    {
        _clipboard.Protected = true;

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.ProtectedControl, result.Status);
        Assert.Empty(_clipboard.Calls);
    }

    [Fact]
    public async Task WhenTheWindowInFrontChangesBeforeCopy_NothingIsSent()
    {
        // The window is looked at when the work begins, again after the keys are let go, and once more just before Copy: the third look finds another.
        _clipboard.ForegroundChangesAfter = 2;

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.ForegroundChanged, result.Status);
        Assert.DoesNotContain("send", _clipboard.Calls);
    }

    [Fact]
    public async Task NoWindowInFront_IsSaidSo_AndNothingIsSent()
    {
        _clipboard.App = null;

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.NoForegroundApp, result.Status);
        Assert.Null(result.App);
        Assert.Empty(_clipboard.Calls);
    }

    [Theory]
    [InlineData((int)SnapshotFailure.Unsupported, CopySelectionStatus.ClipboardNotSaved)]
    [InlineData((int)SnapshotFailure.TooLarge, CopySelectionStatus.ClipboardNotSaved)]
    [InlineData((int)SnapshotFailure.Busy, CopySelectionStatus.ClipboardBusy)]
    public async Task AClipboardThatCannotBeSavedWhole_IsNeverTouched(int failure, CopySelectionStatus expected)
    {
        _clipboard.SnapshotFailure = (SnapshotFailure)failure;
        _clipboard.OnCopy = () => _clipboard.Write("must never be asked for");

        var result = await Service().CopySelectionAsync();

        Assert.Equal(expected, result.Status);
        Assert.Equal(ClipboardRestoreOutcome.NotNeeded, result.Restore);
        Assert.Equal(["snapshot"], _clipboard.Calls);
    }

    [Fact]
    public async Task WhenTheKeyPressCouldNotBeSent_ItFailsWithTheClipboardUntouched()
    {
        _clipboard.SendSucceeds = false;

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.Failed, result.Status);
        Assert.DoesNotContain("restore", _clipboard.Calls);
    }

    // ---- Putting the clipboard back ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClipboardThatSomeoneElseChangedAfterTheCopy_IsNotWrittenOver_ButTheTextIsStillReturned()
    {
        _clipboard.Contents = [new ClipboardFormatData(13, OldText)];
        _clipboard.OnCopy = () => _clipboard.Write("selected");
        _clipboard.OnRead = () => _clipboard.Write("something newer the user copied");

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.Copied, result.Status);
        Assert.Equal("selected", result.Text);
        Assert.Equal(ClipboardRestoreOutcome.ChangedByOther, result.Restore);
        Assert.DoesNotContain("restore", _clipboard.Calls);
    }

    [Fact]
    public async Task APreviousClipboardThatCouldNotBePutBack_IsSaidSo()
    {
        _clipboard.OnCopy = () => _clipboard.Write("selected");
        _clipboard.RestoreSucceeds = false;

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.Copied, result.Status);
        Assert.Equal("selected", result.Text);
        Assert.Equal(ClipboardRestoreOutcome.Failed, result.Restore);
        Assert.Contains(_logger.Messages, message => message.Contains("could not be put back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClipboardThatIsBusyWhenTheTextIsRead_StillPutsThePreviousOneBack()
    {
        _clipboard.OnCopy = () => _clipboard.Write("selected");
        _clipboard.ReadOutcome = ClipboardTextOutcome.Busy;

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.ClipboardBusy, result.Status);
        Assert.Contains("restore", _clipboard.Calls);
    }

    // ---- Everything else --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AlreadyCancelled_ThrowsBeforeAnythingIsDone()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CopySelectionAsync(cancelled.Token));

        Assert.Empty(_clipboard.Calls);
    }

    [Fact]
    public async Task AnExceptionFromWindows_IsAFailedStatus_NotAnException()
    {
        _clipboard.Throw = new InvalidOperationException("secret detail that must not be logged");

        var result = await Service().CopySelectionAsync();

        Assert.Equal(CopySelectionStatus.Failed, result.Status);
        Assert.DoesNotContain("secret detail", string.Join('\n', _logger.Messages), StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", string.Join('\n', _logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkThatDoesNotFinishInTime_IsGivenUpOn()
    {
        using var block = new ManualResetEventSlim();
        _clipboard.Block = block;
        var timings = Quick with { Overall = TimeSpan.FromMilliseconds(150) };

        var result = await Service(timings).CopySelectionAsync();
        block.Set();

        Assert.Equal(CopySelectionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task TheCopiedTextAndTheClipboardsContents_ReachNeitherALogNorToString()
    {
        _clipboard.Contents = [new ClipboardFormatData(13, OldText)];
        _clipboard.OnCopy = () => _clipboard.Write("a very private sentence");

        var result = await Service().CopySelectionAsync();

        var logged = string.Join('\n', _logger.Messages) + result + new ClipboardSnapshot(_clipboard.Contents);
        Assert.DoesNotContain("private sentence", logged, StringComparison.Ordinal);
        Assert.Contains(_logger.Messages, message => message.Contains("customeditor", StringComparison.Ordinal));
        Assert.DoesNotContain(@"C:\Apps", logged, StringComparison.Ordinal);
    }

    [Fact]
    public void ATerminalIsRecognizedByProgramOrByWindowClass()
    {
        Assert.True(CopySelectionService.IsTerminal(new ForegroundApp(1, "WINDOWSTERMINAL", null, 1), ""));
        Assert.True(CopySelectionService.IsTerminal(new ForegroundApp(1, "anything", null, 1), "ConsoleWindowClass"));
        Assert.False(CopySelectionService.IsTerminal(new ForegroundApp(1, "notepad", null, 1), "Notepad"));
        Assert.False(CopySelectionService.IsTerminal(new ForegroundApp(1, "code", null, 1), "Chrome_WidgetWin_1"));
    }

    private sealed class FormatComparer : IEqualityComparer<ClipboardFormatData>
    {
        public bool Equals(ClipboardFormatData? x, ClipboardFormatData? y) =>
            x is not null && y is not null && x.Format == y.Format && x.Data.AsSpan().SequenceEqual(y.Data);

        public int GetHashCode(ClipboardFormatData obj) => obj.Format.GetHashCode();
    }

    // A desktop and a clipboard that do what the real ones do, as far as the service can tell: a change counter that moves with every write,
    // formats that hold bytes, and Copy that runs whatever the test says the application does.
    private sealed class FakeClipboard : IClipboardNativeMethods
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = [];
        private uint _sequence = 100;
        private int _foregroundLooks;
        private int _keysLooks;
        private int _pollsAfterCopy;
        private bool _copied;

        public ForegroundApp? App { get; set; }

        public string WindowClass { get; set; } = "Edit";

        public bool Protected { get; set; }

        public int KeysHeld { get; set; }

        public int ForegroundChangesAfter { get; set; } = int.MaxValue;

        public SnapshotFailure SnapshotFailure { get; set; }

        public bool SendSucceeds { get; set; } = true;

        public bool RestoreSucceeds { get; set; } = true;

        public ClipboardTextOutcome? ReadOutcome { get; set; }

        public Action? OnCopy { get; set; }

        public Action? OnRead { get; set; }

        public Exception? Throw { get; set; }

        /// <summary>A write that happens inside the n-th look at the change counter after Copy was pressed.</summary>
        public (int Poll, string Text)? DelayedWrite { get; set; }

        public ManualResetEventSlim? Block { get; set; }

        public int MaxLengthAsked { get; private set; }

        public List<ClipboardFormatData> Contents { get; set; } = [];

        public List<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public void Write(string text)
        {
            lock (_gate)
            {
                Contents = [new ClipboardFormatData(13, System.Text.Encoding.Unicode.GetBytes(text + "\0"))];
                _sequence++;
            }
        }

        public void WriteNonText()
        {
            lock (_gate)
            {
                Contents = [new ClipboardFormatData(8, [9, 9, 9, 9])];
                _sequence++;
            }
        }

        public ForegroundApp? GetForegroundApp()
        {
            Block?.Wait(TimeSpan.FromSeconds(30));
            if (Throw is not null)
            {
                throw Throw;
            }

            // After a number of looks another window is in front.
            return ++_foregroundLooks > ForegroundChangesAfter && App is not null ? App with { WindowHandle = App.WindowHandle + 1 } : App;
        }

        public string WindowClassOf(nint window) => WindowClass;

        public bool IsFocusedControlProtected() => Protected;

        public bool AreKeysHeld() => _keysLooks++ < KeysHeld;

        public uint SequenceNumber()
        {
            lock (_gate)
            {
                if (_copied && DelayedWrite is { } delayed && ++_pollsAfterCopy == delayed.Poll)
                {
                    Contents = [new ClipboardFormatData(13, System.Text.Encoding.Unicode.GetBytes(delayed.Text + "\0"))];
                    _sequence++;
                }

                return _sequence;
            }
        }

        public SnapshotFailure TrySnapshot(out ClipboardSnapshot? snapshot)
        {
            Record("snapshot");
            lock (_gate)
            {
                snapshot = SnapshotFailure == SnapshotFailure.None ? new ClipboardSnapshot([.. Contents]) : null;
                return SnapshotFailure;
            }
        }

        public bool SendCopy()
        {
            Record("send");
            if (SendSucceeds)
            {
                OnCopy?.Invoke();
                _copied = true;
            }

            return SendSucceeds;
        }

        public ClipboardTextOutcome ReadText(int maxLength, out string? text)
        {
            Record("read");
            MaxLengthAsked = maxLength;
            text = null;
            ClipboardTextOutcome outcome;
            lock (_gate)
            {
                var unicode = Contents.FirstOrDefault(item => item.Format == 13);
                if (ReadOutcome is { } forced)
                {
                    outcome = forced;
                }
                else if (unicode is null)
                {
                    outcome = ClipboardTextOutcome.NoText;
                }
                else
                {
                    var all = System.Text.Encoding.Unicode.GetString(unicode.Data).TrimEnd('\0');
                    text = all.Length > maxLength ? all[..maxLength] : all;
                    outcome = ClipboardTextOutcome.Text;
                }
            }

            // Whatever the test says happens right after the text was read, such as the user copying something else.
            OnRead?.Invoke();
            return outcome;
        }

        public bool Restore(ClipboardSnapshot snapshot)
        {
            Record("restore");
            if (!RestoreSucceeds)
            {
                return false;
            }

            lock (_gate)
            {
                Contents = [.. snapshot.Formats];
                _sequence++;
            }

            return true;
        }

        private void Record(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }
    }

    private sealed class TestLogger : ILogger<CopySelectionService>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public List<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
