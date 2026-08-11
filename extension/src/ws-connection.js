import { WS_URL, PROTOCOL_VERSION, HEALTHCHECK_ALARM, RECONNECT_ALARM_PERIOD_MINUTES } from "./constants.js";
import { getOrCreateClientId, getBrowserKind } from "./client-id.js";

// How long a bookmark-event handler will wait for a connection before giving up on sending
// that particular event. MV3 tears the service worker down after ~30s idle; when a bookmark
// change wakes it back up, the WebSocket has to be re-established from scratch, and that isn't
// instant. Without this wait, an event arriving in that window used to be silently dropped —
// send() would see `socket` as null (a fresh SW has no memory of the old connection) and just
// no-op, with nothing but the next periodic reconciliation (minutes away) to catch it up.
const CONNECT_TIMEOUT_MS = 5000;

let socket = null;
let connectingPromise = null;
let messageHandler = null;

export function onMessage(handler) {
  messageHandler = handler;
}

function envelope(type, fields, clientId) {
  return { type, v: 1, clientId, ts: Date.now(), ...fields };
}

/// Returns true only if the message was actually written to an OPEN socket. Callers that must
/// not lose a message (see pending-deletions.js) use this to decide whether it's safe to drop
/// their durable copy.
export async function send(type, fields) {
  const ws = await ensureConnected();
  if (!ws || ws.readyState !== WebSocket.OPEN) return false; // gave up; the next reconciliation will catch this up
  const clientId = await getOrCreateClientId();
  ws.send(JSON.stringify(envelope(type, fields, clientId)));
  return true;
}

/// Resolves to the live, OPEN WebSocket once connected, or null if the attempt failed/timed out.
/// Callers await this rather than checking a socket synchronously, so a caller that runs right
/// as the service worker wakes up gets a real chance at the connection finishing.
export async function ensureConnected() {
  if (socket && socket.readyState === WebSocket.OPEN) return socket;
  // background.js calls this from several triggers (initial load, onStartup, onInstalled, the
  // healthcheck alarm) that can overlap, plus every send() call. Sharing one in-flight promise
  // means overlapping callers all await the same connection attempt instead of each creating
  // their own WebSocket (which previously caused the first one's handlers to end up
  // referencing the second, still-connecting socket instead of themselves).
  if (!connectingPromise) {
    connectingPromise = connect().finally(() => {
      connectingPromise = null;
    });
  }
  return connectingPromise;
}

async function connect() {
  const clientId = await getOrCreateClientId();
  const browser = getBrowserKind();

  // Handlers below close over `ws` (this specific instance), not the shared `socket`
  // variable, so a later reconnect reassigning `socket` can't cause a stale handler to act
  // on the wrong WebSocket.
  const ws = new WebSocket(WS_URL);
  socket = ws;

  ws.addEventListener("open", () => {
    ws.send(JSON.stringify(envelope("hello", { browser, protocolVersion: PROTOCOL_VERSION }, clientId)));
  });

  ws.addEventListener("message", (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      return;
    }
    messageHandler?.(message);
  });

  ws.addEventListener("close", () => {
    if (socket === ws) socket = null;
    // A short in-memory retry for while the service worker happens to still be alive.
    // The chrome.alarms healthcheck (below) is the durable reconnect path that survives
    // MV3 service-worker suspension, where a plain setTimeout would simply never fire.
    setTimeout(() => {
      ensureConnected();
    }, 3000);
  });

  ws.addEventListener("error", () => {
    // A WebSocket "error" is always followed by "close" — nothing extra to do here.
  });

  return waitForOpen(ws);
}

function waitForOpen(ws) {
  if (ws.readyState === WebSocket.OPEN) return Promise.resolve(ws);

  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      cleanup();
      resolve(null);
    }, CONNECT_TIMEOUT_MS);

    ws.addEventListener("open", onOpen);
    ws.addEventListener("close", onClose);

    function onOpen() {
      cleanup();
      resolve(ws);
    }

    function onClose() {
      cleanup();
      resolve(null);
    }

    function cleanup() {
      clearTimeout(timer);
      ws.removeEventListener("open", onOpen);
      ws.removeEventListener("close", onClose);
    }
  });
}

export function setupReconnectAlarm() {
  chrome.alarms.create(HEALTHCHECK_ALARM, { periodInMinutes: RECONNECT_ALARM_PERIOD_MINUTES });
  chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name === HEALTHCHECK_ALARM) ensureConnected();
  });
}
