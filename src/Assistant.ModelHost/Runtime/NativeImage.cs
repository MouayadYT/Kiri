using System.Reflection.PortableExecutable;

namespace Assistant.ModelHost.Runtime;

/// <summary>
/// What the Windows loader needs to know about a native executable or DLL: the machine it is built for and the DLLs it
/// imports. Delay-loaded DLLs are left out: they load only when used, and may be optional.
/// </summary>
/// <param name="Machine">The machine the image is built for.</param>
/// <param name="Imports">The file names of the DLLs in its import table, as written there.</param>
internal sealed record NativeImage(Machine Machine, IReadOnlyList<string> Imports)
{
    // IMAGE_IMPORT_DESCRIPTOR: five 32-bit fields, the fourth the RVA of the DLL's name. A zeroed one ends the table.
    private const int ImportDescriptorSize = 20;
    private const int MaxNameLength = 260;

    /// <summary>Reads the headers and import table of the image at <paramref name="path"/>.</summary>
    /// <exception cref="BadImageFormatException">The file is not a valid PE image.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be opened.</exception>
    public static NativeImage Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new PEReader(stream);
        var headers = reader.PEHeaders;
        var table = (headers.PEHeader ?? throw new BadImageFormatException("The image has no optional header."))
            .ImportTableDirectory;

        var imports = new List<string>();
        if (table.Size > 0)
        {
            var descriptors = reader.GetSectionData(table.RelativeVirtualAddress).GetReader();
            while (descriptors.RemainingBytes >= ImportDescriptorSize)
            {
                descriptors.Offset += 12;
                var nameAddress = descriptors.ReadInt32();
                descriptors.Offset += 4;
                if (nameAddress == 0)
                {
                    break;
                }

                imports.Add(ReadName(reader, nameAddress));
            }
        }

        return new NativeImage(headers.CoffHeader.Machine, imports);
    }

    private static string ReadName(PEReader reader, int relativeVirtualAddress)
    {
        var name = reader.GetSectionData(relativeVirtualAddress).GetReader();
        var length = name.IndexOf(0);
        if (length is <= 0 or > MaxNameLength)
        {
            throw new BadImageFormatException("An imported DLL's name is malformed.");
        }

        return name.ReadUTF8(length);
    }
}
