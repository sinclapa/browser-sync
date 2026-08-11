import { withRemoteApply } from "./remote-apply-state.js";
import { touch as touchModified } from "./modified-tracker.js";
import { send } from "./ws-connection.js";

export async function applyCommand(message) {
  const created = [];
  await withRemoteApply(async () => {
    for (const op of message.ops ?? []) {
      try {
        await applyOp(op, created);
      } catch (err) {
        console.error("[BrowserSync] failed to apply op", op, err);
      }
    }
  });

  if (created.length > 0) {
    await send("ack", { batchId: message.batchId, created });
  }
}

async function applyOp(op, created) {
  switch (op.op) {
    case "create": {
      const params = { parentId: op.parentNativeId, title: op.title ?? "", index: op.index };
      if (op.url) params.url = op.url;
      const node = await chrome.bookmarks.create(params);
      await touchModified(node.id);
      created.push({ canonicalId: op.canonicalId, nativeId: node.id });
      break;
    }
    case "update": {
      if (!op.nativeId) return;
      const changes = {};
      if (op.title !== undefined && op.title !== null) changes.title = op.title;
      if (op.url !== undefined && op.url !== null) changes.url = op.url;
      if (Object.keys(changes).length > 0) await chrome.bookmarks.update(op.nativeId, changes);
      await touchModified(op.nativeId);
      break;
    }
    case "move": {
      if (!op.nativeId) return;
      await chrome.bookmarks.move(op.nativeId, { parentId: op.parentNativeId, index: op.index });
      await touchModified(op.nativeId);
      break;
    }
    case "reorder": {
      const orderedIds = op.orderedNativeIds ?? [];
      if (!op.parentNativeId || orderedIds.length < 2) return;

      // Placed front-to-back, one index at a time, rather than trusting a single absolute
      // index. chrome.bookmarks.move does not interpret an index the same way onMoved reports
      // one (moving down within a folder is off by one), but placing item i at index i once
      // items 0..i-1 are already settled lands correctly under either interpretation — and any
      // item this browser happens to have that isn't in the list just drifts to the end
      // instead of throwing the rest out of position.
      for (let index = 0; index < orderedIds.length; index++) {
        const nativeId = orderedIds[index];
        try {
          await chrome.bookmarks.move(nativeId, { parentId: op.parentNativeId, index });
          await touchModified(nativeId);
        } catch (err) {
          // One stale/removed ID must not abandon the rest of the ordering.
          console.warn("[BrowserSync] reorder: could not place", nativeId, err);
        }
      }
      break;
    }
    case "remove": {
      if (!op.nativeId) return;
      await chrome.bookmarks.removeTree(op.nativeId).catch(() => chrome.bookmarks.remove(op.nativeId));
      break;
    }
    default:
      break;
  }
}
