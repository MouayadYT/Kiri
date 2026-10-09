using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Assistant.Windows.Interop;

/// <summary>
/// ICompositorDesktopInterop (windows.ui.composition.interop.h), called through its vtable. CsWinRT's
/// <c>As&lt;T&gt;()</c> cannot cast a compositor to this classic COM interface, and a direct call needs no built-in
/// COM interop.
/// </summary>
internal static unsafe class CompositorDesktopInterop
{
    private static readonly Guid InterfaceId = new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    /// <summary>Creates a composition target that shows visuals in <paramref name="window"/>.</summary>
    /// <param name="isTopmost">Whether the visuals draw above the window's child windows.</param>
    public static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, nint window, bool isTopmost)
    {
        var unknown = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in InterfaceId, out var interop));
        try
        {
            // The interface's only method follows IUnknown's three.
            var create = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)(*(nint**)interop)[3];
            nint target;
            Marshal.ThrowExceptionForHR(create(interop, window, isTopmost ? 1 : 0, &target));
            try
            {
                return DesktopWindowTarget.FromAbi(target);
            }
            finally
            {
                Marshal.Release(target);
            }
        }
        finally
        {
            Marshal.Release(interop);
            GC.KeepAlive(compositor);
        }
    }
}
