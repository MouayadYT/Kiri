using System.Runtime.InteropServices;
using Assistant.Windows.Frame;
using Assistant.Windows.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Windows.Tests;

public sealed class WindowFrameTests
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [Theory]
    [InlineData(0x3A3B4C, 0x4C3B3A)]
    [InlineData(0xFF0000, 0x0000FF)]
    [InlineData(0x00FF00, 0x00FF00)]
    public void BorderColorsAreGivenToWindowsAsColorRefs(int rgb, int colorRef) =>
        Assert.Equal(colorRef, WindowFrame.ToColorRef(rgb));

    [Theory]
    [InlineData(SystemBackdropKind.Mica, 2)]
    [InlineData(SystemBackdropKind.Acrylic, 3)]
    [InlineData(SystemBackdropKind.MicaAlt, 4)]
    public void EachBackdropIsItsDwmType(SystemBackdropKind kind, int type) =>
        Assert.Equal(type, WindowFrame.ToBackdropType(kind));

    [Fact]
    public void NativeFrameIsDarkRoundedAndHasTheBackdropUntilTheWindowIsDestroyed()
    {
        // A hidden top-level window, so nothing appears on screen.
        var window = User32.CreateWindow(0, "STATIC", null, User32.WS_POPUP, 0, 0, 200, 100, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        using var factory = new WindowFrameFactory(NullLogger<WindowFrameFactory>.Instance);
        try
        {
            var frame = factory.Apply(window, new WindowFrameStyle(SystemBackdropKind.Mica, 0x3A3B4C));

            Assert.Equal(1, Attribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE));
            Assert.Equal(2, Attribute(window, DWMWA_WINDOW_CORNER_PREFERENCE));

            // Windows 11 22H2 and later draw the backdrop; earlier ones cannot, and the frame is then never translucent.
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                Assert.Equal(2, Attribute(window, DWMWA_SYSTEMBACKDROP_TYPE));
            }
            else
            {
                Assert.False(frame.IsTranslucent);
            }
        }
        finally
        {
            User32.DestroyWindow(window);
        }
    }

    [Fact]
    public void NativeFrameKeepsTheWindowWithoutASystemMenuSoWindowsDrawsNoCaptionButtons()
    {
        const nint SystemMenu = 0x00080000;
        const uint Caption = 0x00C00000;
        var window = User32.CreateWindow(0, "STATIC", null, Caption | (uint)SystemMenu, 0, 0, 200, 100, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        using var factory = new WindowFrameFactory(NullLogger<WindowFrameFactory>.Instance);
        try
        {
            factory.Apply(window, new WindowFrameStyle(SystemBackdropKind.Mica));
            Assert.Equal(0, User32.GetWindowLongPtr(window, -16) & SystemMenu);

            // Styles set later, such as by WPF, lose it too.
            User32.SetWindowLongPtr(window, -16, User32.GetWindowLongPtr(window, -16) | SystemMenu);
            Assert.Equal(0, User32.GetWindowLongPtr(window, -16) & SystemMenu);
            Assert.NotEqual(0, User32.GetWindowLongPtr(window, -16) & (nint)Caption);
        }
        finally
        {
            User32.DestroyWindow(window);
        }
    }

    [Fact]
    public void ApplyingToNoWindowFails()
    {
        using var factory = new WindowFrameFactory(NullLogger<WindowFrameFactory>.Instance);
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Apply(0, new WindowFrameStyle(SystemBackdropKind.Mica)));
    }

    private static int Attribute(nint window, int attribute)
    {
        Assert.Equal(0, DwmGetWindowAttribute(window, attribute, out var value, sizeof(int)));
        return value;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
