using System.IO.Compression;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;

namespace Assistant.Documents.OpenXml;

/// <summary>
/// What is checked of a DOCX or PPTX before the Open XML library opens it, reading only the package's table of contents and
/// nothing inside it: that it is a package at all (a password-protected one is not a zip but a compound file, which is told from
/// a legacy <c>.doc</c> by the stream that holds the encrypted package), that it does not list an absurd number of entries, and
/// that the parts that hold text do not declare more than <see cref="DocumentReadLimits.MaxExpandedBytes"/> once unpacked, so a
/// small file that expands a thousandfold is refused without being expanded. Pictures, videos and embedded objects are not
/// counted: they are never read.
/// </summary>
internal static class OpenXmlPackageGuard
{
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    // "EncryptedPackage" as a compound file writes a stream's name in its directory: UTF-16, little endian.
    private static readonly byte[] EncryptedPackageName = System.Text.Encoding.Unicode.GetBytes("EncryptedPackage");

    public static void Check(Stream stream, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[8];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read >= CompoundFileSignature.Length && header.SequenceEqual(CompoundFileSignature))
        {
            // Not a package. A protected one is Encrypted (it needs a password the Assistant does not have); anything else in this
            // format is a legacy binary document, which no reader here reads.
            throw new DocumentReadException(
                ContainsEncryptedPackage(stream, cancellationToken) ? DocumentReadStatus.Encrypted : DocumentReadStatus.Unsupported);
        }

        stream.Seek(0, SeekOrigin.Begin);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        if (archive.Entries.Count > DocumentReadLimits.MaxPackageEntries)
        {
            throw new DocumentReadException(DocumentReadStatus.TooLarge);
        }

        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                expanded += entry.Length;
                if (expanded > limits.MaxExpandedBytes)
                {
                    throw new DocumentReadException(DocumentReadStatus.TooLarge);
                }
            }
        }

        stream.Seek(0, SeekOrigin.Begin);
    }

    private static bool ContainsEncryptedPackage(Stream stream, CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[(1 << 20) + EncryptedPackageName.Length];
        var carried = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, carried, buffer.Length - carried);
            if (read == 0)
            {
                return false;
            }

            var filled = carried + read;
            if (buffer.AsSpan(0, filled).IndexOf(EncryptedPackageName) >= 0)
            {
                return true;
            }

            // A name can straddle two reads: keep the end of this one at the start of the next.
            carried = Math.Min(EncryptedPackageName.Length - 1, filled);
            buffer.AsSpan(filled - carried, carried).CopyTo(buffer);
        }
    }
}
