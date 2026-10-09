# Window placement

`WindowPlacementService` moves an overlay onto the monitor the user is working on, near the upper center of that
monitor's work area. It does not depend on WPF.

**Which monitor.** The one holding the foreground window. If there is none, or it is the overlay itself (or a
window it owns), or the shell desktop (`Progman`/`WorkerW`, which spans every monitor), or it is on no monitor, the
monitor under the pointer is used, and the primary monitor as a last resort.

**Where on it.** The caller describes the window in DIPs with an `OverlayLayout`: its size, and the anchor, the
visible part to line up (for the Search or Ask bar, the pill inside its shadow gutter). The anchor is centered
horizontally in the work area, with its top edge at the layout's vertical position (22 % for the bar). The work area
excludes the taskbar and docked app bars, wherever they are. The whole window stays inside the work area when it
fits; otherwise the anchor does, and at worst the anchor's top-left corner.

**DPI.** Everything is computed in physical pixels at the target monitor's effective DPI, so positions hold on
monitors with different scaling and at negative coordinates (monitors left of or above the primary). This relies on
the process being per-monitor DPI aware v2, which `Assistant.UI/app.manifest` declares.

The window is moved with `SetWindowPos`, never through WPF's `Left`/`Top`, which convert at the DPI of the monitor
the window is leaving. Place the window while it is hidden: when the move crosses a DPI boundary, Windows sends
`WM_DPICHANGED` during the move and WPF rescales the window then, before it is shown. If rescaling shifts the window,
the service moves it back. `AssistantWindow.ShowAndFocus` places the bar each time it goes from hidden to visible,
including at startup; a visible bar is never moved. When the bar grows into the floating conversation, which is
taller, `FitAnchorTop` reports where the panel would have to be to fit the work area, without moving anything; if that
is higher than the bar, the window is placed a little higher on every frame of the growth, so it rises as it grows.

**Beside another application's window (step 87).** `DescribeForegroundWindow(excludeProcessId)` notes the window the user is in as a
`NearWindowTarget` (its visible bounds, from DWM's extended frame bounds, the pointer, the process id): nothing when there is no
foreground window, it is the desktop, minimized or hidden, or belongs to the overlay's own process. It is safe from any thread and meant to
be called at the moment of the user's act. `FindAnchorTopNear(target, layout)` then asks `NearWindowCalculator` for the point at which
the anchor's top center goes, on the monitor holding the middle of the window and at its DPI: the part of the window that is on the
monitor's work area, the anchor 24 DIPs in from its right edge and 96 DIPs below its top (past a browser's tabs and toolbar), raised
when the window is too short for it below that, on the left edge instead when the pointer is within 64 DIPs of the right-hand place and not
of the left one, centered on a window too narrow for the anchor and its insets. The point goes to `PlaceAnchorTopAt`, which also keeps the
anchor inside the work area. Checked on the four monitors of the dev PC (mixed 125 % and 100 %, negative coordinates): the panel's right inset
came out 23.6 to 24 DIPs and its top inset 96 on each.

**Full windows.** `PlaceCenteredOnActiveMonitor` sizes a top-level window, such as the History window, to the size it
prefers in DIPs at the chosen monitor's DPI, shrunk as far as leaves a margin free around it on a smaller work area, and
centers it there. It sets the bounds again if rescaling for a new DPI changed them.

Logs record only how the monitor was chosen, its DPI and Win32 error codes, never anything about other applications'
windows.

Verification: `dotnet test Assistant.sln`. Unit tests cover the layout arithmetic (scales from 100 % to 300 %,
taskbars on any edge, negative coordinates, work areas too small for the window or the anchor), the monitor fallbacks,
re-moving after a DPI rescale, and failures. One native test moves a hidden window with the real Win32 calls.
