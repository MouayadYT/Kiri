using System.IO;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Contracts;
using Assistant.UI.Controls;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Microsoft.Win32;

namespace Assistant.UI.Messages;

/// <summary>
/// The menu of a composer's plus (PROJECT_SPEC §4.2), as in the reference: Photos, to attach pictures, and Files, to attach documents the Assistant
/// reads, each chosen in the Windows file dialog, several at a time. It opens under the plus, in the app's own glass menu, which blurs what lies
/// behind it. What is chosen is handed to the composer it belongs to; nothing is read here, and nothing chosen is logged.
/// </summary>
internal static class AttachMenu
{
    /// <summary>The menu's words.</summary>
    public const string PhotosText = "Photos";

    /// <summary>The menu's words.</summary>
    public const string FilesText = "Files";

    /// <summary>
    /// Makes the menu for <paramref name="plus"/> and opens it when the plus is clicked. <paramref name="photos"/> and <paramref name="files"/> are
    /// given the pictures and the documents chosen in the dialog, never an empty list.
    /// </summary>
    public static ContextMenu Attach(
        Button plus, IWindowBackdropFactory? backdrops, Action<IReadOnlyList<ImageItem>> photos, Action<IReadOnlyList<DocumentAttachment>> files)
    {
        ArgumentNullException.ThrowIfNull(plus);
        var menu = new ContextMenu { Style = (Style)plus.FindResource("AttachMenu"), PlacementTarget = plus };
        System.Windows.Automation.AutomationProperties.SetName(menu, "Add to the conversation");
        menu.Items.Add(Item(PhotosText, () =>
        {
            var chosen = Pick(plus, "Choose photos", "Photos", ImageFileTypes.Extensions);
            if (chosen.Count > 0)
            {
                photos([.. chosen.Select(path => new ImageItem(Path.GetFileName(path), path))]);
            }
        }));
        menu.Items.Add(Item(FilesText, () =>
        {
            var chosen = Pick(plus, "Choose files", "Files the Assistant reads", DocumentFileTypes.Extensions);
            if (chosen.Count > 0)
            {
                files([.. chosen.Select(path => new DocumentAttachment(Path.GetFileName(path), path))]);
            }
        }));

        if (backdrops is not null)
        {
            var corners = (double)plus.FindResource("Radius.Menu");
            MenuBackdrop.Attach(menu, "Glass", size => PanelShape.GetBackdropCornerRadii(size, corners), backdrops);
        }

        // Through a command, and not the click alone: a glass button without a command is drawn but does nothing (GlassIconButton).
        plus.Command = new ViewModels.RelayCommand(_ => menu.IsOpen = true);
        return menu;
    }

    private static MenuItem Item(string text, Action chosen)
    {
        var item = new MenuItem { Header = text };
        System.Windows.Automation.AutomationProperties.SetName(item, text);

        // The dialog opens once the menu has closed, so that it does not open under it.
        item.Click += (_, _) => item.Dispatcher.BeginInvoke(chosen);
        return item;
    }

    // The files chosen in the Windows file dialog, over the window the plus is in; none when it is cancelled.
    private static IReadOnlyList<string> Pick(DependencyObject plus, string title, string kind, IReadOnlyList<string> extensions)
    {
        var patterns = string.Join(";", extensions.Select(extension => "*" + extension));
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = $"{kind}|{patterns}",
            Multiselect = true,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Window.GetWindow(plus)) == true ? dialog.FileNames : [];
    }
}
