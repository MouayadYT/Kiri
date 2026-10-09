using System.Buffers;
using System.Text;
using System.Text.Unicode;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;

namespace Assistant.Documents.Text;

/// <summary>
/// Turns the bytes of a text file into text without guessing at what is not text. The encoding is the byte order mark's (UTF-8,
/// UTF-16, UTF-32), else UTF-8 when the start of the file is valid UTF-8, else Windows-1252, which is what a file written by an
/// older program is. A file with no mark that holds a zero byte, or is mostly control characters, is binary data under a text
/// name: <see cref="DocumentReadStatus.Unsupported"/>, and nothing of it is returned.
/// </summary>
internal static class TextFileDecoder
{
    /// <summary>How much of the start of a file is looked at to decide its encoding and whether it is text.</summary>
    internal const int SampleBytes = 64 * 1024;

    private const int ChunkChars = 32 * 1024;

    /// <summary>
    /// Checks that <paramref name="stream"/> is a text file and leaves it at the start of its text. Returns the encoding to read it with
    /// and the length of its byte order mark.
    /// </summary>
    public static (Encoding Encoding, int PreambleLength) Sniff(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var rented = ArrayPool<byte>.Shared.Rent(SampleBytes);
        try
        {
            var read = 0;
            while (read < SampleBytes)
            {
                var n = stream.Read(rented, read, SampleBytes - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var sample = rented.AsSpan(0, read);
            var atEnd = read < SampleBytes || stream.Position >= stream.Length;

            if (sample.Length >= 4 && sample[0] == 0xFF && sample[1] == 0xFE && sample[2] == 0 && sample[3] == 0)
            {
                return (new UTF32Encoding(false, false), 4);
            }

            if (sample.Length >= 4 && sample[0] == 0 && sample[1] == 0 && sample[2] == 0xFE && sample[3] == 0xFF)
            {
                return (new UTF32Encoding(true, false), 4);
            }

            if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            {
                return (new UTF8Encoding(false), 3);
            }

            if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
            {
                return (new UnicodeEncoding(false, false), 2);
            }

            if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
            {
                return (new UnicodeEncoding(true, false), 2);
            }

            if (LooksBinary(sample))
            {
                throw new DocumentReadException(DocumentReadStatus.Unsupported);
            }

            return IsValidUtf8(sample, atEnd)
                ? (new UTF8Encoding(false), 0)
                : (CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1, 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Reads the text of the file, at most <paramref name="maxCharacters"/> characters of it. <paramref name="truncated"/> says
    /// whether there was more. The reader does not read past what it returns.
    /// </summary>
    public static string Read(Stream stream, int maxCharacters, CancellationToken cancellationToken, out bool truncated)
    {
        var (encoding, preamble) = Sniff(stream);
        stream.Seek(preamble, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        var text = new StringBuilder(Math.Min(maxCharacters, 256 * 1024));
        var buffer = ArrayPool<char>.Shared.Rent(ChunkChars);
        try
        {
            while (text.Length < maxCharacters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = reader.Read(buffer, 0, Math.Min(ChunkChars, maxCharacters - text.Length));
                if (n == 0)
                {
                    break;
                }

                text.Append(buffer, 0, n);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        truncated = text.Length >= maxCharacters && reader.Peek() >= 0;
        return text.ToString();
    }

    private static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        var controls = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                return true;
            }

            // Tab, line feed, form feed, carriage return and escape (a log with colours) are text.
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0C or 0x0D or 0x1B))
            {
                controls++;
            }
            else if (b == 0x7F)
            {
                controls++;
            }
        }

        return controls * 20 > sample.Length;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> sample, bool atEnd)
    {
        var chars = ArrayPool<char>.Shared.Rent(sample.Length);
        try
        {
            var status = Utf8.ToUtf16(sample, chars, out _, out _, replaceInvalidSequences: false, isFinalBlock: atEnd);
            return status is OperationStatus.Done or OperationStatus.NeedMoreData;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}
