using System.Buffers.Binary;
using System.IO.Compression;

namespace Assistant.Windows.Imaging;

/// <summary>
/// Writes a small picture with transparency as a PNG, in memory and without the Windows imaging codecs: what an application's icon
/// is passed on as. The pixels are 8-bit red, green, blue and alpha, written as they come, in one chunk of image data.
/// </summary>
internal static class IconPng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Makes a PNG of <paramref name="bgra"/>: four bytes a pixel in the order blue, green, red, alpha, the first row at the top and
    /// none between rows. With <paramref name="premultiplied"/> the colors have been multiplied by their alpha, as the shell's icon
    /// bitmaps have, and are divided by it again, since a PNG holds them the other way.
    /// </summary>
    public static byte[] Encode(byte[] bgra, int width, int height, bool premultiplied)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if (bgra.LongLength != (long)width * height * 4)
        {
            throw new ArgumentException("The pixels are not the size of the picture.", nameof(bgra));
        }

        // Each row begins with its filter, none, and holds red, green, blue and alpha.
        var rows = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var target = y * (width * 4 + 1);
            rows[target++] = 0;
            for (var x = 0; x < width; x++)
            {
                var source = ((y * width) + x) * 4;
                byte blue = bgra[source], green = bgra[source + 1], red = bgra[source + 2], alpha = bgra[source + 3];
                if (premultiplied && alpha is > 0 and < 255)
                {
                    blue = Undo(blue, alpha);
                    green = Undo(green, alpha);
                    red = Undo(red, alpha);
                }

                rows[target++] = red;
                rows[target++] = green;
                rows[target++] = blue;
                rows[target++] = alpha;
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; // bits per channel
        header[9] = 6; // red, green, blue and alpha
        WriteChunk(output, "IHDR"u8, header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(rows);
            }

            WriteChunk(output, "IDAT"u8, compressed.ToArray());
        }

        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static byte Undo(byte color, byte alpha) => (byte)Math.Min(255, ((color * 255) + (alpha / 2)) / alpha);

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        var crc = Crc.Update(Crc.Update(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    // The checksum every PNG chunk ends with (CRC-32, polynomial 0xEDB88320).
    private static class Crc
    {
        private static readonly uint[] Table = Build();

        public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
