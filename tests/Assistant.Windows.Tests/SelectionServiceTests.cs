using Assistant.Windows.Selection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Reading the selected text of the foreground application (PROJECT_SPEC §4.2): what each answer of an application becomes, that an
/// application that does not expose its selection is a plain status and never an exception, and that the text reaches no log.
/// The desktop is a fake here; the real UI Automation reader is tested in <see cref="RealSelectionTests"/>.
/// </summary>
public sealed class SelectionServiceTests
{
    private static readonly ForegroundApp Notepad = new(4120, "notepad", @"C:\Windows\System32\notepad.exe", 0x1A2B);

    private readonly FakeDesktop _desktop = new() { App = Notepad };
    private readonly TestLogger _logger = new();

    private SelectionService Service => new(_logger, _desktop, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task SelectedText_IsReturned_WithTheApplicationItIsIn()
    {
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, "Hello, world", Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Selected, result.Status);
        Assert.True(result.HasText);
        Assert.Equal("Hello, world", result.Text);
        Assert.False(result.IsTruncated);
        Assert.Equal(Notepad, result.App);
        Assert.Equal(SelectionService.MaxTextLength, _desktop.MaxLengthAsked);
    }

    [Fact]
    public async Task TheTextIsGivenAsTheApplicationHadIt_BlanksAndLineBreaksIncluded()
    {
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, "  first\r\n\tsecond  \n", Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.Equal("  first\r\n\tsecond  \n", result.Text);
    }

    [Fact]
    public async Task AControlThatExposesItsSelection_WithNothingSelected_IsNoSelection()
    {
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Empty, ControlProcessId: Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.NoSelection, result.Status);
        Assert.False(result.HasText);
        Assert.Null(result.Text);
        Assert.Equal(Notepad, result.App);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \r\n\t ")]
    public async Task ASelectionOfOnlyBlanks_IsNoSelection(string blanks)
    {
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, blanks, Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.NoSelection, result.Status);
        Assert.Null(result.Text);
    }

    // The outcome is internal, so the cases are its numbers.
    [Theory]
    [InlineData((int)SelectionProbeOutcome.NoTextPattern)]
    [InlineData((int)SelectionProbeOutcome.NoSelectionSupport)]
    [InlineData((int)SelectionProbeOutcome.NoFocusedControl)]
    [InlineData((int)SelectionProbeOutcome.Protected)]
    [InlineData((int)SelectionProbeOutcome.Failed)]
    public async Task AnApplicationThatDoesNotExposeItsSelection_IsUnsupported_NotAnException(int outcome)
    {
        _desktop.Probe = new SelectionProbe((SelectionProbeOutcome)outcome, ControlProcessId: Notepad.ProcessId, FailureType: "COMException");

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Unsupported, result.Status);
        Assert.Null(result.Text);
        Assert.Equal(Notepad, result.App);
    }

    [Fact]
    public async Task APasswordBoxsText_IsNeverPassedOn_EvenIfTheProbeHadSome()
    {
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Protected, "hunter2", Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Unsupported, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task WhenNoWindowIsInTheForeground_NobodyIsAsked_AndTheAnswerSaysSo()
    {
        _desktop.App = null;

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.NoForegroundApp, result.Status);
        Assert.Null(result.App);
        Assert.Null(result.Text);
        Assert.Equal(0, _desktop.Reads);
    }

    [Fact]
    public async Task AnApplicationThatDoesNotAnswerInTime_IsUnsupported_AndTheCallerIsNotHeldUp()
    {
        using var release = new ManualResetEventSlim();
        _desktop.Block = release;
        var service = new SelectionService(_logger, _desktop, TimeSpan.FromMilliseconds(100));

        try
        {
            var result = await service.GetSelectionAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(SelectionStatus.Unsupported, result.Status);
            Assert.Equal(Notepad, result.App);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AReaderThatThrows_IsUnsupported_AndOnlyTheTypeOfTheFailureIsLogged()
    {
        _desktop.Throw = new InvalidOperationException("the secret text is in this message");

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Unsupported, result.Status);
        Assert.Equal(Notepad, result.App);
        Assert.Contains(_logger.Messages, message => message.Contains("InvalidOperationException"));
        Assert.DoesNotContain(_logger.Messages, message => message.Contains("secret"));
    }

    [Fact]
    public async Task AForegroundQueryThatThrows_IsNoForegroundApp()
    {
        _desktop.ForegroundThrows = true;

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.NoForegroundApp, result.Status);
    }

    [Fact]
    public async Task ACancelledRequest_Throws_AndAsksNobody()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.GetSelectionAsync(cancelled.Token));
        Assert.Equal(0, _desktop.Reads);
    }

    [Fact]
    public async Task AControlInAnotherProcessThanTheForegroundWindows_IsNotRead()
    {
        // Focus can sit in a window behind the one in front: what is selected there is not what the user is looking at.
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, "from behind", ControlProcessId: 999);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Unsupported, result.Status);
        Assert.Null(result.Text);
        Assert.Equal(Notepad, result.App);
    }

    [Fact]
    public async Task ForAModernApp_TheAppIsNamedRatherThanTheFrameHostThatOwnsItsWindow()
    {
        var frame = new ForegroundApp(700, "ApplicationFrameHost", @"C:\Windows\System32\ApplicationFrameHost.exe", 0x77);
        var calculator = new ForegroundApp(5150, "CalculatorApp", @"C:\Program Files\WindowsApps\Calculator\CalculatorApp.exe", 0x77);
        _desktop.App = frame;
        _desktop.Described[calculator.ProcessId] = calculator;
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, "42", calculator.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Selected, result.Status);
        Assert.Equal("42", result.Text);
        Assert.Equal(calculator, result.App);
    }

    [Fact]
    public async Task ForAModernApp_ThatCannotBeDescribed_TheFrameHostStaysTheApp()
    {
        var frame = new ForegroundApp(700, "ApplicationFrameHost", null, 0x77);
        _desktop.App = frame;
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, "42", ControlProcessId: 5150);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Selected, result.Status);
        Assert.Equal(frame, result.App);
    }

    [Fact]
    public async Task AControlWhoseProcessIsNotKnown_IsTakenToBeTheForegroundApplications()
    {
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, "text", ControlProcessId: 0);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Selected, result.Status);
    }

    [Fact]
    public async Task ASelectionLongerThanTheLimit_IsCut_AndSaysSo()
    {
        _desktop.Probe = new SelectionProbe(
            SelectionProbeOutcome.Selected, new string('a', SelectionService.MaxTextLength + 1), Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.Equal(SelectionStatus.Selected, result.Status);
        Assert.True(result.IsTruncated);
        Assert.Equal(SelectionService.MaxTextLength, result.Text!.Length);
    }

    [Fact]
    public async Task ASelectionOfExactlyTheLimit_IsNotCut()
    {
        _desktop.Probe = new SelectionProbe(
            SelectionProbeOutcome.Selected, new string('a', SelectionService.MaxTextLength), Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.False(result.IsTruncated);
        Assert.Equal(SelectionService.MaxTextLength, result.Text!.Length);
    }

    [Fact]
    public async Task TheCut_NeverLeavesHalfASurrogatePairAtTheEnd()
    {
        // A pair (an emoji) that straddles the limit: its first half would be the last character kept.
        var text = new string('a', SelectionService.MaxTextLength - 1) + "\U0001F600" + "b";
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, text, Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.True(result.IsTruncated);
        Assert.Equal(SelectionService.MaxTextLength - 1, result.Text!.Length);
        Assert.False(char.IsHighSurrogate(result.Text[^1]));
    }

    [Fact]
    public async Task TheSelectedText_IsNotInAnyLog_ToStringIncluded()
    {
        _desktop.Probe = new SelectionProbe(SelectionProbeOutcome.Selected, "my private sentence", Notepad.ProcessId);

        var result = await Service.GetSelectionAsync();

        Assert.NotEmpty(_logger.Messages);
        Assert.DoesNotContain(_logger.Messages, message => message.Contains("private"));
        Assert.DoesNotContain("private", result.ToString());
        Assert.Contains("notepad", string.Join('\n', _logger.Messages));
    }

    private sealed class FakeDesktop : ISelectionNativeMethods
    {
        private int _reads;

        public ForegroundApp? App { get; set; }

        public bool ForegroundThrows { get; set; }

        public SelectionProbe Probe { get; set; } = new(SelectionProbeOutcome.NoTextPattern);

        public Exception? Throw { get; set; }

        public ManualResetEventSlim? Block { get; set; }

        public Dictionary<int, ForegroundApp> Described { get; } = [];

        public int MaxLengthAsked { get; private set; }

        public int Reads => Volatile.Read(ref _reads);

        public ForegroundApp? GetForegroundApp() => ForegroundThrows ? throw new InvalidOperationException() : App;

        public ForegroundApp? DescribeProcess(int processId, nint window) => Described.GetValueOrDefault(processId);

        public SelectionProbe ReadSelection(int maxLength)
        {
            Interlocked.Increment(ref _reads);
            MaxLengthAsked = maxLength;
            Block?.Wait(TimeSpan.FromSeconds(30));
            return Throw is not null ? throw Throw : Probe;
        }
    }

    private sealed class TestLogger : ILogger<SelectionService>
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
