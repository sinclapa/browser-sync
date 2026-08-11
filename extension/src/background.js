import { onMessage, send, ensureConnected, setupReconnectAlarm } from "./ws-connection.js";
import { registerBookmarkListeners, buildSnapshot, flushPendingDeletions } from "./bookmark-sync.js";
import { applyCommand } from "./apply-remote.js";

// Listeners must be registered at module top-level (not inside an async callback) so
// Chrome/Edge correctly reattaches them every time this service worker is woken up.
registerBookmarkListeners();
setupReconnectAlarm();

onMessage(async (message) => {
  switch (message.type) {
    case "helloAck":
    case "requestSnapshot": {
      // Deletes queued while disconnected must reach the host BEFORE the snapshot, so their
      // absence from that snapshot is already accounted for rather than being (mis)read as an
      // unexplained disappearance.
      await flushPendingDeletions();
      const nodes = await buildSnapshot();
      await send("snapshot", { generatedAt: new Date().toISOString(), nodes });
      break;
    }
    case "command":
      await applyCommand(message);
      break;
    default:
      break;
  }
});

chrome.runtime.onStartup.addListener(() => {
  ensureConnected();
});

chrome.runtime.onInstalled.addListener(() => {
  ensureConnected();
});

// The service worker can be evaluated fresh at any point (including immediately after
// being suspended), so always attempt a connection as soon as this module runs.
ensureConnected();
