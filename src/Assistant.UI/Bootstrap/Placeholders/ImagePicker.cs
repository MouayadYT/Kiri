using System.Windows;
using Microsoft.Win32;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>Asks the user for an image file, for the developer's image test (<c>demo image</c>).</summary>
internal interface IImagePicker
{
    /// <summary>The full path of the image file the user chose, or <see langword="null"/> when they chose none.</summary>
    Task<string?> PickAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The standard Open dialog, owned by the Assistant's window in front, so it stays above the topmost panel it was asked
/// from.
/// </summary>
internal sealed class OpenFileImagePicker : IImagePicker
{
    private const string Filter =
        "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.heic)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.heic" +
        "|All files (*.*)|*.*";

    /// <inheritdoc/>
    public async Task<string?> PickAsync(CancellationToken cancellationToken)
    {
        if (Application.Current is not { } application)
        {
            return null;
        }

        return await application.Dispatcher.InvokeAsync(() =>
        {
            var owner = application.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
            var dialog = new OpenFileDialog { Title = "Choose an image to ask about", Filter = Filter, CheckFileExists = true };
            var chosen = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return chosen == true ? dialog.FileName : null;
        }).Task.WaitAsync(cancellationToken).ConfigureAwait(true);
    }
}
