using Assistant.Core.Assets;
using Assistant.Core.Storage;
using Assistant.Core.Voice;
using Assistant.Voice;
using Xunit;

namespace Assistant.Voice.Tests;

public sealed class VoiceModelFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-voice-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public VoiceModelFoldersTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void PutUserFiles(string id)
    {
        var folder = Path.Combine(_paths.VoicesDirectory, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "tokens.txt"), "x");
    }

    [Fact]
    public void TheUsersOwnFolderIsUsedFirstAndNotChecked()
    {
        PutUserFiles("piper");
        var packaged = new FakePackagedAssets(_root, AssetGroupStatus.Damaged);

        var folder = new VoiceModelFolders(_paths, packaged).Resolve("piper");

        Assert.Equal(Path.Combine(_paths.VoicesDirectory, "piper"), folder);
        Assert.Equal(0, packaged.Verified);
    }

    [Fact]
    public void AVoiceThatIsInNeitherPlaceIsNotInstalled()
    {
        var exception = Assert.Throws<VoiceEngineException>(() => new VoiceModelFolders(_paths).Resolve("piper"));

        Assert.Equal(VoiceEngineFailure.NotInstalled, exception.Failure);
        Assert.DoesNotContain(_root, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyUserFolderCountsAsNotThere()
    {
        Directory.CreateDirectory(Path.Combine(_paths.VoicesDirectory, "piper"));

        Assert.False(new VoiceModelFolders(_paths).HasUserFiles("piper"));
        Assert.Throws<VoiceEngineException>(() => new VoiceModelFolders(_paths).Resolve("piper"));
    }

    [Fact]
    public void APackagedVoiceIsCheckedBeforeItIsUsed()
    {
        var packaged = new FakePackagedAssets(_root, AssetGroupStatus.Unverified, verifiedStatus: AssetGroupStatus.Verified);

        var folder = new VoiceModelFolders(_paths, packaged).Resolve("piper");

        Assert.Equal(Path.Combine(packaged.Paths.VoicesDirectory, "piper"), folder);
        Assert.Equal(1, packaged.Verified);
    }

    [Theory]
    [InlineData(AssetGroupStatus.Damaged)]
    [InlineData(AssetGroupStatus.Incomplete)]
    [InlineData(AssetGroupStatus.Unreadable)]
    public void APackagedVoiceThatFailsItsCheckIsNotUsed(AssetGroupStatus status)
    {
        var packaged = new FakePackagedAssets(_root, AssetGroupStatus.Unverified, verifiedStatus: status);

        var exception = Assert.Throws<VoiceEngineException>(() => new VoiceModelFolders(_paths, packaged).Resolve("piper"));

        Assert.Equal(VoiceEngineFailure.FilesFailedCheck, exception.Failure);
    }

    [Theory]
    [InlineData(AssetGroupStatus.NotPackaged)]
    [InlineData(AssetGroupStatus.Missing)]
    public void AVoiceTheManifestDoesNotHaveIsNotInstalled(AssetGroupStatus status)
    {
        var packaged = new FakePackagedAssets(_root, status);

        var exception = Assert.Throws<VoiceEngineException>(() => new VoiceModelFolders(_paths, packaged).Resolve("piper"));

        Assert.Equal(VoiceEngineFailure.NotInstalled, exception.Failure);
    }

    [Fact]
    public void APlainPathIsLeftAlone() => Assert.Equal(@"C:\Users\plain\voices", NativePath.Prepare(@"C:\Users\plain\voices"));

    [Fact]
    public void APathTheRuntimeCannotReadIsRefusedInWordsForTheUser()
    {
        // Characters no Windows code page has in common with the runtime's ANSI wrapper, and no short name for a folder that does not exist.
        var exception = Assert.Throws<VoiceEngineException>(() => NativePath.Prepare("C:\\no\\such\\\u0e2a\u0e27\u0e31\u0e2a\u0e14\u0e35\\folder"));

        Assert.Equal(VoiceEngineFailure.LoadFailed, exception.Failure);
        Assert.DoesNotContain("no\\such", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakePackagedAssets(string root, AssetGroupStatus peek, AssetGroupStatus? verifiedStatus = null) : IPackagedAssets
    {
        public PackagedAssetPaths Paths { get; } = new(Path.Combine(root, "install"));

        public int Verified { get; private set; }

        public IReadOnlyList<string> GroupIds(AssetKind kind) => [];

        public AssetGroupState Peek(AssetKind kind, string groupId) => AssetGroupState.Of(groupId, peek);

        public Task<AssetGroupState> VerifyAsync(AssetKind kind, string groupId, AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default)
        {
            Verified++;
            return Task.FromResult(AssetGroupState.Of(groupId, verifiedStatus ?? peek));
        }

        public Task VerifyAllAsync(AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureModelUsableAsync(Assistant.Core.Contracts.ModelFiles files, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed class WakeWordLevelTests
{
    private static short[] Block(short amplitude) => Enumerable.Repeat(amplitude, 320).ToArray();

    private static SherpaWakeWordService.LevelHistory History(params (short Amplitude, int Blocks)[] runs)
    {
        var history = new SherpaWakeWordService.LevelHistory(320);
        foreach (var (amplitude, blocks) in runs)
        {
            for (var i = 0; i < blocks; i++)
            {
                history.Add(Block(amplitude));
            }
        }

        return history;
    }

    [Fact]
    public void AWordAfterAPauseBeginsARequest() => Assert.True(History((50, 60), (8000, 25), (50, 15)).WordBeginsAfterAPause(2.0));

    [Fact]
    public void AWordInTheMiddleOfSpeechDoesNot() => Assert.False(History((7000, 60), (8000, 25), (6000, 15)).WordBeginsAfterAPause(2.0));

    [Fact]
    public void WithLittleHistoryTheWordIsTheFirstThingSaid() => Assert.True(History((8000, 10)).WordBeginsAfterAPause(2.0));

    // "Hey Kiri": a short burst of speech after a pause. The "hey" is as loud as the word, so the word is not louder than what is before it, but the burst is short.
    private const double Gate = 0.02;

    [Fact]
    public void AGreetingAndTheWordAreOneShortRun() =>
        Assert.Equal(TimeSpan.FromMilliseconds(660), History((50, 60), (8000, 12), (500, 3), (8000, 18), (50, 5)).SpeechRun(Gate));

    [Fact]
    public void ALongStretchOfSpeechIsALongRun() => Assert.Equal(TimeSpan.FromSeconds(2), History((50, 30), (7000, 100), (50, 5)).SpeechRun(Gate));

    [Fact]
    public void AQuarterOfASecondOfQuietEndsTheRun() =>
        Assert.Equal(TimeSpan.FromMilliseconds(400), History((8000, 40), (50, 13), (8000, 20), (50, 4)).SpeechRun(Gate));

    [Fact]
    public void ADipOfLessThanThatBetweenTwoWordsIsStillSpeech() =>
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), History((8000, 25), (50, 5), (8000, 20)).SpeechRun(Gate));

    [Fact]
    public void SpeechThatBeganBeforeTheHistoryIsAsLongAsTheHistory() => Assert.Equal(TimeSpan.FromSeconds(3), History((7000, 200)).SpeechRun(Gate));

    [Fact]
    public void WithNoSpeechThereIsNoRun() => Assert.Equal(TimeSpan.Zero, History((50, 100)).SpeechRun(Gate));

    [Fact]
    public void ByDefaultTheListenerHearsKiriAndHeyKiriAndAllowsAShortLeadIn()
    {
        var tuning = new WakeWordTuning();

        Assert.Contains(tuning.Phrases, phrase => phrase.EndsWith("@KIRI", StringComparison.Ordinal));
        Assert.Contains(tuning.Phrases, phrase => phrase.EndsWith("@HEYKIRI", StringComparison.Ordinal));
        Assert.True(tuning.RequireQuietBefore);
        Assert.Equal(TimeSpan.FromSeconds(1.5), tuning.ShortLeadIn);
    }

    [Fact]
    public void TheNoiseFloorFallsQuicklyAndRisesSlowly()
    {
        var floor = new NoiseFloor();
        for (var i = 0; i < 100; i++)
        {
            floor.Update(0.001);
        }

        var quiet = floor.Update(0.001);
        for (var i = 0; i < 20; i++)
        {
            floor.Update(0.2);
        }

        var afterSpeech = floor.Update(0.2);

        Assert.True(quiet < 0.002);
        Assert.True(afterSpeech < 0.05, "Speech must not be taken for the room's noise.");
    }
}

public sealed class EndOfSpeechDetectorTests
{
    private const double Block = 0.1;

    // Runs the detector over a sequence of (level, seconds, whether words are being written), with the last word at the end of each speaking run.
    private static double? EndedAt(TimeSpan pause, params (double Level, double Seconds, bool Words)[] runs)
    {
        var detector = new EndOfSpeechDetector(pause);
        double time = 0;
        double? lastWord = null;
        foreach (var (level, seconds, words) in runs)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += Block)
            {
                time += Block;
                if (words)
                {
                    lastWord = time;
                }

                if (detector.Update(level, Block, time, lastWord))
                {
                    return time;
                }
            }
        }

        return null;
    }

    [Fact]
    public void AQuietPauseAfterWordsEndsTheRequestAboutAPauseLater()
    {
        var ended = EndedAt(TimeSpan.FromSeconds(1), (0.001, 1.0, false), (0.1, 2.0, true), (0.001, 3.0, false));

        Assert.NotNull(ended);
        Assert.InRange(ended!.Value, 3.9, 4.2);
    }

    [Fact]
    public void ContinuousSpeechNeverEndsTheRequest() => Assert.Null(EndedAt(TimeSpan.FromSeconds(1), (0.1, 10.0, true)));

    [Fact]
    public void AShortBreathBetweenSentencesDoesNot() => Assert.Null(EndedAt(TimeSpan.FromSeconds(1), (0.1, 2.0, true), (0.001, 0.5, false), (0.1, 2.0, true)));

    [Fact]
    public void NothingSaidYetNeverEndsIt() => Assert.Null(EndedAt(TimeSpan.FromSeconds(1), (0.001, 20.0, false)));

    [Fact]
    public void ANoisyRoomEndsTheRequestOnTheWordsAloneALittleLater()
    {
        // The audio is never quiet (a fan, a street), but no word has come for as long as a pause and then some.
        var ended = EndedAt(TimeSpan.FromSeconds(1), (0.1, 2.0, true), (0.05, 6.0, false));

        Assert.NotNull(ended);
        Assert.InRange(ended!.Value, 4.4, 5.0);
    }
}
