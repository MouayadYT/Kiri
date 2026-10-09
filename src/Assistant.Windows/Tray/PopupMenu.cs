using Assistant.Windows.Interop;

namespace Assistant.Windows.Tray;

/// <summary>The native pop-up menu of the notification-area icon.</summary>
internal static class PopupMenu
{
    /// <summary>
    /// Opens a menu of <paramref name="items"/> with its bottom corner at the screen point (<paramref name="x"/>, <paramref name="y"/>), kept
    /// in the work area so it never lies over the taskbar, and waits for the user to choose a line or to dismiss it.
    /// </summary>
    /// <param name="owner">The icon's hidden window, which receives the menu's messages.</param>
    /// <returns>The id of the line chosen, or 0 when the menu was dismissed.</returns>
    public static int Show(nint owner, IReadOnlyList<TrayMenuItem> items, int x, int y)
    {
        UxTheme.AllowDarkMenus();
        var menu = User32.CreatePopupMenu();
        if (menu == 0)
        {
            return 0;
        }

        try
        {
            var defaultId = 0u;
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    User32.AppendMenu(menu, User32.MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = User32.MF_STRING;
                flags |= item.IsEnabled ? 0 : User32.MF_GRAYED;
                flags |= item.IsChecked ? User32.MF_CHECKED : 0;
                User32.AppendMenu(menu, flags, (nuint)item.Id, item.Text);
                if (item.IsDefault)
                {
                    defaultId = (uint)item.Id;
                }
            }

            if (defaultId != 0)
            {
                User32.SetMenuDefaultItem(menu, defaultId, 0);
            }

            // The window that owns the menu must be the foreground one, or the menu does not close when the user clicks somewhere else.
            User32.SetForegroundWindow(owner);
            var command = User32.TrackPopupMenuEx(
                menu, User32.TPM_RIGHTBUTTON | User32.TPM_BOTTOMALIGN | User32.TPM_RETURNCMD | User32.TPM_WORKAREA, x, y, owner, 0);

            // ... and a message afterwards lets the menu go away completely (the documented remedy).
            User32.PostMessage(owner, User32.WM_NULL, 0, 0);
            return command;
        }
        finally
        {
            User32.DestroyMenu(menu);
        }
    }
}
