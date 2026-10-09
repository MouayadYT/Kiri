using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Window subclassing from comctl32.dll.</summary>
internal static unsafe partial class ComCtl32
{
    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(
        nint window, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> subclassProc,
        nuint id, nuint referenceData);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveWindowSubclass(
        nint window, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> subclassProc, nuint id);

    [LibraryImport("comctl32.dll")]
    public static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
}
