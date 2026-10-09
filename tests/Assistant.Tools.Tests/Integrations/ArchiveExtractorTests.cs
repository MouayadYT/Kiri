using System.Formats.Tar;
using Assistant.Tools.Integrations;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 108: an archive from the internet is hostile until proven otherwise.</summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Archive(byte[] bytes, string name = "archive.bin")
    {
        var path = _folder.File(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Destination => _folder.File("out");

    private void Extract(byte[] bytes, ArchiveKind kind, string top = "", ExtractionLimits? limits = null) =>
        ArchiveExtractor.Extract(Archive(bytes), kind, Destination, top, limits ?? ExtractionLimits.Default);

    private InstallException Refused(byte[] bytes, ArchiveKind kind, string top = "", ExtractionLimits? limits = null) =>
        Assert.Throws<InstallException>(() => Extract(bytes, kind, top, limits));

    // ---- zip ----

    [Fact]
    public void AZipIsUnpackedAndTheTopFolderIsDropped()
    {
        Extract(TestArchives.Zip(("pkg/", ""), ("pkg/a.txt", "A"), ("pkg/sub/b.txt", "B")), ArchiveKind.Zip, "pkg");

        Assert.Equal("A", File.ReadAllText(Path.Combine(Destination, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(Destination, "sub", "b.txt")));
    }

    [Fact]
    public void AZipWithoutATopFolderIsUnpackedAsItIs()
    {
        Extract(TestArchives.Zip(("manifest.json", "{}"), ("server/x.exe", "MZ")), ArchiveKind.Zip);

        Assert.True(File.Exists(Path.Combine(Destination, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(Destination, "server", "x.exe")));
    }

    [Fact]
    public void AZipWhoseFilesAreNotInsideTheExpectedTopFolderIsRefused()
    {
        var failure = Refused(TestArchives.Zip(("other/a.txt", "A")), ArchiveKind.Zip, "pkg");

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("sub/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/../../../evil.txt")]
    public void AnEntryThatWouldLandOutsideTheFolderRefusesTheWholeArchiveAndWritesNothingThere(string name)
    {
        var failure = Refused(TestArchives.Zip(("ok.txt", "fine"), (name, "pwned")), ArchiveKind.Zip);

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
        Assert.False(File.Exists(_folder.File("evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_folder.Path)!, "evil.txt")));
    }

    [Theory]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("a.txt:hidden")]
    [InlineData("con.txt")]
    [InlineData("dir/NUL")]
    [InlineData("lpt1")]
    [InlineData("trailing.")]
    [InlineData("name?.txt")]
    [InlineData("a|b.txt")]
    public void NamesThatWindowsWouldReadAsSomethingElseAreRefused(string name)
    {
        var failure = Refused(TestArchives.Zip((name, "x")), ArchiveKind.Zip);

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
    }

    [Fact]
    public void ALeadingSlashIsDroppedSoAnAbsolutePathLandsInsideTheFolderAndNowhereElse()
    {
        Extract(TestArchives.Zip(("/etc/passwd", "x")), ArchiveKind.Zip);

        Assert.True(File.Exists(Path.Combine(Destination, "etc", "passwd")));
    }

    [Theory]
    [InlineData(0xA000)]
    [InlineData(0x1000)]
    [InlineData(0x2000)]
    [InlineData(0x6000)]
    public void AZipEntryThatIsALinkOrASpecialFileIsRefused(int unixType)
    {
        var failure = Refused(TestArchives.ZipWithUnixType("link", unixType), ArchiveKind.Zip);

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
        Assert.False(File.Exists(Path.Combine(Destination, "link")));
    }

    [Fact]
    public void AZipWithMoreEntriesThanTheLimitIsRefused()
    {
        var entries = Enumerable.Range(0, 20).Select(index => ($"f{index}.txt", "x")).ToArray();

        var failure = Refused(TestArchives.Zip(entries), ArchiveKind.Zip, limits: new ExtractionLimits(MaxEntries: 10));

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
    }

    [Fact]
    public void AZipThatUnpacksToMoreThanTheTotalLimitIsStoppedWhileItIsWrittenNotWhenItDeclaresIt()
    {
        var bigZeros = new byte[300_000];

        var failure = Refused(TestArchives.Zip(("a.bin", bigZeros), ("b.bin", bigZeros)), ArchiveKind.Zip, limits: new ExtractionLimits(MaxTotalBytes: 400_000));

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
    }

    [Fact]
    public void AZipWithOneFileOverTheFileLimitIsRefused()
    {
        var failure = Refused(TestArchives.Zip(("a.bin", new byte[300_000])), ArchiveKind.Zip, limits: new ExtractionLimits(MaxFileBytes: 100_000));

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
    }

    [Fact]
    public void ABrokenZipIsASetupFailureAndNotACrash()
    {
        var failure = Refused([1, 2, 3, 4, 5], ArchiveKind.Zip);

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
    }

    [Fact]
    public void NothingTheArchiveHoldsIsEverRun()
    {
        Extract(TestArchives.Zip(("run.bat", "echo pwned > ..\\ran.txt"), ("evil.exe", "MZ")), ArchiveKind.Zip);

        Assert.False(File.Exists(_folder.File("ran.txt")));
    }

    // ---- tar.gz ----

    [Fact]
    public void ATarGzIsUnpackedAndTheTopFolderIsDropped()
    {
        Extract(TestArchives.TarGz(("python", "", TarEntryType.Directory), ("python/python.exe", "MZ", TarEntryType.RegularFile), ("python/Lib/x.py", "x = 1", TarEntryType.RegularFile)), ArchiveKind.TarGz, "python");

        Assert.Equal("MZ", File.ReadAllText(Path.Combine(Destination, "python.exe")));
        Assert.Equal("x = 1", File.ReadAllText(Path.Combine(Destination, "Lib", "x.py")));
    }

    [Fact]
    public void ATarNameIsACStringSoWhatFollowsItsFirstNulIsIgnoredAsRealArchivesNeedIt()
    {
        // The Python runtime's real archive holds names such as "python/DLLs/_asyncio.pyd" followed by a NUL and left-over header bytes.
        var name = "python/DLLs/_asyncio.pyd" + (char)0 + "cio.pyd";
        Extract(TestArchives.TarGz((name, "MZ", TarEntryType.RegularFile)), ArchiveKind.TarGz, "python");

        Assert.Equal("MZ", File.ReadAllText(Path.Combine(Destination, "DLLs", "_asyncio.pyd")));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public void ATarEntryThatIsALinkIsRefused(TarEntryType type)
    {
        var failure = Refused(TestArchives.TarGz(("python/ok.txt", "ok", TarEntryType.RegularFile), ("python/link", "C:/Windows/system32", type)), ArchiveKind.TarGz, "python");

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
        Assert.False(File.Exists(Path.Combine(Destination, "link")));
    }

    [Theory]
    [InlineData("python/../../evil.txt")]
    [InlineData("python/a/../../../evil.txt")]
    public void ATarEntryThatWouldLandOutsideTheFolderIsRefused(string name)
    {
        var failure = Refused(TestArchives.TarGz((name, "pwned", TarEntryType.RegularFile)), ArchiveKind.TarGz, "python");

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
        Assert.False(File.Exists(_folder.File("evil.txt")));
    }

    [Fact]
    public void ATarGzOverTheTotalLimitIsStopped()
    {
        var failure = Refused(
            TestArchives.TarGz(("a.txt", new string('x', 200_000), TarEntryType.RegularFile), ("b.txt", new string('x', 200_000), TarEntryType.RegularFile)),
            ArchiveKind.TarGz,
            limits: new ExtractionLimits(MaxTotalBytes: 300_000));

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
    }

    [Fact]
    public void ABrokenTarGzIsASetupFailure()
    {
        var failure = Refused([0x1f, 0x8b, 8, 0, 0, 0], ArchiveKind.TarGz);

        Assert.Equal(InstallFailure.SetupFailed, failure.Failure);
    }

    [Fact]
    public void AnArchiveIsUnpackedWithTheUnpackedFilesOnlyUnderTheDestination()
    {
        Extract(TestArchives.Zip(("a/b/c.txt", "x")), ArchiveKind.Zip);

        var files = Directory.EnumerateFiles(_folder.Path, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith("archive.bin", StringComparison.Ordinal)).ToList();
        Assert.All(files, file => Assert.StartsWith(Destination + Path.DirectorySeparatorChar, file, StringComparison.OrdinalIgnoreCase));
    }
}
