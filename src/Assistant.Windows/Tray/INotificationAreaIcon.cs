namespace Assistant.Windows.Tray;

/// <summary>
/// The Assistant's icon in the notification area (the tray) of the taskbar (PROJECT_SPEC §4.9): a click on it, and the lines of a menu the
/// right button opens. It belongs to the thread that made it, which must pump messages, and raises its events there.
/// </summary>
public interface INotificationAreaIcon : IDisposable
{
    /// <summary>Whether the icon is in the notification area.</summary>
    bool IsShown { get; }

    /// <summary>The words that appear when the pointer rests on the icon. Setting them changes the icon at once while it is shown.</summary>
    string Tooltip { get; set; }

    /// <summary>The lines of the menu, from the top. They are read when the menu opens, so a change shows the next time.</summary>
    IReadOnlyList<TrayMenuItem> Menu { get; set; }

    /// <summary>Raised when the user clicks the icon, or chooses it with the keyboard.</summary>
    event EventHandler? Selected;

    /// <summary>Raised with the <see cref="TrayMenuItem.Id"/> of the line the user chose from the menu.</summary>
    event EventHandler<int>? CommandInvoked;

    /// <summary>Puts the icon in the notification area. Returns whether Windows accepted it; showing a shown icon changes nothing.</summary>
    bool Show();

    /// <summary>Takes the icon out of the notification area. It can be shown again.</summary>
    void Hide();
}
