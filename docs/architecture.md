# Architecture

BrowserSync keeps bookmarks in sync between Chrome and Edge **on the same Windows machine**.
There is no cloud account, no relay server, and no native-messaging host registration — just a
local WebSocket connection between a shared browser extension and a small .NET host process.

```
 Chrome  --- ws://127.0.0.1:8787/ws ---\
                                        >---  BrowserSync.Host (Kestrel + SQLite)
 Edge    --- ws://127.0.0.1:8787/ws ---/
```

## Why a local host instead of native messaging?

Native messaging requires a native-messaging-host manifest registered in the Windows registry,
separately for Chrome and Edge, listing the extension's ID as an allowed origin. It works, but
it's fiddly to set up and easy to misconfigure. A plain WebSocket to `127.0.0.1` needs none of
that — the extension just needs `bookmarks`, `storage`, and `alarms` permissions.

## Why a canonical store instead of directly relaying events?

Chrome and Edge each assign their own native bookmark IDs (`chrome.bookmarks` node IDs) — the
same bookmark has a *different* ID in each browser, and those IDs are never shared or stable
across a reinstall. The host can't just forward "native ID 42 changed" from one browser to the
other, because native ID 42 means nothing to the other browser.

Instead, the host maintains:
- **Canonical bookmarks** (`CanonicalBookmark`) — one row per real-world bookmark/folder, keyed
  by a host-assigned GUID that's stable regardless of which browser reports it.
- **Client mappings** (`ClientBookmarkMapping`) — for each connected client (browser install),
  which native ID currently corresponds to which canonical GUID.
- **Tombstones** (`Tombstone`) — a delete record kept for a retention window so a client that
  was offline when something was deleted learns about the delete instead of resurrecting the
  item next time it reconciles.

Every change is expressed in terms of the canonical GUID, then translated into each target
client's own native IDs immediately before being sent — see `SyncEngine.BuildCommandForClientAsync`.

## Two sync paths

1. **Real-time events** (`SyncEngine.ApplyEventAsync`) — the extension forwards
   `chrome.bookmarks.on*` events as they happen. Fast, but can miss events if the host was down
   or a browser's service worker was suspended mid-change.
2. **Periodic reconciliation** (`SyncEngine.ReconcileAsync`) — each client periodically (and on
   every connect) sends its *entire* flattened bookmark tree. `BookmarkTreeDiffer` compares it
   against canonical state to catch anything the real-time path missed. This is the safety net
   that makes the whole system eventually consistent even after a crash or an offline period.

Conflicts (the same node edited on both sides) are resolved by **last-write-wins**: whichever
side has the newer client-supplied timestamp wins; a tie favors the existing canonical value.
Deletes are unconditional ("delete wins") rather than subject to LWW — simpler and safer than
trying to arbitrate an edit-vs-delete race.

## Deletions are asymmetric on purpose

Adds and changes can be taken from either sync path safely. Deletions cannot, because the two
paths carry signals of very different quality:

- An explicit `onRemoved` event is the browser stating a fact. It's trustworthy, so it's applied
  automatically — and it's made *durable* (a persistent queue in the extension, flushed on
  connect) so it can't be lost to service-worker teardown or host downtime.
- A snapshot omitting an item is only evidence of deletion *if the snapshot is complete*, which
  cannot be assumed. A truncated snapshot is indistinguishable from a mass deletion, and reading
  it as one previously destroyed large batches of real bookmarks.

So absence-inference never deletes anything by itself. It passes through
`SnapshotCompletenessGuard` (implausibly incomplete snapshots are ignored for this purpose
entirely), then a two-pass confirmation, and finally lands as a user-reviewed list rather than an
action. See the README's "How deletions work" for the operational view.

## First-run merge

Both browsers normally already hold most of the same bookmarks before BrowserSync ever runs, so
adoption cannot assume "this client has an item I don't know about" means "this is a new item".
When a snapshot node has no mapping yet, `SyncEngine.FindUnclaimedContentMatchAsync` first looks
for an existing canonical item that is evidently the same thing — same canonical parent, same
kind, same title, same URL — and which this client hasn't already claimed. If found, the client
is simply mapped to it; no new canonical item, no fan-out.

Because adoption runs parent-before-child, matching a folder means its children are then compared
within that same shared folder, so an entire overlapping subtree collapses into one identity
rather than just its root.

Without this, each browser's copy of the same folder became a *separate* canonical item: edits to
one never reached the other (they were unrelated items), and each was pushed to the other browser
as a brand-new folder. The visible symptom was "Mobile bookmarks > Raspberry PI doesn't sync"
together with runaway duplication, and a canonical store holding exactly double the real number
of items.

The "already claimed" check is what keeps this from over-merging: two genuinely identical
bookmarks side by side in one folder stay two items, because the second finds the canonical item
already taken by the first.

## Ordering

A move event describes one node only. Dragging B above A fires an event for B; A silently shifts
from index 0 to 1 with no event at all. So `SyncEngine.ApplyOrderedMoveAsync` renumbers the whole
sibling list on every move, keeping canonical order identical to what the browser displays.
(Those renumbered siblings are not fanned out — replaying the single move on the other browser
shifts its siblings the same way.)

Reconciliation deliberately does **not** correct a pure ordering difference back to the client
that reported it. The client is describing the order the user is currently looking at; pushing
back would undo their reorder, and since applying a correction fires fresh move events, the two
ends could trade moves indefinitely. A change of *folder* is still resolved by last-write-wins;
ordering alone simply settles on whatever reconciled most recently, which converges.

## Known v1 limitations
- A brand-new, deeply nested folder structure created entirely while a browser was offline may
  take more than one reconciliation cycle to fully materialize on the other side.
- If a client stays offline longer than the tombstone retention window (default 30 days), a
  delete it missed could resurrect a bookmark on its next reconciliation.

See `docs/protocol.md` for the wire format between the extension and the host.
