using System.Runtime.InteropServices;

namespace Assistant.Windows.Hardware;

/// <summary>Enumerates present PnP devices, rather than inferring an NPU from the CPU's marketing name.</summary>
public static class NpuDevices
{
    public static IReadOnlyList<string> List()
    {
        List<string> names = [];
        var set = SetupDiGetClassDevsW(0, null, 0, 6); // PRESENT | ALLCLASSES
        if (set == new nint(-1)) return names;
        try
        {
            var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var name = Read(set, ref data, 12) ?? Read(set, ref data, 0);
                var deviceClass = Read(set, ref data, 7);
                if (name is not null && (deviceClass == "ComputeAccelerator" || name.Contains("AI Boost", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("NPU", StringComparison.OrdinalIgnoreCase) || name.Contains("Neural", StringComparison.OrdinalIgnoreCase))) names.Add(name);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return names.Distinct().ToArray();
    }

    private static string? Read(nint set, ref DeviceInfo data, uint property)
    {
        var bytes = new byte[2048];
        return SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, bytes, (uint)bytes.Length, out _)
            ? System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid Class; public uint Instance; public nuint Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern nint SetupDiGetClassDevsW(nint classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfo data, uint property, out uint type, byte[] buffer, uint size, out uint needed);
}
