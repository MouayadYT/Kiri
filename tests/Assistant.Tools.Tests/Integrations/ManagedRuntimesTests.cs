using System.Runtime.InteropServices;
using System.Text.Json;
using Assistant.Core.Storage;
using Assistant.Tools.Integrations;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 108: the managed dependency layer: a runtime is set up once, reused, and never half there.</summary>
public sealed class ManagedRuntimesTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakePackageDownloader _downloader = new();
    private readonly IntegrationLayout _layout;
    private readonly byte[] _node;
    private readonly RuntimeRelease _release;

    public ManagedRuntimesTests()
    {
        var paths = new AppPaths(_folder.Path);
        paths.EnsureDirectoriesExist();
        _layout = new IntegrationLayout(paths);
        _node = TestArchives.Zip(
            ("node-v1.2.3-win-x64/node.exe", "MZ node"), ("node-v1.2.3-win-x64/node_modules/npm/bin/npm-cli.js", "// npm"), ("node-v1.2.3-win-x64/LICENSE", "x"));
        _release = new RuntimeRelease(
            RuntimeKind.NodeJs, "1.2.3", "Node.js 1", new Uri("https://nodejs.org/dist/v1.2.3/node-v1.2.3-win-x64.zip"), TestArchives.Sha256(_node), _node.Length,
            ArchiveKind.Zip, "node-v1.2.3-win-x64", "node.exe");
        _downloader.Files[_release.DownloadUrl.AbsoluteUri] = _node;
    }

    public void Dispose() => _folder.Dispose();

    private ManagedRuntimes Runtimes(Func<RuntimeKind, RuntimeRelease?>? catalog = null) =>
        new(_layout, _downloader, new ManualTimeProvider(Reviewable.Now), NullLogger<ManagedRuntimes>.Instance, catalog ?? (kind => kind == RuntimeKind.NodeJs ? _release : null));

    [Fact]
    public async Task ARuntimeThatIsMissingIsDownloadedCheckedUnpackedAndMarked()
    {
        var runtimes = Runtimes();
        Assert.Null(runtimes.Find(RuntimeKind.NodeJs));

        var runtime = await runtimes.EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);

        Assert.Equal("1.2.3", runtime.Version);
        Assert.Equal(Path.Combine(_layout.RuntimesRoot, "node", "1.2.3", "node.exe"), runtime.ExecutablePath);
        Assert.True(File.Exists(runtime.ExecutablePath));
        Assert.True(File.Exists(Path.Combine(runtime.Directory, "node_modules", "npm", "bin", "npm-cli.js")));
        Assert.True(File.Exists(Path.Combine(runtime.Directory, "runtime.json")));
        Assert.NotNull(runtimes.Find(RuntimeKind.NodeJs));
        Assert.Single(_downloader.Downloads);
    }

    [Fact]
    public async Task ARuntimeThatIsThereIsReusedAndNothingIsDownloadedAgain()
    {
        var runtimes = Runtimes();
        await runtimes.EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);

        var again = await Runtimes().EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);

        Assert.Equal("1.2.3", again.Version);
        Assert.Single(_downloader.Downloads);
    }

    [Fact]
    public async Task NothingIsLeftBesideTheRuntimeAfterwards()
    {
        await Runtimes().EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);

        var staging = Path.Combine(_layout.RuntimesRoot, ".staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public async Task ADownloadWithTheWrongHashSetsNothingUp()
    {
        _downloader.Files[_release.DownloadUrl.AbsoluteUri] = [.. _node, 0];

        var failure = await Assert.ThrowsAsync<InstallException>(() => Runtimes().EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None));

        Assert.Equal(InstallFailure.HashMismatch, failure.Failure);
        Assert.False(Directory.Exists(Path.Combine(_layout.RuntimesRoot, "node", "1.2.3")));
        var staging = Path.Combine(_layout.RuntimesRoot, ".staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public async Task AnArchiveWithoutTheProgramIsNotARuntime()
    {
        var bad = TestArchives.Zip(("node-v1.2.3-win-x64/README.md", "no program here"));
        var release = _release with { Hash = TestArchives.Sha256(bad), SizeBytes = bad.Length };
        _downloader.Files[release.DownloadUrl.AbsoluteUri] = bad;

        var failure = await Assert.ThrowsAsync<InstallException>(() => Runtimes(_ => release).EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None));

        Assert.Equal(InstallFailure.RuntimeUnavailable, failure.Failure);
        Assert.False(Directory.Exists(Path.Combine(_layout.RuntimesRoot, "node", "1.2.3")));
    }

    [Fact]
    public async Task AFolderWithoutTheMarkerIsNotAReadyRuntimeAndIsReplaced()
    {
        var folder = Path.Combine(_layout.RuntimesRoot, "node", "1.2.3");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "node.exe"), "half set up");
        var runtimes = Runtimes();
        Assert.Null(runtimes.Find(RuntimeKind.NodeJs));

        var runtime = await runtimes.EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);

        Assert.Equal("MZ node", await File.ReadAllTextAsync(runtime.ExecutablePath));
        Assert.Single(_downloader.Downloads);
    }

    [Fact]
    public async Task AMarkerForAnotherReleaseOrHashIsNotTrusted()
    {
        var runtimes = Runtimes();
        var runtime = await runtimes.EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);
        var marker = Path.Combine(runtime.Directory, "runtime.json");
        var edited = (await File.ReadAllTextAsync(marker)).Replace(_release.Hash.Hex, new string('0', 64), StringComparison.Ordinal);
        await File.WriteAllTextAsync(marker, edited);

        Assert.Null(runtimes.Find(RuntimeKind.NodeJs));
        await Runtimes().EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);
        Assert.Equal(2, _downloader.Downloads.Count);
    }

    [Fact]
    public async Task ACorruptMarkerIsNotTrusted()
    {
        var runtime = await Runtimes().EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(runtime.Directory, "runtime.json"), "not json {{{");

        Assert.Null(Runtimes().Find(RuntimeKind.NodeJs));
    }

    [Fact]
    public async Task TheProgramMissingAfterwardsMakesTheRuntimeNotReady()
    {
        var runtime = await Runtimes().EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);
        File.Delete(runtime.ExecutablePath);

        Assert.Null(Runtimes().Find(RuntimeKind.NodeJs));
    }

    [Fact]
    public async Task TwoRequestsAtOnceDownloadTheRuntimeOnce()
    {
        var gate = new TaskCompletionSource();
        _downloader.Before = _ => gate.Task;
        var runtimes = Runtimes();

        var first = runtimes.EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);
        var second = runtimes.EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);
        await Task.Delay(100);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(_downloader.Downloads);
    }

    [Fact]
    public async Task ACancelledSetupLeavesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        _downloader.Before = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runtimes().EnsureAsync(RuntimeKind.NodeJs, null, cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(_layout.RuntimesRoot, "node", "1.2.3")));
        var staging = Path.Combine(_layout.RuntimesRoot, ".staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public async Task APcWithNoRuntimeOfThatKindIsToldSoAndNothingIsDownloaded()
    {
        var failure = await Assert.ThrowsAsync<InstallException>(() => Runtimes().EnsureAsync(RuntimeKind.Python, null, CancellationToken.None));

        Assert.Equal(InstallFailure.RuntimeUnavailable, failure.Failure);
        Assert.Empty(_downloader.Downloads);
    }

    [Fact]
    public async Task ProgressSaysWhatIsHappeningInWordsWithTheSize()
    {
        var steps = new List<InstallProgress>();

        await Runtimes().EnsureAsync(RuntimeKind.NodeJs, new Progress<InstallProgress>(steps.Add), CancellationToken.None);
        await Task.Delay(100);

        Assert.Contains(steps, step => step.Step == InstallStep.DownloadingRuntime && step.Message.Contains("Node.js 1", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Step == InstallStep.UnpackingRuntime);
        Assert.All(steps, step => Assert.DoesNotContain("http", step.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuntimesNoIntegrationUsesAreRemovedAndOnesInUseAreKept()
    {
        var runtimes = Runtimes();
        await runtimes.EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);
        var old = Path.Combine(_layout.RuntimesRoot, "node", "0.9.0");
        Directory.CreateDirectory(old);
        await File.WriteAllTextAsync(Path.Combine(old, "node.exe"), "x");

        var removed = runtimes.RemoveUnused(new HashSet<(RuntimeKind, string)> { (RuntimeKind.NodeJs, "1.2.3") });

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.NotNull(runtimes.Find(RuntimeKind.NodeJs));

        Assert.Equal(1, runtimes.RemoveUnused(new HashSet<(RuntimeKind, string)>()));
        Assert.Null(runtimes.Find(RuntimeKind.NodeJs));
    }

    [Fact]
    public void ThePinnedRuntimesAreHttpsDownloadsFromListedHostsWithValidHashes()
    {
        foreach (var kind in new[] { RuntimeKind.NodeJs, RuntimeKind.Python })
        {
            var release = RuntimeCatalog.For(kind, Architecture.X64);

            Assert.NotNull(release);
            Assert.True(PackageDownloadPolicy.Standard.IsAllowed(release!.DownloadUrl), release.DownloadUrl.ToString());
            Assert.Equal("sha256", release.Hash.Algorithm);
            Assert.True(release.Hash.IsValid);
            Assert.True(release.SizeBytes > 1_000_000);
            Assert.True(IntegrationRules.IsValidVersion(release.Version));
        }
    }

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X86)]
    public void ThereIsNoRuntimeForAProcessorTheAssistantHasNoBuildFor(Architecture architecture)
    {
        Assert.Null(RuntimeCatalog.For(RuntimeKind.NodeJs, architecture));
        Assert.Null(RuntimeCatalog.For(RuntimeKind.Python, architecture));
    }

    [Fact]
    public async Task TheMarkerHoldsTheKindVersionAndHashAndNothingElse()
    {
        var runtime = await Runtimes().EnsureAsync(RuntimeKind.NodeJs, null, CancellationToken.None);

        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runtime.Directory, "runtime.json")));
        var names = marker.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(["Hash", "InstalledAt", "Kind", "SchemaVersion", "Version"], names);
    }
}
