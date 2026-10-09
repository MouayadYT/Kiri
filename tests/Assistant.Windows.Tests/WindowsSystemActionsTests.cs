using System.ComponentModel;
using System.Diagnostics;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Windows.Audio;
using Assistant.Windows.Shell;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>What the quick actions do to Windows: a closed set of operations, with the volume and the folders checked for real.</summary>
public sealed class WindowsSystemActionsTests
{
    private static WindowsSystemActions Actions(
        FakeVolume? volume = null, List<ProcessStartInfo>? started = null, Func<bool>? lockWorkstation = null,
        Func<SystemFolder, string?>? folders = null) =>
        new(
            info =>
            {
                started?.Add(info);
                return true;
            },
            volume ?? new FakeVolume(), lockWorkstation ?? (() => true), folders ?? (_ => null));

    [Fact]
    public void TheSettingsAppIsOpenedByItsFixedAddressThroughTheShell()
    {
        var started = new List<ProcessStartInfo>();

        Assert.True(Actions(started: started).OpenWindowsSettings());

        var info = Assert.Single(started);
        Assert.Equal("ms-settings:", info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Empty(info.Arguments);
    }

    [Fact]
    public void AShellThatRefusesIsAFailureAndNeverAnException()
    {
        var actions = new WindowsSystemActions(
            _ => throw new Win32Exception(2), new FakeVolume(), () => true, _ => null);

        Assert.False(actions.OpenWindowsSettings());
    }

    [Fact]
    public void LockingIsWhatWindowsAnswers()
    {
        Assert.True(Actions(lockWorkstation: () => true).LockWorkstation());
        Assert.False(Actions(lockWorkstation: () => false).LockWorkstation());
    }

    [Fact]
    public void MutingAndUnmutingChangeOnlyTheSwitch()
    {
        var volume = new FakeVolume { Percent = 40 };
        var actions = Actions(volume);

        Assert.True(actions.SetMuted(true));
        Assert.True(volume.Muted);
        Assert.Equal(40, volume.Percent);

        Assert.True(actions.SetMuted(false));
        Assert.False(volume.Muted);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    [InlineData(-5, 0)]
    public void SettingTheVolumeKeepsItWithinZeroToAHundred(int asked, int expected)
    {
        var volume = new FakeVolume { Percent = 55 };

        Assert.True(Actions(volume).SetVolume(asked));

        Assert.Equal(expected, volume.Percent);
    }

    [Fact]
    public void ASetVolumeEndsMuteExceptWhenItIsNothing()
    {
        var volume = new FakeVolume { Percent = 55, Muted = true };
        var actions = Actions(volume);

        actions.SetVolume(0);
        Assert.True(volume.Muted);

        actions.SetVolume(20);
        Assert.False(volume.Muted);
    }

    [Theory]
    [InlineData(50, 10, 60)]
    [InlineData(50, -10, 40)]
    [InlineData(95, 10, 100)]
    [InlineData(5, -10, 0)]
    public void ChangingTheVolumeMovesItByPointsWithinZeroToAHundred(int from, int by, int expected)
    {
        var volume = new FakeVolume { Percent = from };

        Assert.Equal(expected, Actions(volume).ChangeVolume(by));
        Assert.Equal(expected, volume.Percent);
    }

    [Fact]
    public void RaisingTheVolumeEndsMuteAndLoweringItDoesNot()
    {
        var volume = new FakeVolume { Percent = 50, Muted = true };
        var actions = Actions(volume);

        actions.ChangeVolume(-10);
        Assert.True(volume.Muted);

        actions.ChangeVolume(10);
        Assert.False(volume.Muted);
    }

    [Fact]
    public void WithoutAnOutputDeviceTheVolumeCannotBeChanged()
    {
        var volume = new FakeVolume { Available = false };
        var actions = Actions(volume);

        Assert.False(actions.SetMuted(true));
        Assert.False(actions.SetVolume(10));
        Assert.Null(actions.ChangeVolume(10));
        Assert.Null(actions.GetVolume());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(42, false)]
    [InlineData(100, true)]
    public void TheVolumeAndTheMuteSwitchAreRead_AsTheyAre(int percent, bool muted)
    {
        var volume = new FakeVolume { Percent = percent, Muted = muted };
        var actions = Actions(volume);

        Assert.Equal(new VolumeState(percent, muted), actions.GetVolume());

        // Reading changes nothing.
        Assert.Equal(percent, volume.Percent);
        Assert.Equal(muted, volume.Muted);
    }

    [Fact]
    public void OnlyAFullPathIsGivenForAFolder()
    {
        var actions = Actions(folders: folder => folder switch
        {
            SystemFolder.Home => "C:\\Users\\sam",
            SystemFolder.Desktop => "relative\\path",
            SystemFolder.Documents => "",
            _ => null,
        });

        Assert.Equal("C:\\Users\\sam", actions.GetFolder(SystemFolder.Home));
        Assert.Null(actions.GetFolder(SystemFolder.Desktop));
        Assert.Null(actions.GetFolder(SystemFolder.Documents));
        Assert.Null(actions.GetFolder(SystemFolder.Music));
    }

    [Theory]
    [InlineData(SystemFolder.Home)]
    [InlineData(SystemFolder.Desktop)]
    [InlineData(SystemFolder.Documents)]
    [InlineData(SystemFolder.Downloads)]
    [InlineData(SystemFolder.Pictures)]
    [InlineData(SystemFolder.Music)]
    [InlineData(SystemFolder.Videos)]
    public void TheRealKnownFoldersAreFullPaths(SystemFolder folder)
    {
        var path = new WindowsSystemActions().GetFolder(folder);

        Assert.NotNull(path);
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.True(Directory.Exists(path), "Windows names a folder that is there.");
    }

    // The real speakers: what is read is read, and what is written is what was already there, so nothing is heard to change.
    [Fact]
    public void TheRealDefaultSpeakersReportTheirStateAndAcceptTheSameStateBack()
    {
        var volume = new SystemVolume();
        if (!volume.TryGetState(out var percent, out var muted))
        {
            return; // No output device on this PC.
        }

        Assert.InRange(percent, 0, 100);

        Assert.True(volume.SetPercent(percent));
        Assert.True(volume.SetMuted(muted));

        Assert.True(volume.TryGetState(out var after, out var mutedAfter));
        Assert.Equal(muted, mutedAfter);
        Assert.InRange(Math.Abs(after - percent), 0, 1);
    }

    private sealed class FakeVolume : ISystemVolume
    {
        public int Percent { get; set; } = 50;

        public bool Muted { get; set; }

        public bool Available { get; set; } = true;

        public bool TryGetState(out int percent, out bool muted)
        {
            percent = Percent;
            muted = Muted;
            return Available;
        }

        public bool SetMuted(bool muted)
        {
            if (Available)
            {
                Muted = muted;
            }

            return Available;
        }

        public bool SetPercent(int percent)
        {
            if (Available)
            {
                Percent = percent;
            }

            return Available;
        }
    }
}
