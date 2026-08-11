// Chrome and Edge otherwise look identical to the extension APIs, so the browser kind is
// sniffed from the UA string.
export function getBrowserKind() {
  return navigator.userAgent.includes("Edg/") ? "edge" : "chrome";
}

// Fixed, hardcoded per-browser-kind IDs rather than a randomly-generated one persisted in
// chrome.storage.local. BrowserSync is explicitly scoped to exactly one Chrome + one Edge
// install on the same machine (see docs/architecture.md) — a random per-install UUID bought
// nothing over a fixed constant here, but it was unreliable in practice: chrome.storage.local
// was observed NOT persisting the generated ID across every service-worker cold start, causing
// a "new device" ID on some reconnects. The host then had no sync history for that "new"
// client and re-pushed every existing bookmark as a fresh create, producing runaway duplicate
// bookmarks. A fixed ID has nothing to fail to persist.
const FIXED_CLIENT_IDS = {
  chrome: "b2020000-0000-4000-8000-000000000001",
  edge: "b2020000-0000-4000-8000-000000000002",
};

export async function getOrCreateClientId() {
  return FIXED_CLIENT_IDS[getBrowserKind()];
}
