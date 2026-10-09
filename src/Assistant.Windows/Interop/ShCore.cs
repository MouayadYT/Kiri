using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Display scaling functions from shcore.dll.</summary>
internal static partial class ShCore
{
    public const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Gets a monitor's DPI, as seen by the calling thread's DPI awareness, and returns the HRESULT.</summary>
    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
