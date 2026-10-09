// What the extension captures when the user chooses the menu entry (PROJECT_SPEC §4.5, §5.7).
//
// Exactly three things leave the page: the text the user selected, the page's title and the
// page's address. The page itself (its body, its other text, its cookies) is never read. The
// browser's own name goes along, so the Assistant can say where the words came from. This module
// only shapes those values; it has no side effects and touches no browser API.

// The selection is capped where the Assistant caps what it reads from other applications
// (SelectionService.MaxTextLength); the title and address are cut to sane lengths.
export const MAX_SELECTION_LENGTH = 200000;
export const MAX_TITLE_LENGTH = 500;
export const MAX_URL_LENGTH = 2048;
export const MAX_BROWSER_NAME_LENGTH = 64;

/** Keeps at most `limit` UTF-16 units without cutting a surrogate pair in half. */
function clip(text, limit) {
  if (text.length <= limit) {
    return text;
  }
  let end = limit;
  const last = text.charCodeAt(end - 1);
  if (last >= 0xd800 && last <= 0xdbff) {
    end -= 1;
  }
  return text.slice(0, end);
}

function asText(value) {
  return typeof value === "string" ? value : "";
}

/**
 * Builds the request for one use of the menu entry, or returns null when nothing is selected.
 *
 * `info` is the click data of `chrome.contextMenus.onClicked`; `tab` is its tab. The address
 * comes from `info.pageUrl` (the frame's top page), the title from `tab.title`, which the
 * browser shows the extension only for the tab the user chose the entry in (`activeTab`).
 * `browserName` is what the browser calls itself (browser-name.js), or "" when that is not known.
 */
export function buildSelectionRequest(info, tab, browserName) {
  const selection = asText(info && info.selectionText);
  if (selection.trim() === "") {
    return null;
  }

  const url = asText(info.pageUrl) || asText(tab && tab.url);
  return {
    selectionText: clip(selection, MAX_SELECTION_LENGTH),
    selectionTruncated: selection.length > MAX_SELECTION_LENGTH,
    pageTitle: clip(asText(tab && tab.title), MAX_TITLE_LENGTH),
    pageUrl: clip(url, MAX_URL_LENGTH),
    browserName: clip(asText(browserName).trim(), MAX_BROWSER_NAME_LENGTH),
  };
}
