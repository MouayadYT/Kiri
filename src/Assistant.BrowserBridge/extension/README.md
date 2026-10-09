# Ask Assistant browser extension

Manifest V3 extension for Chromium browsers (Edge, Chrome, Brave). Part of `Assistant.BrowserBridge`
(PROJECT_SPEC §4.5, §5.7). It adds one **Ask Assistant** entry to the right-click menu of selected
text. Choosing it captures four things and nothing else: the selected text, the page's title, the
page's address and the browser's own name (such as "Microsoft Edge"). The page itself is not read, unless you
choose **Selection + Nearby Context** in the extension's options (below). The request goes to the Assistant app on this PC through
its native-messaging host (`Assistant.BrowserBridge.exe`), which the browser starts; no server and no
network connection are involved.

- **Permissions:** `contextMenus` (the entry), `activeTab` (the browser shows the title of the tab the
  entry was chosen in, only at that moment), `nativeMessaging` (to talk to the Assistant's own host
  on this PC). No host permissions, no content scripts, no storage, no remote code, no network connection.
  One **optional** permission, `scripting`, is asked for only if you choose Nearby Context (below): it lets the extension read the text
  around the selection, in the one frame you clicked in.
- **Files:** `manifest.json`; `service-worker.js` (the menu and its click); `selection-request.js`
  (shapes the captured values, with length limits); `browser-name.js` (what the browser calls itself,
  from its own brand list); `hand-over.js` (sends the request to the host
  and reads its answer); `settings.js` (the Selection Only / Nearby Context choice); `nearby-page.js` (the reader that
  runs inside the page, only in Nearby Context mode); `nearby-context.js` (cleans and bounds what it read, and asks for it);
  `options.html`, `options.css`, `options.js` (the options page); `_locales/en/messages.json`; `icons/`.
- **Identity:** the manifest's `key` gives the extension the same id on every machine
  (`cklcgaanpeplmbjmconamjjghmibhfok`), which the host's manifest names in `allowed_origins`.

## Selection Only or Selection + Nearby Context

Open the extension's options (`edge://extensions` > Details > *Extension options*, or right-click its toolbar icon > *Extension
options*) and choose:

- **Selection Only** (default): only the words you selected, with the page's title and address. Nothing is read from the page.
- **Selection + Nearby Context**: also a short stretch of the page's text just before and after the selection, at most 1,000
  characters on each side. It is read only when you choose *Ask Assistant*, only in the frame you chose it in, and never from form
  fields or hidden, script or style text; invisible and unreadable characters are removed, and very long "words" (encoded data) are
  dropped. The Assistant shows "Plus N characters of nearby page text" in its panel when it is included.

Choosing Nearby Context makes your browser ask for permission (the optional `scripting` permission); if you decline, the extension stays on
Selection Only. Choosing Selection Only gives the permission back, and you can also take it away in the browser's extension settings. Nothing is
stored by the extension: the choice *is* whether that permission is granted. It applies the next time you choose the entry.

## Renaming or re-skinning it

The wording lives in one place, `_locales/en/messages.json`: `extensionName`, `extensionDescription`
and `menuTitle` (the entry's label). The icon is the extension's own, `icons/icon-16/32/48/128.png`:
replace those files. The browser draws that icon next to the label itself (it cannot be set per
entry), and no script holds the product name.

## Try it

1. Turn on **Edge and Chrome** under *Settings > Integrations* in the Assistant (or run
   `Assistant.BrowserBridge.exe register` from the app's folder). This registers the native-messaging
   host for the current user; `unregister` removes it.
2. Open `edge://extensions`, `chrome://extensions` or `brave://extensions`, turn on *Developer mode*,
   choose *Load unpacked* and pick this folder.
3. Select text on any page and right-click: the menu shows **Ask Assistant**. Choosing it sends the
   selection to the running Assistant (and starts the Assistant if it is not running), which opens its
   Ask panel with the selected text, the page's title and address and the browser's name attached.
   Reload the extension in `edge://extensions` after updating this folder.

The host answers `{"v":1,"type":"pong"}` to `{"v":1,"type":"ping"}`; from the extension's service-worker
console, `chrome.runtime.sendNativeMessage("assistant.browser_bridge", {v: 1, type: "ping"})` checks
that the host is registered and reachable.
