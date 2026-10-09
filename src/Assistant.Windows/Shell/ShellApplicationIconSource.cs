using System.Runtime.InteropServices;
using Assistant.Core.QuickSearch;
using Assistant.Windows.Imaging;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Shell;

/// <summary>
/// Draws an application's own icon (PROJECT_SPEC §4.1) as the shell does for Start: it asks the application's item in the apps folder
/// for its icon, 72 pixels square so that a 36-pixel tile is sharp on a screen at twice the usual density, and passes it on as a PNG
/// with its transparency. Nothing is saved: the picture is made in memory and handed back.
/// </summary>
public sealed class ShellApplicationIconSource : IApplicationIconSource
{
    /// <summary>The side of the picture, in pixels.</summary>
    public const int IconSize = 72;

    /// <inheritdoc/>
    public Task<byte[]?> GetIconAsync(string applicationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Any(char.IsControl))
        {
            return Task.FromResult<byte[]?>(null);
        }

        return StaThread.RunAsync(() => Draw(applicationId), cancellationToken);
    }

    private static byte[]? Draw(string applicationId)
    {
        IShellItem? item = null;
        nint bitmap = 0;
        try
        {
            // A shortcut file has the icon it says it has (a game's, for a launcher's link); an entry of the apps folder, its own.
            item = Shell32.CreateItem(ApplicationLaunchId.IsStartableFile(applicationId) ? applicationId : "shell:AppsFolder\\" + applicationId);
            if (item is not IShellItemImageFactory factory)
            {
                return null;
            }

            var size = new NativeSize { Width = IconSize, Height = IconSize };
            if (factory.GetImage(size, ShellImageFlags.IconOnly | ShellImageFlags.BiggerSizeOk, out bitmap) < 0 || bitmap == 0)
            {
                return null;
            }

            return Encode(bitmap);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            // An application whose icon cannot be read is listed with the application glyph.
            return null;
        }
        finally
        {
            if (bitmap != 0)
            {
                Gdi32.DeleteObject(bitmap);
            }

            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    // The bitmap's pixels, top row first, as a PNG. The shell's icon bitmaps hold colors that are multiplied by their alpha.
    private static unsafe byte[]? Encode(nint bitmap)
    {
        if (Gdi32.GetObject(bitmap, Marshal.SizeOf<Gdi32.BitmapInfo>(), out var info) == 0 || info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }

        var pixels = new byte[info.Width * info.Height * 4];
        var header = new Gdi32.BitmapInfoHeader
        {
            Size = (uint)sizeof(Gdi32.BitmapInfoHeader),
            Width = info.Width,
            Height = -info.Height,
            Planes = 1,
            BitCount = 32,
            Compression = Gdi32.BI_RGB,
        };
        var context = Gdi32.CreateCompatibleDC(0);
        if (context == 0)
        {
            return null;
        }

        try
        {
            int copied;
            fixed (byte* target = pixels)
            {
                copied = Gdi32.GetDIBits(context, bitmap, 0, (uint)info.Height, target, ref header, Gdi32.DIB_RGB_COLORS);
            }

            return copied == 0 ? null : IconPng.Encode(pixels, info.Width, info.Height, premultiplied: true);
        }
        finally
        {
            Gdi32.DeleteDC(context);
        }
    }
}
