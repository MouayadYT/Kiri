using System.Runtime.InteropServices;
using Assistant.Core.Hardware;
using Assistant.Windows.Interop;
using Microsoft.Win32;

namespace Assistant.Windows.Hardware;

/// <summary>
/// Reads the processor from Windows: its name and rated speed from the registry key Windows fills in at start, the physical cores from
/// <c>GetLogicalProcessorInformationEx</c> and the logical processors, in every processor group, from <c>GetActiveProcessorCount</c>.
/// </summary>
internal sealed class NativeCpuInfoSource : ICpuInfoSource
{
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    /// <inheritdoc/>
    public CpuInfo Read()
    {
        var (name, megahertz) = ReadRegistry();
        var logical = (int)Kernel32.GetActiveProcessorCount(Kernel32.AllProcessorGroups);
        return new CpuInfo(
            name, CountPhysicalCores(), logical > 0 ? logical : Environment.ProcessorCount,
            RuntimeInformation.OSArchitecture.ToString(), megahertz);
    }

    /// <summary>The processor's name as Windows records it, with the runs of spaces that some makers pad it with made single.</summary>
    internal static string Tidy(string? name) =>
        name is null ? string.Empty : string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static (string Name, int Megahertz) ReadRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProcessorKey);
            return (Tidy(key?.GetValue("ProcessorNameString") as string), key?.GetValue("~MHz") is int megahertz ? megahertz : 0);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (string.Empty, 0);
        }
    }

    // One record comes back for each physical core: a 4-byte relationship and a 4-byte size, then the record's own fields.
    private static unsafe int CountPhysicalCores()
    {
        uint length = 0;
        Kernel32.GetLogicalProcessorInformationEx(Kernel32.RelationProcessorCore, null, ref length);
        if (length < 8)
        {
            return 0;
        }

        var buffer = new byte[length];
        fixed (byte* pointer = buffer)
        {
            if (!Kernel32.GetLogicalProcessorInformationEx(Kernel32.RelationProcessorCore, pointer, ref length))
            {
                return 0;
            }
        }

        var cores = 0;
        var offset = 0;
        while (offset + 8 <= length)
        {
            var relationship = BitConverter.ToInt32(buffer, offset);
            var size = BitConverter.ToInt32(buffer, offset + 4);
            if (size <= 0)
            {
                break;
            }

            if (relationship == Kernel32.RelationProcessorCore)
            {
                cores++;
            }

            offset += size;
        }

        return cores;
    }
}
