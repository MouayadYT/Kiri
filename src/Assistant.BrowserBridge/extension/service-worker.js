// Ask Assistant: Manifest V3 service worker (PROJECT_SPEC §4.5, §5.7).
//
// Registers one entry in the right-click menu of selected text. When the user chooses it, the
// selection, the page's title, the page's address and the browser's name are captured
// (selection-request.js, browser-name.js) and handed over (hand-over.js). With the option "Selection + Nearby
// Context" (options.html, settings.js) a bounded, cleaned stretch of the page's text around the selection goes
// with them (nearby-context.js); by default, "Selection Only", the page itself is never read. No network is
// used, and nothing happens in the background.
//
// The entry's wording comes from _locales/en/messages.json ("menuTitle") and its icon is the
// extension's own (manifest "icons"), so renaming or re-skinning it touches no code.

import { detectBrowserName } from "./browser-name.js";
import { handOver } from "./hand-over.js";
import { captureNearbyContext } from "./nearby-context.js";
import { buildSelectionRequest } from "./selection-request.js";

const MENU_ID = "assistant.ask";

// Menu entries persist across browser restarts, so they are (re)created when the extension is
// installed or updated, never on every worker start (which would fail with a duplicate id).
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_ID,
      title: chrome.i18n.getMessage("menuTitle"),
      contexts: ["selection"],
    });
  });
});

// The selection goes to the app either way; the page's nearby text goes with it only when the user chose that and it could be read.
async function sendRequest(request, info, tab) {
  const nearby = await captureNearbyContext(info, tab);
  handOver(nearby === null ? request : { ...request, ...nearby });
}

// Listeners must be registered synchronously at the top level so the worker is woken for them.
chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU_ID) {
    return;
  }
  const request = buildSelectionRequest(info, tab, detectBrowserName(navigator));
  if (request !== null) {
    sendRequest(request, info, tab);
  }
});
