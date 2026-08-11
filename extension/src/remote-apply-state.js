// Shared between bookmark-sync.js and apply-remote.js so the former can swallow the
// chrome.bookmarks.* events the latter causes while applying a host-driven command batch,
// instead of echoing them straight back to the host as new "user" changes.
let applying = false;

export function isApplyingRemote() {
  return applying;
}

export async function withRemoteApply(fn) {
  applying = true;
  try {
    await fn();
  } finally {
    applying = false;
  }
}
