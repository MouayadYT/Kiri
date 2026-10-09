using Assistant.Core.Ipc;
using Assistant.ExplorerExtension.Forwarding;
using Assistant.ExplorerExtension.Selection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ExplorerExtension.Tests;

/// <summary>
/// What the entry point does when File Explorer starts it for a selected file (PROJECT_SPEC §4.4): reads the whole selection,
/// lets one process send it, and falls back to the file it was started for. Over real named pipes with names of their own and
/// a fake Explorer; the real app is never started.
/// </summary>
public sealed class AskHandlerTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _pipeName = LocalPipe.CreateUniqueName("Assistant.Tests.Ask");
    private readonly CancellationTokenSource _stop = new();
    private readonly RecordingHandler _handler = new();
    private readonly Task _serving;

    public AskHandlerTests()
    {
        var server = new InvocationServer(_pipeName, _handler, NullLogger<InvocationServer>.Instance);
        _serving = Task.Run(() => server.RunAsync(_stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _serving.WaitAsync(Wait);
        _stop.Dispose();
    }

    [Fact]
    public void TheWholeSelectionIsSentOnce_InTheOrderTheWindowListsIt_WhateverFileTheMenuWasOpenedOn()
    {
        string[] selection = [@"C:\Shots\b.png", @"C:\Shots\a.png", @"C:\Shots\notes.md", @"C:\Shots\c.zip", @"C:\Shots\sub folder"];
        var explorer = new FakeSelection(selection);

        var outcome = Handler(explorer, new FakeGate()).Run([@"C:\Shots\notes.md"]);

        Assert.Equal(ForwardResult.Forwarded, outcome.Result);
        Assert.True(outcome.FromSelection);
        Assert.Equal(5, outcome.Sent);
        Assert.Equal(selection, Assert.Single(_handler.Requests).Paths);
        Assert.Equal([@"C:\Shots\notes.md"], explorer.AskedAbout);
    }

    [Fact]
    public void OnlyTheFirstProcessOfASelectionSends_TheOthersEndAtOnce_AndADifferentSelectionIsItsOwn()
    {
        string[] selection = [@"C:\Shots\a.png", @"C:\Shots\b.png", @"C:\Shots\c.png"];
        var gate = new FakeGate();
        var explorer = new FakeSelection(selection);

        AskOutcome? second = null;
        AskOutcome? third = null;

        // Explorer starts one process for each of the three files, each reading the same selection: the others start while the
        // first still holds its claim.
        var firstProcess = new AskHandler(explorer, gate, Forwarder(), _ =>
        {
            second = Handler(explorer, gate).Run([@"C:\Shots\b.png"]);
            third = Handler(explorer, gate).Run([@"C:\Shots\c.png"]);
        }, TimeSpan.FromSeconds(4));
        var first = firstProcess.Run([@"C:\Shots\a.png"]);

        Assert.Equal(ForwardResult.Forwarded, first.Result);
        Assert.Null(second!.Result);
        Assert.Null(third!.Result);
        Assert.Equal(0, second.Sent + third.Sent);
        Assert.Single(_handler.Requests);

        // Another selection of the same folder is another choice of the menu.
        var other = Handler(new FakeSelection([@"C:\Shots\a.png", @"C:\Shots\d.png"]), gate).Run([@"C:\Shots\a.png"]);
        Assert.Equal(ForwardResult.Forwarded, other.Result);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public void TheClaimIsHeldForTheTimeExplorerTakesToStartTheOthers_ThenLetGo()
    {
        var gate = new FakeGate();
        var waited = new List<TimeSpan>();
        var handler = new AskHandler(
            new FakeSelection([@"C:\a.png", @"C:\b.png"]), gate, Forwarder(), waited.Add, TimeSpan.FromSeconds(4));

        handler.Run([@"C:\a.png"]);

        var held = Assert.Single(waited);
        Assert.InRange(held, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4));
        Assert.Equal(0, gate.Open);
    }

    [Fact]
    public void WithoutAWindowWithTheFileSelected_TheFileItselfIsSent_AndNothingIsClaimed()
    {
        var gate = new FakeGate();

        // No window answers; or the window's selection does not have the file (the user moved on).
        var none = Handler(new FakeSelection(null), gate).Run([@"C:\Docs\plan.docx"]);
        var elsewhere = Handler(new FakeSelection([@"C:\Other\x.png"]), gate).Run([@"C:\Docs\notes.md"]);

        Assert.Equal(ForwardResult.Forwarded, none.Result);
        Assert.False(none.FromSelection);
        Assert.Equal(ForwardResult.Forwarded, elsewhere.Result);
        Assert.Equal([[@"C:\Docs\plan.docx"], [@"C:\Docs\notes.md"]], _handler.Requests.Select(request => request.Paths.ToArray()));
        Assert.Equal(0, gate.Claims);
    }

    [Fact]
    public void ThePathsAreNormalizedBeforeAnythingIsAskedOrSent_AndASpellingTwiceIsOnce()
    {
        var explorer = new FakeSelection([@"C:\Shots\a.png", @"C:\Shots\b.png"]);

        Handler(explorer, new FakeGate()).Run(["\"c:/Shots/./a.png\"", @"\\?\C:\Shots\a.png"]);

        Assert.Equal([@"c:\Shots\a.png"], explorer.AskedAbout);
        Assert.Equal([@"C:\Shots\a.png", @"C:\Shots\b.png"], Assert.Single(_handler.Requests).Paths);
    }

    [Fact]
    public void ASelectionBeyondTheLimitIsCutToIt_AndRepeatedPathsAreOne()
    {
        var selection = Enumerable.Range(1, InvocationProtocol.MaxPaths + 40).Select(i => $@"C:\Shots\{i}.png").ToList();
        selection.Insert(3, @"C:\SHOTS\1.png");

        var outcome = Handler(new FakeSelection(selection), new FakeGate()).Run([@"C:\Shots\1.png"]);

        Assert.Equal(ForwardResult.Forwarded, outcome.Result);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(InvocationProtocol.MaxPaths, request.Paths.Count);
        Assert.Equal(request.Paths.Count, request.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ArgumentsThatAreNotPathsAreSentAsTheyAre_SoTheAppCanSaySo()
    {
        var outcome = Handler(new FakeSelection(null), new FakeGate()).Run(["not a path"]);

        Assert.Equal(ForwardResult.Forwarded, outcome.Result);
        Assert.Equal(["not a path"], Assert.Single(_handler.Requests).Paths);
    }

    [Fact]
    public void TheSelectionClaimIsNamedForTheSelectionAlone()
    {
        var name = NamedMutexSelectionGate.NameFor([@"C:\a.png", @"C:\b.png"]);
        Assert.Equal(name, NamedMutexSelectionGate.NameFor([@"c:\B.PNG", @"c:\A.png"]));
        Assert.NotEqual(name, NamedMutexSelectionGate.NameFor([@"C:\a.png", @"C:\c.png"]));
        Assert.DoesNotContain("png", name, StringComparison.OrdinalIgnoreCase);

        // Two real claims for one selection: the second is refused until the first is let go, and the name is free again after.
        var gate = new NamedMutexSelectionGate();
        string[] files = [$@"C:\Tests\{Guid.NewGuid():N}.png"];
        using (var first = gate.TryClaim(files))
        {
            Assert.NotNull(first);
            Assert.Null(gate.TryClaim(files));
        }

        using var again = gate.TryClaim(files);
        Assert.NotNull(again);
    }

    [Fact]
    public void ShortNamesAreWrittenInFull_OnlyWhereTheNameCouldBeShort()
    {
        var asked = new List<string>();
        string? Expand(string path)
        {
            asked.Add(path);
            return @"C:\Program Files\Assistant\a.png";
        }

        Assert.Equal(@"C:\Program Files\Assistant\a.png", ExplorerPaths.Prepare(@"C:\PROGRA~1\ASSIST~1\a.png", Expand));
        Assert.Equal(@"C:\Plain\a.png", ExplorerPaths.Prepare(@"C:\Plain\a.png", Expand));
        Assert.Single(asked);

        // A name Windows cannot expand (no such file) is kept; a path that is not one stays not one.
        Assert.Equal(@"C:\GONE~1\a.png", ExplorerPaths.Prepare(@"C:\GONE~1\a.png", _ => null));
        Assert.Null(ExplorerPaths.Prepare("a~1.png", Expand));
        Assert.Equal(@"C:\Users", ExplorerPaths.Prepare(@"C:\Users\"));
    }

    private AskHandler Handler(ISelectionSource selection, ISelectionGate gate) =>
        new(selection, gate, Forwarder(), _ => { }, TimeSpan.Zero);

    private InvocationForwarder Forwarder() => new(
        _pipeName,
        new NeverStarts(),
        new ForwarderOptions { StartLockName = $@"Local\Assistant.Tests.Ask.{Guid.NewGuid():N}", ConnectTimeout = TimeSpan.FromSeconds(5) });

    private sealed class NeverStarts : IAppStarter
    {
        public bool IsRunning() => true;

        public bool Start() => throw new InvalidOperationException("The app is running; it is never started.");
    }

    private sealed class FakeSelection(IReadOnlyList<string>? selection) : ISelectionSource
    {
        public List<string> AskedAbout { get; } = [];

        public IReadOnlyList<string>? TryRead(IReadOnlyList<string> clicked)
        {
            AskedAbout.AddRange(clicked);
            return selection is not null && clicked.All(file => selection.Contains(file, StringComparer.OrdinalIgnoreCase))
                ? selection
                : null;
        }
    }

    // Claims by the selection's paths, as the named mutex does; a claim is open until it is disposed.
    private sealed class FakeGate : ISelectionGate
    {
        private readonly HashSet<string> _held = [];

        public int Claims { get; private set; }

        public int Open => _held.Count;

        public IDisposable? TryClaim(IReadOnlyList<string> paths)
        {
            var key = NamedMutexSelectionGate.NameFor(paths);
            if (!_held.Add(key))
            {
                return null;
            }

            Claims++;
            return new Claim(() => _held.Remove(key));
        }

        private sealed class Claim(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }

    private sealed class RecordingHandler : IInvocationHandler
    {
        private readonly List<InvocationRequest> _requests = [];

        public IReadOnlyList<InvocationRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public InvocationReply Handle(InvocationRequest request)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return InvocationReply.Accepted;
        }
    }
}
