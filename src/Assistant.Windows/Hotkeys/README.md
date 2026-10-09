# Global hotkey

`GlobalHotkeyService` registers one shortcut on an existing HWND with `RegisterHotKey`, using `MOD_NOREPEAT`.
It owns unregistering and message validation; it does not depend on WPF or create a window.
Construct, register, process messages and dispose on the owning UI thread.

The UI loads `ISettingsService.LoadAsync()` at startup and uses `AppSettings.Hotkeys.SearchOrAsk`
(default **Alt+A**). A null shortcut disables registration. Settings currently use the existing in-memory
provider; persistent settings and a settings editor remain separate steps. Key names support letters,
digits, F1–F24 and common navigation/editing keys such as Space, Enter, Escape and PageDown. Windows may
reject reserved shortcuts, including F12. Unknown names or modifier bits are rejected without logging
the supplied value. `Register` can replace or disable a shortcut on the same thread.

`OverlayHotkeyBinding` forwards the Assistant's one window's HWND messages to the service. Hotkey
invocation runs `AssistantWindowStateController.Invoke()`: it calls `ShowAndFocus()` on that same window, which
opens as the compact bar, or brings back the floating conversation the bar has grown into while one is open. Escape turns voice input off first, then clears the draft; Escape on an
empty draft, or losing focus, animates the window out and hides it, retaining its HWND, registration and draft.
Invoking it while it is leaving turns it back. Repeated shortcuts preserve the current draft.
Alt+F4 on the bar closes the overlay and exits the app; on the floating conversation it only dismisses it. Closing/shutdown unregisters the shortcut and detaches the hook.

The same window carries more services, each with an id of its own, so Windows tells the shortcuts apart:
`VisualIntelligenceHotkeyId` (**Alt+Shift+S**, starts the screen capture) and `SelectedTextHotkeyId`
(**Alt+Shift+W**, reads the selected text of the application in front and opens the Ask panel with it), and `SelectedTextByCopyHotkeyId`
(**Alt+Shift+C**, step 89: the explicit fallback that presses Copy in an application that does not share its selection; the app registers it
only while the Selected Text by Copy permission is on, passing no shortcut otherwise). All are read at startup, so a changed shortcut
applies at the next start.

Registration conflicts are non-fatal and produce a warning with the numeric hotkey id and Win32 error.
The initial overlay still opens and remains editable. No alternative shortcut is silently selected and
no prompt text, window content or configurable key string is logged. The normal application logging
privacy filter still applies.

Verification: `dotnet test Assistant.sln` on Windows. The native UI integration test requires desktop
input access and sends the uncommon Ctrl+Alt+Shift+F23 chord to verify real `WM_HOTKEY` delivery, focus,
window reuse and release. Unit tests cover configuration, conflicts, privacy-safe failure messages,
replacement, disabled shortcuts, stale messages and cleanup without touching global shortcuts.
