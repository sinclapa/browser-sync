// A durable, on-disk queue of deletions that have happened locally but not yet been confirmed
// delivered to the host.
//
// chrome.bookmarks.onRemoved is the ONLY trustworthy "this was deleted" signal available — it
// is the browser explicitly telling us. But it fires at an arbitrary moment, and under MV3 the
// service worker is torn down every ~30s idle, so more often than not there is no live socket
// at that instant. Previously the event was simply dropped in that case, leaving the host to
// infer deletion from a bookmark's absence in a later snapshot — inference from a negative,
// valid only if snapshots are guaranteed complete. They are not, and that inference twice
// caused large batches of real bookmarks to be deleted from both browsers.
//
// Persisting here first means a delete survives service-worker death AND host downtime, and is
// delivered as an explicit event on the next connect. That turns deletion back into a positive,
// trustworthy signal and removes the need to infer it from absence at all in the normal case.
const STORAGE_KEY = "browserSyncPendingDeletions";

// Bounds worst-case growth if the host is unreachable for a very long time. Far above any
// plausible real backlog; oldest entries are dropped first.
const MAX_QUEUED = 500;

async function getQueue() {
  const stored = await chrome.storage.local.get(STORAGE_KEY);
  return stored[STORAGE_KEY] || [];
}

export async function enqueue(nativeId, timestamp) {
  const queue = await getQueue();
  if (queue.some((entry) => entry.nativeId === nativeId)) return;

  queue.push({ nativeId, timestamp });
  const trimmed = queue.length > MAX_QUEUED ? queue.slice(queue.length - MAX_QUEUED) : queue;
  await chrome.storage.local.set({ [STORAGE_KEY]: trimmed });
}

export async function getAll() {
  return getQueue();
}

export async function clear(nativeIds) {
  const drop = new Set(nativeIds);
  const queue = await getQueue();
  const kept = queue.filter((entry) => !drop.has(entry.nativeId));
  if (kept.length !== queue.length) {
    await chrome.storage.local.set({ [STORAGE_KEY]: kept });
  }
}
