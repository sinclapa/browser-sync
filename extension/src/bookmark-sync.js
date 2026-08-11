import { send } from "./ws-connection.js";
import { touch, getAll as getAllModified, remove as removeModified } from "./modified-tracker.js";
import { enqueue as enqueueDeletion, getAll as getQueuedDeletions, clear as clearQueuedDeletions } from "./pending-deletions.js";
import { isApplyingRemote } from "./remote-apply-state.js";

const ROLE_BY_ROOT_INDEX = ["bookmarksBar", "other", "mobile"];

// Chromium does NOT guarantee the Bookmarks Bar / Other / Mobile roots keep native IDs
// "1"/"2"/"3" — on a profile with enough bookmark history behind it, "Other"/"Mobile" can end
// up with arbitrary IDs (observed in practice: "30" and "164"). The only reliable way to find
// them is positionally: they are always exactly the super-root's children, in this fixed
// order. This set is learned from the most recent buildSnapshot() call and used to keep root
// folders out of regular create/rename/move/delete event forwarding. Seeded with the common
// "1"/"2"/"3" guess so the very first events before any snapshot has run still get filtered in
// the common case; buildSnapshot() replaces it with the real IDs as soon as it runs (which
// happens immediately on every connect).
let knownRootNativeIds = new Set(["1", "2", "3"]);

export function registerBookmarkListeners() {
  chrome.bookmarks.onCreated.addListener(onCreated);
  chrome.bookmarks.onRemoved.addListener(onRemoved);
  chrome.bookmarks.onChanged.addListener(onChanged);
  chrome.bookmarks.onMoved.addListener(onMoved);
  chrome.bookmarks.onChildrenReordered.addListener(onChildrenReordered);
}

async function onCreated(id, node) {
  if (isApplyingRemote() || knownRootNativeIds.has(id)) return;
  const timestamp = await touch(id);
  await send("event", {
    op: "created",
    nativeId: id,
    parentNativeId: node.parentId ?? null,
    index: node.index ?? 0,
    title: node.title ?? "",
    url: node.url ?? null,
    timestamp,
  });
}

async function onRemoved(id) {
  if (isApplyingRemote() || knownRootNativeIds.has(id)) return;
  const timestamp = new Date().toISOString();
  await removeModified(id);

  // Persist BEFORE attempting to send. This is the only trustworthy delete signal we ever get,
  // and it usually fires with no live socket (MV3 tears the worker down every ~30s). Dropping
  // it left the host inferring deletion from snapshot absence instead — see pending-deletions.js.
  await enqueueDeletion(id, timestamp);
  const delivered = await send("event", { op: "removed", nativeId: id, timestamp });
  if (delivered) await clearQueuedDeletions([id]);
}

/// Delivers deletes that happened while disconnected. Called on every connect BEFORE the
/// snapshot is sent, so the host has already applied them — meaning those items are legitimately
/// absent from the snapshot that follows, rather than looking like an unexplained disappearance.
export async function flushPendingDeletions() {
  const queued = await getQueuedDeletions();
  if (queued.length === 0) return;

  const delivered = [];
  for (const { nativeId, timestamp } of queued) {
    if (!(await send("event", { op: "removed", nativeId, timestamp }))) break; // socket gone; keep the rest for next time
    delivered.push(nativeId);
  }

  if (delivered.length > 0) await clearQueuedDeletions(delivered);
}

async function onChanged(id, changeInfo) {
  if (isApplyingRemote() || knownRootNativeIds.has(id)) return;
  const timestamp = await touch(id);
  await send("event", {
    op: "changed",
    nativeId: id,
    title: changeInfo.title ?? null,
    url: changeInfo.url ?? null,
    timestamp,
  });
}

async function onMoved(id, moveInfo) {
  if (isApplyingRemote() || knownRootNativeIds.has(id)) return;
  const timestamp = await touch(id);
  await send("event", {
    op: "moved",
    nativeId: id,
    parentNativeId: moveInfo.parentId,
    index: moveInfo.index,
    timestamp,
  });
}

async function onChildrenReordered(parentId, reorderInfo) {
  if (isApplyingRemote()) return;
  // onChildrenReordered reports a whole folder's children being reindexed at once, not a
  // single node — synthesize one `reordered` (position-only) event per child so the host's
  // SyncEngine can treat it exactly like an ordinary move.
  const childIds = reorderInfo.childIds ?? [];
  for (let index = 0; index < childIds.length; index++) {
    const nativeId = childIds[index];
    if (knownRootNativeIds.has(nativeId)) continue;
    const timestamp = await touch(nativeId);
    await send("event", { op: "reordered", nativeId, parentNativeId: parentId, index, timestamp });
  }
}

export async function buildSnapshot() {
  const roots = await chrome.bookmarks.getTree();
  // roots is [superRoot]; superRoot.children are the three permanent folders, always in this
  // fixed order (Bookmarks Bar, Other, Mobile) — positionally reliable even though their
  // native IDs are not.
  const permanentFolders = roots[0]?.children ?? [];
  knownRootNativeIds = new Set(permanentFolders.map((node) => node.id));

  const flatNodes = [];
  const walk = (node, role) => {
    flatNodes.push({ node, role });
    for (const child of node.children ?? []) walk(child, null);
  };
  permanentFolders.forEach((node, i) => walk(node, ROLE_BY_ROOT_INDEX[i] ?? null));

  // One bulk read for the whole tree instead of one chrome.storage.local round-trip per node —
  // with a few hundred bookmarks, the per-node version meant a few hundred sequential awaits,
  // slow enough to plausibly get the service worker killed mid-build.
  const modifiedMap = await getAllModified();
  const snapshotNodes = [];
  for (const { node, role } of flatNodes) {
    snapshotNodes.push({
      nativeId: node.id,
      parentNativeId: node.parentId ?? null,
      kind: node.url ? "bookmark" : "folder",
      title: node.title ?? "",
      url: node.url ?? null,
      index: node.index ?? 0,
      role,
      lastLocalModified: modifiedMap[node.id] || new Date(0).toISOString(),
    });
  }
  return snapshotNodes;
}
