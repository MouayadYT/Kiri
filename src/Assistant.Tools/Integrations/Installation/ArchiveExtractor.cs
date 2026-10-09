using System.Formats.Tar;
using System.IO.Compression;

namespace Assistant.Tools.Integrations;

/// <summary>How much an archive may hold.</summary>
/// <param name="MaxEntries">The most files and folders.</param>
/// <param name="MaxTotalBytes">The most bytes of all the files together once unpacked.</param>
/// <param name="MaxFileBytes">The most bytes of one file once unpacked.</param>
public sealed record ExtractionLimits(int MaxEntries = 60_000, long MaxTotalBytes = 1_500_000_000, long MaxFileBytes = 500_000_000)
{
    /// <summary>The limits for a bundle or a runtime.</summary>
    public static ExtractionLimits Default { get; } = new();
}

/// <summary>
/// Unpacks a zip or a tar.gz file that came from the internet into a folder (PROJECT_SPEC §4.8, step 108), treating the archive as hostile: an entry whose
/// path would land outside the folder (<c>..</c>, a rooted or drive path, an alternate data stream, a device name) refuses the whole archive; a link
/// or a special file is never created; the number of entries and the bytes unpacked are counted as they are written and not as the archive declares
/// them. A file's content is never run. When it refuses, what it had unpacked is left for the caller to delete with the folder.
/// </summary>
internal static class ArchiveExtractor
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Unpacks <paramref name="archivePath"/> into <paramref name="destination"/>, leaving out the folder <paramref name="topFolder"/> that everything is inside, when given.</summary>
    /// <exception cref="InstallException">The archive is not safe or not valid.</exception>
    public static void Extract(string archivePath, ArchiveKind kind, string destination, string topFolder, ExtractionLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(limits);
        Directory.CreateDirectory(destination);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        try
        {
            if (kind == ArchiveKind.Zip)
            {
                ExtractZip(archivePath, root, topFolder, limits, cancellationToken);
            }
            else
            {
                ExtractTarGz(archivePath, root, topFolder, limits, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or NotSupportedException or FormatException or ArgumentException)
        {
            throw new InstallException(InstallFailure.SetupFailed, "The download is not a valid archive.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(InstallFailure.DiskFailed, "I could not unpack it. Check that the disk has room.", exception);
        }
    }

    private static void ExtractZip(string archivePath, string root, string topFolder, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > limits.MaxEntries)
        {
            throw new InstallException(InstallFailure.SetupFailed, "The archive holds more than I allow.");
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A zip made on a Unix system can mark an entry as a link in the high bits of its attributes.
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is 0xA000 or 0x1000 or 0x2000 or 0x6000 or 0xC000)
            {
                throw new InstallException(InstallFailure.SetupFailed, "The archive holds a link or a special file, which I do not unpack.");
            }

            var relative = Relative(entry.FullName, topFolder);
            if (relative is null)
            {
                continue;
            }

            var path = Resolve(root, relative);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            total = Copy(source, path, total, limits);
        }
    }

    private static void ExtractTarGz(string archivePath, string root, string topFolder, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        long total = 0;
        var count = 0;
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > limits.MaxEntries)
            {
                throw new InstallException(InstallFailure.SetupFailed, "The archive holds more than I allow.");
            }

            // A name in a tar header is a C string: it ends at the first NUL, whatever the header holds after it.
            var entryName = CutAtNul(entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    break;
                case TarEntryType.Directory:
                    if (Relative(entryName, topFolder) is { } folder)
                    {
                        Directory.CreateDirectory(Resolve(root, folder));
                    }

                    continue;
                case TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes:
                    continue;
                default:
                    throw new InstallException(InstallFailure.SetupFailed, "The archive holds a link or a special file, which I do not unpack.");
            }

            var relative = Relative(entryName, topFolder);
            if (relative is null)
            {
                continue;
            }

            var path = Resolve(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (entry.DataStream is null)
            {
                File.WriteAllBytes(path, []);
                continue;
            }

            total = Copy(entry.DataStream, path, total, limits);
        }
    }

    private static string CutAtNul(string name)
    {
        var nul = name.IndexOf((char)0);
        return nul >= 0 ? name[..nul] : name;
    }

    // The entry's path below the top folder, as a relative path with forward slashes; null for the top folder itself or something outside it that is skipped.
    private static string? Relative(string name, string topFolder)
    {
        var path = name.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (topFolder.Length > 0)
        {
            var prefix = topFolder.TrimEnd('/') + "/";
            if (path.Equals(topFolder.TrimEnd('/'), StringComparison.Ordinal) || path.Equals(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InstallException(InstallFailure.SetupFailed, "The archive is not laid out the way I expected.");
            }

            path = path[prefix.Length..];
        }

        return path.Length == 0 ? null : path;
    }

    // The full path of an entry inside the folder; refuses one that would be outside it or that Windows would read as something else.
    private static string Resolve(string root, string relative)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal) || segment.Any(char.IsControl)
                || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || segment.EndsWith('.') || segment.EndsWith(' ')
                || DeviceNames.Contains(Path.GetFileNameWithoutExtension(segment).Split('.')[0]))
            {
                throw new InstallException(InstallFailure.SetupFailed, "The archive holds a file with a name that is not safe to unpack.");
            }
        }

        var path = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallException(InstallFailure.SetupFailed, "The archive holds a file that would land outside its folder.");
        }

        return path;
    }

    // Writes the stream to the file, counting what is written, and returns the new total.
    private static long Copy(Stream source, string path, long total, ExtractionLimits limits)
    {
        using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            total += read;
            if (written > limits.MaxFileBytes || total > limits.MaxTotalBytes)
            {
                throw new InstallException(InstallFailure.SetupFailed, "The archive unpacks to more than I allow.");
            }

            target.Write(buffer, 0, read);
        }

        return total;
    }
}
