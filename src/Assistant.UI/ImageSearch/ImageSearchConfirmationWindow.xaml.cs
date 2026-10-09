using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Assistant.Core.Contracts;
using Assistant.Core.ImageSearch;

namespace Assistant.UI.ImageSearch;

/// <summary>
/// The window that asks whether a picture may be sent to an image search provider (PROJECT_SPEC §3.4, §4.6): it shows the picture as it
/// would be sent and who it would go to. <see cref="Confirmed"/> is true only when the user pressed Search; closing it any other way,
/// Esc and Enter included, is no.
/// </summary>
internal sealed partial class ImageSearchConfirmationWindow : Window
{
    public ImageSearchConfirmationWindow(ImageSearchDisclosure disclosure)
    {
        ArgumentNullException.ThrowIfNull(disclosure);
        InitializeComponent();
        Picture.Source = Decode(disclosure.Image);
        Body.Text = Describe(disclosure.ProviderName);
        SearchButton.Click += (_, _) =>
        {
            Confirmed = true;
            Close();
        };
        CancelButton.Click += (_, _) => Close();
    }

    /// <summary>Whether the user said yes to sending the picture.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The Search button.</summary>
    internal System.Windows.Controls.Button SearchChoice => SearchButton;

    /// <summary>The Cancel button.</summary>
    internal System.Windows.Controls.Button CancelChoice => CancelButton;

    /// <summary>What the window tells the user about where the picture goes.</summary>
    internal static string Describe(string providerName) =>
        $"The picture will be sent to {providerName} to find where it appears online. Nothing else from your PC is sent, and the " +
        "Assistant keeps no copy of it. You are asked each time.";

    private static BitmapSource? Decode(ReadOnlyMemory<byte> picture)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(picture.ToArray());
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// The user's say-so, asked in a window (<see cref="ImageSearchConfirmationWindow"/>): the explicit action that lets a picture leave this
/// PC. It asks on the UI thread and waits for the answer; with no window to ask in, the answer is no.
/// </summary>
internal sealed class WpfImageSearchConfirmation : IImageSearchConfirmation
{
    /// <inheritdoc/>
    public Task<bool> ConfirmAsync(ImageSearchDisclosure disclosure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disclosure);
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return Task.FromResult(false);
        }

        return dispatcher.InvokeAsync(
            () =>
            {
                var window = new ImageSearchConfirmationWindow(disclosure);
                window.ShowDialog();
                return window.Confirmed;
            },
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task;
    }
}
