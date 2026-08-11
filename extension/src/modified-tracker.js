// chrome.bookmarks.BookmarkTreeNode has no reliable "last modified" field, so the
// extension tracks its own per-native-ID modified timestamp, sent in every `event`/`snapshot`
// message and used by the host as the last-write-wins comparison key.
const STORAGE_KEY = "browserSyncLastModified";

async function getMap() {
  const stored = await chrome.storage.local.get(STORAGE_KEY);
  return stored[STORAGE_KEY] || {};
}

// Exposed so buildSnapshot() can read the whole map ONCE for a full-tree snapshot instead of
// once per node. With a few hundred bookmarks, one-storage-call-per-node meant a few hundred
// sequential chrome.storage.local round-trips — slow enough to plausibly get the MV3 service
// worker killed mid-build, sending an incomplete snapshot that made the host think a large
// number of real bookmarks had been deleted locally (and it would delete them for real).
export async function getAll() {
  return getMap();
}

export async function touch(nativeId, whenIso = new Date().toISOString()) {
  const map = await getMap();
  map[nativeId] = whenIso;
  await chrome.storage.local.set({ [STORAGE_KEY]: map });
  return whenIso;
}

export async function get(nativeId) {
  const map = await getMap();
  return map[nativeId] || new Date(0).toISOString();
}

export async function remove(nativeId) {
  const map = await getMap();
  if (nativeId in map) {
    delete map[nativeId];
    await chrome.storage.local.set({ [STORAGE_KEY]: map });
  }
}
