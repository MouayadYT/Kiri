// Where a captured selection goes next: to the Assistant app, through its native-messaging host
// (PROJECT_SPEC §4.5, §5.7). The browser starts the host, passes it the message on its standard
// input and returns its reply; the host hands the selection to the running app over a local pipe.
// No server and no network are involved, and nothing is stored or logged here.

// The host's name, as the host's registration names it (NativeHostRegistration.HostName).
export const HOST_NAME = "assistant.browser_bridge";

// The version of the message protocol this extension speaks (BrowserMessageProtocol.Version).
export const PROTOCOL_VERSION = 1;

/** The message the host reads for one captured selection (see selection-request.js). */
export function buildHostMessage(request) {
  return { v: PROTOCOL_VERSION, type: "selection", body: request };
}

/**
 * Sends the captured selection to the host. Resolves to the host's error code when the selection
 * did not reach the app ("appNotFound", "appDidNotRespond", ...), "hostUnavailable" when the browser
 * could not reach the host at all (it is not registered, or it stopped), and null when the app took it.
 * It never rejects: the click that led here has nobody to tell.
 */
export async function handOver(request) {
  try {
    const reply = await chrome.runtime.sendNativeMessage(HOST_NAME, buildHostMessage(request));
    if (reply && reply.type === "accepted") {
      return null;
    }
    return reply && reply.body && typeof reply.body.code === "string" ? reply.body.code : "hostUnavailable";
  } catch {
    return "hostUnavailable";
  }
}
