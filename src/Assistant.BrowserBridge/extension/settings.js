// The one setting the extension has (PROJECT_SPEC §4.5, steps 88 and 90): how much of the page goes with the text the user selected.
//
// It is not stored anywhere. "Selection + Nearby Context" means the browser has granted the extension the optional `scripting` permission,
// which it asks the user for when they choose that mode; "Selection Only" means it has not. So the extension is installed with only the
// permissions its core feature needs, the choice survives restarts because the browser keeps the grant, and revoking the permission in the
// browser's own extension settings turns the page reading off without this code being involved at all.

// Selection Only: only the words the user selected, with the page's title and address. The default.
export const MODE_SELECTION_ONLY = "selection";

// Selection Only plus a short, cleaned stretch of the page's text just before and after the selection.
export const MODE_NEARBY = "nearby";

// The permission the nearby reading needs (manifest `optional_permissions`).
export const NEARBY_PERMISSION = "scripting";

const NEARBY_REQUEST = { permissions: [NEARBY_PERMISSION] };

/** The mode in force. It never rejects: when the browser cannot say, the page is not read. */
export async function readContextMode() {
  try {
    return (await chrome.permissions.contains(NEARBY_REQUEST)) ? MODE_NEARBY : MODE_SELECTION_ONLY;
  } catch {
    return MODE_SELECTION_ONLY;
  }
}

/**
 * Switches to `mode` and returns the mode that is in force afterwards. Choosing the nearby mode asks the browser for the optional
 * permission, which shows the user its own prompt and must be called from the click that chose it; when the user declines, the mode
 * stays Selection Only. Choosing Selection Only gives the permission back. Rejects only when the browser fails to carry out the change.
 */
export async function chooseContextMode(mode) {
  if (mode === MODE_NEARBY) {
    return (await chrome.permissions.request(NEARBY_REQUEST)) ? MODE_NEARBY : MODE_SELECTION_ONLY;
  }
  await chrome.permissions.remove(NEARBY_REQUEST);
  return MODE_SELECTION_ONLY;
}

/** Calls `listener` when the mode may have changed (the permission granted or taken back, here or in the browser's own settings). */
export function onContextModeChanged(listener) {
  chrome.permissions.onAdded.addListener(listener);
  chrome.permissions.onRemoved.addListener(listener);
}
