using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Assets;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The packaged assets (step 123): what is installed, what matches its manifest, and what is allowed to be loaded.</summary>
public sealed class PackagedAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-assets-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PackagedAssetPaths _paths;

    public PackagedAssetsTests() => _paths = new PackagedAssetPaths(Path.Combine(_root, "install"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CachePath => Path.Combine(_root, "cache", "asset-checks.json");

    private PackagedAssets Assets(IAssetCheckCache? cache = null, IAppEventBus? events = null) =>
        new(_paths, cache ?? new JsonAssetCheckCache(CachePath), events, NullLogger<PackagedAssets>.Instance);

    // Writes the files of a group and adds it to its kind's manifest, with the sizes and checksums of what was written.
    private AssetGroup Package(AssetKind kind, string id, params (string Name, string Content)[] files)
    {
        var folder = Path.Combine(_paths.DirectoryOf(kind), id);
        Directory.CreateDirectory(folder);
        var entries = new List<AssetFile>();
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(path, bytes);
            entries.Add(new AssetFile(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        var group = new AssetGroup(id, entries);
        var manifestPath = _paths.ManifestOf(kind);
        var groups = File.Exists(manifestPath) ? [.. AssetManifestReader.Parse(File.ReadAllBytes(manifestPath)).Groups.Where(g => g.Id != id)] : new List<AssetGroup>();
        groups.Add(group);
        File.WriteAllText(manifestPath, new AssetManifest(groups).ToJson());
        return group;
    }

    private string FileOf(AssetKind kind, string id, string name) =>
        Path.Combine(_paths.DirectoryOf(kind), id, name.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task AnAssetNoManifestLists_IsNotPackaged()
    {
        using var assets = Assets();

        Assert.Equal(AssetGroupStatus.NotPackaged, assets.Peek(AssetKind.Model, "chat-4b").Status);
        Assert.Equal(AssetGroupStatus.NotPackaged, (await assets.VerifyAsync(AssetKind.Voice, "piper")).Status);
        Assert.Empty(assets.GroupIds(AssetKind.Model));
    }

    [Fact]
    public async Task AnAssetThatIsListedAndWhole_IsUnverifiedUntilItIsChecked_ThenVerified()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"), ("mmproj.gguf", "projector bytes"));
        using var assets = Assets();

        var before = assets.Peek(AssetKind.Model, "chat-4b");
        var checkedNow = await assets.VerifyAsync(AssetKind.Model, "chat-4b");
        var after = assets.Peek(AssetKind.Model, "chat-4b");

        Assert.Equal(AssetGroupStatus.Unverified, before.Status);
        Assert.True(before.IsInstalled);
        Assert.False(before.IsVerified);
        Assert.Equal(AssetGroupStatus.Verified, checkedNow.Status);
        Assert.True(checkedNow.IsVerified);
        Assert.Equal(AssetGroupStatus.Verified, after.Status);
        Assert.Equal(["chat-4b"], assets.GroupIds(AssetKind.Model));
    }

    [Fact]
    public async Task AMissingFile_MakesTheAssetIncomplete_AndNoFileAtAllMissing()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"), ("mmproj.gguf", "projector bytes"));
        Package(AssetKind.Voice, "piper", ("voice.onnx", "voice"));
        File.Delete(FileOf(AssetKind.Model, "chat-4b", "mmproj.gguf"));
        File.Delete(FileOf(AssetKind.Voice, "piper", "voice.onnx"));
        using var assets = Assets();

        var incomplete = await assets.VerifyAsync(AssetKind.Model, "chat-4b");
        var missing = await assets.VerifyAsync(AssetKind.Voice, "piper");

        Assert.Equal(AssetGroupStatus.Incomplete, incomplete.Status);
        Assert.Equal(["mmproj.gguf"], incomplete.ProblemFiles);
        Assert.Equal(AssetGroupStatus.Missing, missing.Status);
        Assert.False(incomplete.IsInstalled);
        Assert.False(missing.IsInstalled);
    }

    [Fact]
    public async Task AFileOfTheWrongSize_IsDamaged_WithoutReadingIt()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        File.AppendAllText(FileOf(AssetKind.Model, "chat-4b", "model.gguf"), "x");
        using var assets = Assets();

        Assert.Equal(AssetGroupStatus.Damaged, assets.Peek(AssetKind.Model, "chat-4b").Status);
        var checkedNow = await assets.VerifyAsync(AssetKind.Model, "chat-4b");

        Assert.Equal(AssetGroupStatus.Damaged, checkedNow.Status);
        Assert.Equal(["model.gguf"], checkedNow.ProblemFiles);
    }

    [Fact]
    public async Task AFileOfTheRightSizeWithOtherBytes_IsDamaged_AndPeekCannotKnow()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        File.WriteAllText(FileOf(AssetKind.Model, "chat-4b", "model.gguf"), "other bytes");
        using var assets = Assets();

        var peeked = assets.Peek(AssetKind.Model, "chat-4b");
        var checkedNow = await assets.VerifyAsync(AssetKind.Model, "chat-4b");

        Assert.Equal(AssetGroupStatus.Unverified, peeked.Status);
        Assert.Equal(AssetGroupStatus.Damaged, checkedNow.Status);
        Assert.Equal(["model.gguf"], checkedNow.ProblemFiles);
    }

    [Fact]
    public async Task AFileChecked_IsNotReadAgainUnlessItChanged_AndReverifyReadsItRegardless()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        var path = FileOf(AssetKind.Model, "chat-4b", "model.gguf");
        using var assets = Assets();
        Assert.Equal(AssetGroupStatus.Verified, (await assets.VerifyAsync(AssetKind.Model, "chat-4b")).Status);

        // The same size and time stamp with other bytes, as a failing disk may leave: only a full check sees it.
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "other bytes");
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(AssetGroupStatus.Verified, (await assets.VerifyAsync(AssetKind.Model, "chat-4b", AssetCheckMode.Verify)).Status);
        Assert.Equal(AssetGroupStatus.Damaged, (await assets.VerifyAsync(AssetKind.Model, "chat-4b", AssetCheckMode.Reverify)).Status);

        // A damaged file is forgotten: the next ordinary check reads it and finds it damaged too.
        Assert.Equal(AssetGroupStatus.Damaged, (await assets.VerifyAsync(AssetKind.Model, "chat-4b", AssetCheckMode.Verify)).Status);
    }

    [Fact]
    public async Task AFileThatChanges_IsReadAgainBecauseItsTimeStampChanged()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        var path = FileOf(AssetKind.Model, "chat-4b", "model.gguf");
        using var assets = Assets();
        await assets.VerifyAsync(AssetKind.Model, "chat-4b");

        File.WriteAllText(path, "other bytes");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));

        Assert.Equal(AssetGroupStatus.Unverified, assets.Peek(AssetKind.Model, "chat-4b").Status);
        Assert.Equal(AssetGroupStatus.Damaged, (await assets.VerifyAsync(AssetKind.Model, "chat-4b")).Status);
    }

    [Fact]
    public async Task APeekAfterACheck_SaysWhatTheCheckFound_UntilAFileChanges_EvenWithoutACache()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        Package(AssetKind.Model, "chat-9b", ("model.gguf", "bigger bytes"));
        var damaged = FileOf(AssetKind.Model, "chat-9b", "model.gguf");
        File.WriteAllText(damaged, "BIGGER BYTES");
        using var assets = Assets(NoAssetCheckCache.Instance);

        await assets.VerifyAsync(AssetKind.Model, "chat-4b");
        await assets.VerifyAsync(AssetKind.Model, "chat-9b");

        // Nothing is saved between runs here, yet what was found is still what a peek reports.
        Assert.Equal(AssetGroupStatus.Verified, assets.Peek(AssetKind.Model, "chat-4b").Status);
        Assert.Equal(AssetGroupStatus.Damaged, assets.Peek(AssetKind.Model, "chat-9b").Status);
        Assert.Equal(["model.gguf"], assets.Peek(AssetKind.Model, "chat-9b").ProblemFiles);

        // A file that was written to since is not what was checked: the answer is "not checked yet" again.
        File.WriteAllText(damaged, "bigger bytes");
        File.SetLastWriteTimeUtc(damaged, DateTime.UtcNow.AddMinutes(3));

        Assert.Equal(AssetGroupStatus.Unverified, assets.Peek(AssetKind.Model, "chat-9b").Status);
        Assert.Equal(AssetGroupStatus.Verified, (await assets.VerifyAsync(AssetKind.Model, "chat-9b")).Status);
        Assert.Equal(AssetGroupStatus.Verified, assets.Peek(AssetKind.Model, "chat-9b").Status);
    }

    [Fact]
    public async Task TheCache_IsKeptBetweenRuns_SoAFreshServiceTrustsWhatWasChecked()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        using (var first = Assets())
        {
            await first.VerifyAsync(AssetKind.Model, "chat-4b");
        }

        using var second = Assets();

        Assert.True(File.Exists(CachePath));
        Assert.Equal(AssetGroupStatus.Verified, second.Peek(AssetKind.Model, "chat-4b").Status);
    }

    [Fact]
    public async Task ADamagedCacheFile_IsOnlyALostCache()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, "{ this is not json");
        using var assets = Assets();

        Assert.Equal(AssetGroupStatus.Unverified, assets.Peek(AssetKind.Model, "chat-4b").Status);
        Assert.Equal(AssetGroupStatus.Verified, (await assets.VerifyAsync(AssetKind.Model, "chat-4b")).Status);
        Assert.Contains("chat-4b", File.ReadAllText(CachePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestThatCannotBeRead_MakesEveryAssetOfItsKindDamaged_AndTheOtherKindIsUnaffected()
    {
        Package(AssetKind.Voice, "piper", ("voice.onnx", "voice"));
        Directory.CreateDirectory(_paths.ModelsDirectory);
        File.WriteAllText(_paths.ManifestOf(AssetKind.Model), "{ broken");
        using var assets = Assets();

        var damaged = assets.Peek(AssetKind.Model, "chat-4b");
        var verified = await assets.VerifyAsync(AssetKind.Model, "chat-4b");

        Assert.Equal(AssetGroupStatus.Damaged, damaged.Status);
        Assert.Equal(["manifest.json"], damaged.ProblemFiles);
        Assert.Equal(AssetGroupStatus.Damaged, verified.Status);
        Assert.Equal(AssetGroupStatus.Unverified, assets.Peek(AssetKind.Voice, "piper").Status);
        Assert.Empty(assets.GroupIds(AssetKind.Model));
    }

    [Fact]
    public async Task AManifestEditedWhileTheAppRuns_IsReadAgain()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        using var assets = Assets();
        Assert.Equal(["chat-4b"], assets.GroupIds(AssetKind.Model));

        Package(AssetKind.Model, "chat-9b", ("model.gguf", "bigger model bytes"));
        File.SetLastWriteTimeUtc(_paths.ManifestOf(AssetKind.Model), DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(["chat-4b", "chat-9b"], assets.GroupIds(AssetKind.Model));
        Assert.Equal(AssetGroupStatus.Verified, (await assets.VerifyAsync(AssetKind.Model, "chat-9b")).Status);
    }

    [Fact]
    public async Task ACheckThatIsAlreadyRunning_IsJoinedNotRepeated_AndTheStateIsAnnounced()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var listener = new Listener();
        using var subscription = bus.Subscribe<Listener, AssetStateChanged>(listener, static (self, changed, _) => self.OnChangedAsync(changed));
        using var assets = Assets(events: bus);

        var first = assets.VerifyAsync(AssetKind.Model, "chat-4b");
        await listener.CheckingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = assets.VerifyAsync(AssetKind.Model, "chat-4b");

        Assert.Equal(AssetGroupStatus.Checking, assets.Peek(AssetKind.Model, "chat-4b").Status);
        listener.Release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, state => Assert.Equal(AssetGroupStatus.Verified, state.Status));
        Assert.Equal([AssetGroupStatus.Checking, AssetGroupStatus.Verified], listener.Seen);
        Assert.Equal(AssetGroupStatus.Verified, assets.Peek(AssetKind.Model, "chat-4b").Status);
    }

    [Fact]
    public async Task OneCallersCancellation_OnlyEndsItsOwnWait()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var listener = new Listener();
        using var subscription = bus.Subscribe<Listener, AssetStateChanged>(listener, static (self, changed, _) => self.OnChangedAsync(changed));
        using var assets = Assets(events: bus);
        using var cancel = new CancellationTokenSource();

        var impatient = assets.VerifyAsync(AssetKind.Model, "chat-4b", cancellationToken: cancel.Token);
        await listener.CheckingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var patient = assets.VerifyAsync(AssetKind.Model, "chat-4b");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        listener.Release.SetResult();
        Assert.Equal(AssetGroupStatus.Verified, (await patient).Status);
    }

    [Fact]
    public async Task VerifyAll_ChecksEveryAssetOfEveryKind()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        Package(AssetKind.Model, "chat-9b", ("model.gguf", "bigger model bytes"));
        Package(AssetKind.Voice, "piper", ("voice.onnx", "voice"));
        File.WriteAllText(FileOf(AssetKind.Model, "chat-9b", "model.gguf"), "BIGGER MODEL BYTES");
        using var assets = Assets();

        await assets.VerifyAllAsync();

        Assert.Equal(AssetGroupStatus.Verified, assets.Peek(AssetKind.Model, "chat-4b").Status);
        Assert.Equal(AssetGroupStatus.Verified, assets.Peek(AssetKind.Voice, "piper").Status);
        Assert.Equal(AssetGroupStatus.Damaged, (await assets.VerifyAsync(AssetKind.Model, "chat-9b")).Status);
    }

    [Fact]
    public async Task APackagedModelThatMatches_MayBeLoaded_OneThatDoesNotMayNot()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"), ("mmproj.gguf", "projector bytes"));
        using var assets = Assets();
        var files = new ModelFiles(FileOf(AssetKind.Model, "chat-4b", "model.gguf")) { ProjectorPath = FileOf(AssetKind.Model, "chat-4b", "mmproj.gguf") };

        await assets.EnsureModelUsableAsync(files);

        File.WriteAllText(FileOf(AssetKind.Model, "chat-4b", "mmproj.gguf"), "PROJECTOR BYTES");
        File.SetLastWriteTimeUtc(FileOf(AssetKind.Model, "chat-4b", "mmproj.gguf"), DateTime.UtcNow.AddMinutes(2));
        var failure = await Assert.ThrowsAsync<AssetIntegrityException>(() => assets.EnsureModelUsableAsync(files));

        Assert.Equal(AssetKind.Model, failure.Kind);
        Assert.Equal("chat-4b", failure.State.GroupId);
        Assert.Equal(AssetGroupStatus.Damaged, failure.State.Status);
        Assert.Equal(["mmproj.gguf"], failure.State.ProblemFiles);
    }

    [Fact]
    public async Task AFileTheManifestListsButIsGone_StopsTheModelEvenIfThePathsLoadedDoNotNameIt()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"), ("mmproj.gguf", "projector bytes"));
        File.Delete(FileOf(AssetKind.Model, "chat-4b", "mmproj.gguf"));
        using var assets = Assets();

        var textOnly = new ModelFiles(FileOf(AssetKind.Model, "chat-4b", "model.gguf"));
        var failure = await Assert.ThrowsAsync<AssetIntegrityException>(() => assets.EnsureModelUsableAsync(textOnly));

        Assert.Equal(AssetGroupStatus.Incomplete, failure.State.Status);
    }

    [Fact]
    public async Task AModelNoManifestLists_OrOutsideThePackagedFolder_IsLeftAlone()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        var elsewhere = Path.Combine(_root, "elsewhere", "model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "not packaged");
        var unlisted = FileOf(AssetKind.Model, "my-own-model", "model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(unlisted)!);
        File.WriteAllText(unlisted, "also not packaged");
        using var assets = Assets();

        await assets.EnsureModelUsableAsync(new ModelFiles(elsewhere));
        await assets.EnsureModelUsableAsync(new ModelFiles(unlisted));
        await assets.EnsureModelUsableAsync(new ModelFiles(Path.Combine(_paths.ModelsDirectory, "loose.gguf")));
    }

    [Fact]
    public async Task APackagedModelWhoseManifestCannotBeRead_IsNotLoaded()
    {
        Directory.CreateDirectory(Path.Combine(_paths.ModelsDirectory, "chat-4b"));
        File.WriteAllText(Path.Combine(_paths.ModelsDirectory, "chat-4b", "model.gguf"), "bytes");
        File.WriteAllText(_paths.ManifestOf(AssetKind.Model), "{ broken");
        using var assets = Assets();

        var failure = await Assert.ThrowsAsync<AssetIntegrityException>(
            () => assets.EnsureModelUsableAsync(new ModelFiles(Path.Combine(_paths.ModelsDirectory, "chat-4b", "model.gguf"))));

        Assert.Equal(AssetGroupStatus.Damaged, failure.State.Status);
    }

    [Fact]
    public async Task AFileLockedAgainstReading_CannotBeChecked_AndIsNotCalledDamaged()
    {
        Package(AssetKind.Model, "chat-4b", ("model.gguf", "model bytes"));
        using var assets = Assets();
        using var locked = new FileStream(FileOf(AssetKind.Model, "chat-4b", "model.gguf"), FileMode.Open, FileAccess.Read, FileShare.None);

        var state = await assets.VerifyAsync(AssetKind.Model, "chat-4b");

        Assert.Equal(AssetGroupStatus.Unreadable, state.Status);
        Assert.Equal(["model.gguf"], state.ProblemFiles);
    }

    [Fact]
    public void TheWords_NameFilesAsTheManifestDoes_AndNeverAPath()
    {
        var damaged = new AssetGroupState("chat-4b", AssetGroupStatus.Damaged, ["model.gguf", "mmproj.gguf", "a.bin", "b.bin"]);

        var text = AssetStatusText.Describe(damaged, "model");

        Assert.Contains("(model.gguf, mmproj.gguf, a.bin and 1 more)", text, StringComparison.Ordinal);
        Assert.Contains("do not match", text, StringComparison.Ordinal);
        Assert.Equal("Installed, and its files match what was packaged.", AssetStatusText.Describe(AssetGroupState.Of("x", AssetGroupStatus.Verified), "model"));
        Assert.Equal("Not installed with this copy of the Assistant.", AssetStatusText.Describe(AssetGroupState.Of("x", AssetGroupStatus.NotPackaged), "voice"));
        Assert.Equal("Checking its files…", AssetStatusText.Describe(AssetGroupState.Of("x", AssetGroupStatus.Checking), "voice"));
    }

    // Holds the first Checking event until the test lets it go, so that a second caller arrives while the check is running.
    private sealed class Listener
    {
        private readonly List<AssetGroupStatus> _seen = [];

        public TaskCompletionSource CheckingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AssetGroupStatus> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public async Task OnChangedAsync(AssetStateChanged changed)
        {
            lock (_seen)
            {
                _seen.Add(changed.State.Status);
            }

            if (changed.State.Status == AssetGroupStatus.Checking)
            {
                CheckingStarted.TrySetResult();
                await Release.Task;
            }
        }
    }
}
