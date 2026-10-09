// "Selection + Nearby Context" (PROJECT_SPEC §4.5, step 88): when the user has chosen that mode in the extension's options, and
// only then, the service worker reads a bounded stretch of the page's text just before and just after the selection (see
// nearby-page.js), cleans it here, and adds it to the request. In "Selection Only" (the default) nothing is read from the page.
//
// This runs only when the user chooses the menu entry. The page's text is untrusted: it is cleaned of what cannot be read or is
// there to hide something, cut to a fixed length, and the Assistant cleans it again before it uses it.

import { collectNearbyText } from "./nearby-page.js";
import { MODE_NEARBY, readContextMode } from "./settings.js";

// The most characters kept on each side of the selection: what the Assistant's request limit allows (BrowserSelection.MaxNearbyLength).
export const MAX_NEARBY_LENGTH = 1000;

// The page is read for twice that, since cleaning removes runs of space and unreadable characters; and no more than this many of its
// nodes are looked at on each side of the selection, so a very large page cannot make the click slow.
export const GATHER_LENGTH = 2000;
export const MAX_NEARBY_NODES = 600;

// A word longer than this is data (an encoded blob, a token, a long address), not prose, and is dropped (NearbyPageText.MaxWordLength).
export const MAX_WORD_LENGTH = 80;

// The longest the page may take to answer before the selection is sent without its nearby text.
export const READ_TIMEOUT_MS = 2000;

const ELLIPSIS = "…";

// How far a cut moves to land between words.
const WORD_SEARCH_LENGTH = 30;

// Control, invisible formatting (zero-width, direction overrides, "tag" characters), unpaired surrogates, private-use and
// unassigned characters are not text a reader sees: they are dropped. Line breaks and spaces are handled before this applies.
const UNREADABLE = /[\p{Cc}\p{Cf}\p{Cs}\p{Co}\p{Cn}]/u;
const SPACE = /\p{Zs}/u;

function readable(text) {
  let out = "";
  for (const character of text) {
    const code = character.codePointAt(0);
    if (code === 0xfffd) {
      continue;
    }
    if (code === 0x0a || code === 0x0d || code === 0x85 || code === 0x2028 || code === 0x2029) {
      out += "\n";
    } else if (code === 0x09 || code === 0x0b || code === 0x0c) {
      out += " ";
    } else if (UNREADABLE.test(character)) {
      continue;
    } else if (SPACE.test(character)) {
      out += " ";
    } else {
      out += character;
    }
  }
  return out;
}

// One space between words, no words that are data, and no empty line but a single one between paragraphs.
function tidy(text) {
  const lines = [];
  let blank = false;
  for (const raw of text.split("\n")) {
    const line = raw
      .split(" ")
      .filter((word) => word !== "" && word.length <= MAX_WORD_LENGTH)
      .join(" ");
    if (line === "") {
      blank = lines.length > 0;
      continue;
    }
    if (blank) {
      lines.push("");
      blank = false;
    }
    lines.push(line);
  }
  return lines.join("\n");
}

function isSpace(character) {
  return character === " " || character === "\n";
}

// Cuts to at most `limit` characters, keeping the end next to the selection ("before") or the start ("after"), without cutting a
// word or a surrogate pair in half, and marks the cut with an ellipsis.
function cut(text, limit, side) {
  const keep = limit - 1;
  if (side === "before") {
    let start = text.length - keep;
    const first = text.charCodeAt(start);
    if (first >= 0xdc00 && first <= 0xdfff) {
      start += 1;
    }
    if (!isSpace(text[start - 1]) && !isSpace(text[start])) {
      const space = Math.min(text.length, start + WORD_SEARCH_LENGTH);
      for (let i = start; i < space; i += 1) {
        if (isSpace(text[i])) {
          start = i + 1;
          break;
        }
      }
    }
    return ELLIPSIS + text.slice(start).trimStart();
  }

  let end = keep;
  const last = text.charCodeAt(end - 1);
  if (last >= 0xd800 && last <= 0xdbff) {
    end -= 1;
  }
  if (!isSpace(text[end - 1]) && !isSpace(text[end])) {
    const floor = Math.max(1, end - WORD_SEARCH_LENGTH);
    for (let i = end - 1; i >= floor; i -= 1) {
      if (isSpace(text[i])) {
        end = i;
        break;
      }
    }
  }
  return text.slice(0, end).trimEnd() + ELLIPSIS;
}

/**
 * Cleans page text for one side of the selection ("before" or "after") and cuts it to `MAX_NEARBY_LENGTH`. Returns "" when
 * nothing readable is left, and for anything that is not a string.
 */
export function cleanNearbyText(value, side) {
  if (typeof value !== "string" || value.trim() === "") {
    return "";
  }
  const cleaned = tidy(readable(value));
  return cleaned.length <= MAX_NEARBY_LENGTH ? cleaned : cut(cleaned, MAX_NEARBY_LENGTH, side);
}

/**
 * Shapes what the page returned (`{ before, after }`, from nearby-page.js) into the two request fields that are not empty, or
 * returns null when there is nothing. What the page returned is untrusted: anything but two strings is ignored.
 */
export function shapeNearbyContext(result) {
  if (result === null || typeof result !== "object") {
    return null;
  }
  const fields = {};
  const before = cleanNearbyText(result.before, "before");
  const after = cleanNearbyText(result.after, "after");
  if (before !== "") {
    fields.nearbyBefore = before;
  }
  if (after !== "") {
    fields.nearbyAfter = after;
  }
  return before === "" && after === "" ? null : fields;
}

async function readPage(info, tab) {
  // The API exists only while the browser has granted the optional permission (settings.js); without it the page is simply not read.
  if (!chrome.scripting || typeof chrome.scripting.executeScript !== "function") {
    return null;
  }
  const target = { tabId: tab.id };
  if (typeof info.frameId === "number") {
    target.frameIds = [info.frameId];
  }
  const [injection] = await chrome.scripting.executeScript({
    target,
    func: collectNearbyText,
    args: [GATHER_LENGTH, MAX_NEARBY_NODES],
  });
  return shapeNearbyContext(injection && injection.result);
}

/**
 * The nearby context to send with the selection the user just chose the menu entry on: `{ nearbyBefore?, nearbyAfter? }`, or null
 * when the setting is "Selection Only", the page cannot be read (the browser refuses, it is a page the extension may not touch,
 * it did not answer in time) or it has nothing next to the selection. It never rejects: the selection goes either way.
 */
export async function captureNearbyContext(info, tab) {
  if ((await readContextMode()) !== MODE_NEARBY || !tab || typeof tab.id !== "number") {
    return null;
  }
  let timer;
  try {
    const timeout = new Promise((resolve) => {
      timer = setTimeout(() => resolve(null), READ_TIMEOUT_MS);
    });
    return await Promise.race([readPage(info, tab), timeout]);
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}
