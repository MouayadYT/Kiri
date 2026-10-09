using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Assistant.UI.Capture;

/// <summary>Puts a picture on the clipboard, for the Copy chip of Visual Intelligence (PROJECT_SPEC §4.6).</summary>
internal interface IImageClipboard
{
    /// <summary>Puts <paramref name="image"/> on the clipboard. Returns whether it is there: another program may hold the clipboard.</summary>
    bool TrySetImage(BitmapSource image);
}

/// <summary>
/// The Windows clipboard, through WPF. It tries a few times, since another program that has just used it may still hold it. The picture
/// is marked so that Windows does not send it to the user's other devices (the cloud clipboard goes through Microsoft's servers, and
/// the Assistant keeps what was on the screen on this PC); it is pasted and kept in the local clipboard history as any copy is.
/// </summary>
internal sealed class WpfImageClipboard : IImageClipboard
{
    /// <summary>The clipboard format that keeps a copy off the cloud clipboard: a 32-bit number, 0 for no.</summary>
    internal const string CloudUploadFormat = "CanUploadToCloudClipboard";

    private const int Attempts = 5;
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(40);

    /// <inheritdoc/>
    public bool TrySetImage(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(CreateData(image), copy: true);
                return true;
            }
            catch (COMException)
            {
                // CLIPBRD_E_CANT_OPEN: held by another program for a moment.
                Thread.Sleep(Wait);
            }
        }

        return false;
    }

    /// <summary>What goes on the clipboard: the picture, and the mark that it stays on this PC.</summary>
    internal static DataObject CreateData(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData(CloudUploadFormat, new MemoryStream(BitConverter.GetBytes(0)));
        return data;
    }
}
