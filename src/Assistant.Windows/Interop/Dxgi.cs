using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>
/// The parts of DXGI, the DirectX graphics infrastructure, that list the graphics adapters and the video memory of each (PROJECT_SPEC §5.6, step 124).
/// DXGI is the one Windows interface that reports a card's memory in full: the older WMI figure (<c>Win32_VideoController.AdapterRAM</c>) is a
/// 32-bit number that stops at 4 GB.
/// </summary>
internal static partial class Dxgi
{
    /// <summary><c>DXGI_ERROR_NOT_FOUND</c>: there is no adapter with the index asked for, which ends the enumeration.</summary>
    public const int ErrorNotFound = unchecked((int)0x887A0002);

    /// <summary><c>DXGI_ADAPTER_FLAG_SOFTWARE</c>: the adapter is a software renderer, not a device.</summary>
    public const uint AdapterFlagSoftware = 2;

    /// <summary><c>DXGI_MEMORY_SEGMENT_GROUP_LOCAL</c>: the adapter's own memory.</summary>
    public const uint SegmentGroupLocal = 0;

    /// <summary>The interface id of <see cref="IDxgiFactory1"/>.</summary>
    public static readonly Guid FactoryIid = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(in Guid interfaceId, out nint factory);

    /// <summary>The native <c>DXGI_ADAPTER_DESC1</c>.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    /// <summary>The native <c>DXGI_QUERY_VIDEO_MEMORY_INFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QueryVideoMemoryInfo
    {
        /// <summary>How much of the segment Windows says programs may use now.</summary>
        public ulong Budget;

        /// <summary>How much of it is in use.</summary>
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }
}

// The interfaces below are declared in the order of their native tables (after IUnknown's three entries), because that order is how a call finds its
// method. An entry this code never calls is a placeholder.

/// <summary><c>IDXGIFactory1</c>.</summary>
[ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDxgiFactory1
{
    // IDXGIObject: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent.
    void Placeholder3();
    void Placeholder4();
    void Placeholder5();
    void Placeholder6();

    // IDXGIFactory: EnumAdapters, MakeWindowAssociation, GetWindowAssociation, CreateSwapChain, CreateSoftwareAdapter.
    void Placeholder7();
    void Placeholder8();
    void Placeholder9();
    void Placeholder10();
    void Placeholder11();

    [PreserveSig]
    int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDxgiAdapter1 adapter);
}

/// <summary><c>IDXGIAdapter1</c>.</summary>
[ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDxgiAdapter1
{
    // IDXGIObject: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent.
    void Placeholder3();
    void Placeholder4();
    void Placeholder5();
    void Placeholder6();

    // IDXGIAdapter: EnumOutputs, GetDesc, CheckInterfaceSupport.
    void Placeholder7();
    void Placeholder8();
    void Placeholder9();

    [PreserveSig]
    int GetDesc1(out Dxgi.AdapterDesc1 description);
}

/// <summary><c>IDXGIAdapter3</c>, which can say how much video memory is in use.</summary>
[ComImport, Guid("645967A4-1392-4310-A798-8053CE3E93FD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDxgiAdapter3
{
    // IDXGIObject: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent.
    void Placeholder3();
    void Placeholder4();
    void Placeholder5();
    void Placeholder6();

    // IDXGIAdapter: EnumOutputs, GetDesc, CheckInterfaceSupport. IDXGIAdapter1: GetDesc1. IDXGIAdapter2: GetDesc2.
    void Placeholder7();
    void Placeholder8();
    void Placeholder9();
    void Placeholder10();
    void Placeholder11();

    // IDXGIAdapter3: RegisterHardwareContentProtectionTeardownStatusEvent, UnregisterHardwareContentProtectionTeardownStatus.
    void Placeholder12();
    void Placeholder13();

    [PreserveSig]
    int QueryVideoMemoryInfo(uint nodeIndex, uint memorySegmentGroup, out Dxgi.QueryVideoMemoryInfo information);
}
