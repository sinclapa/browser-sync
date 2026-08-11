# BrowserSync

Keeps bookmarks in sync between Chrome and Edge **on the same Windows PC** — no cloud account,
no relay server. A shared browser extension talks to a small local .NET host over a WebSocket
on `127.0.0.1`.

See [`docs/architecture.md`](docs/architecture.md) for how it works and
[`docs/protocol.md`](docs/protocol.md) for the wire format.

## Prerequisites

- .NET 8 SDK (or newer — an installed .NET 9 SDK builds `net8.0`-targeted projects fine)
- Chrome and/or Edge with Developer mode enabled for extensions

## Running the host

```
dotnet run --project src\BrowserSync.Host
```

This starts a WebSocket server at `ws://127.0.0.1:8787/ws`, creates its SQLite database at
`%LOCALAPPDATA%\BrowserSync\browsersync.db`, writes logs to
`%LOCALAPPDATA%\BrowserSync\logs\`, and shows a tray icon with:
- a live count of what each browser last reported (e.g. `Chrome: 118 bookmarks, 21 folders`), so
  the two can be compared at a glance — differing numbers mean they really are out of step
- **Sync now** — forces an immediate reconciliation with every connected browser
- **Start with Windows** — toggles an HKCU Run-key entry so the host starts on login
- **Open logs** — opens the log folder
- **Remove detected duplicates...** — see [Duplicate bookmarks](#duplicate-bookmarks) below
- **Quit**

## Loading the extension

The same `extension\` folder is loaded, unpacked, into both browsers:

1. `chrome://extensions` (or `edge://extensions`) → enable **Developer mode** → **Load unpacked**
   → select the `extension\` folder.
2. Repeat in the other browser, pointing at the *same* folder.
3. Open each browser's service worker DevTools console (via the extension's "service worker"
   link on the extensions page) to watch connection/sync logs.

With the host already running, both extensions should connect within a second or two.

## Manual end-to-end testing — protect your real bookmarks first

Do **not** test directly against your everyday bookmarks. Before any manual testing:

1. **Back up first.** In both browsers: bookmarks manager → ⋮ menu → **Export bookmarks**, and
   save the HTML file somewhere outside this repo (e.g. `%USERPROFILE%\Desktop\bookmark-backups\`).
2. **Test inside a dedicated folder.** In both browsers, create a top-level folder named
   `BrowserSync-Test`, and do all create/rename/move/delete testing *inside that folder only*.
3. Run through the scenarios: create/rename/move/delete propagation in both directions;
   editing the same bookmark in both browsers within the same second (should converge, not
   diverge); killing the host mid-edit and confirming reconciliation catches up on restart;
   deleting a bookmark while the other browser is fully closed, then reopening it and confirming
   the delete propagates instead of the item reappearing.
4. When done, delete the `BrowserSync-Test` folder in both browsers (the delete will itself
   sync) and confirm bookmarks match your pre-test export. If anything looks off, re-import the
   backup HTML file to restore your original bookmarks exactly.

## Duplicate bookmarks

Every time a browser sends a snapshot (on connect, and every periodic reconciliation), the host
scans that browser's *raw* bookmark tree for exact duplicates — same parent folder, same title,
same URL — and writes findings to `%LOCALAPPDATA%\BrowserSync\logs\duplicates-<browser>-<clientId>.json`.
This is the only way to find them: duplicates aren't tracked in the canonical store (that's what
made them duplicates in the first place), so they can only be seen in what a browser actually
reports.

To act on them: check the log for a `Found N duplicate bookmark group(s)` warning (or the
`duplicates-*.json` files) to see what's flagged, then click **Remove detected duplicates...**
in the tray menu. For each duplicate group it keeps the oldest copy and sends a `remove` command
for the rest to the browser that reported them — real deletions, so review the log first. Click
**Sync now** beforehand if you want a fresh scan.

## Activity log

`%LOCALAPPDATA%\BrowserSync\logs\activity-yyyyMMdd.log` records one line per change, with enough
detail to reverse any of them by hand:

```
2026-08-12 00:15:08  Chrome→Edge    NEWFOLDER  Mobile bookmarks/Raspberry PI
2026-08-12 00:15:08  Chrome→Edge    ADD        Mobile bookmarks/Raspberry PI/Fan SHIM  [https://shop.pimoroni.com/products/fan-shim]
2026-08-12 00:15:08  Edge→Chrome    RENAME     Bookmarks bar/Weather  "Weather" → "Met Office"
2026-08-12 00:15:08  Chrome→Edge    MOVE       Other bookmarks/Work/Docs  Bookmarks bar/Docs → Other bookmarks/Work/Docs
2026-08-12 00:15:08  Chrome→Edge    REORDER    Mobile bookmarks/Raspberry PI  [Fan SHIM, Samsung, FIX] → [Fan SHIM, FIX, Samsung]
2026-08-12 00:15:08  Edge→Chrome    DELETE     Mobile bookmarks/Raspberry PI/PiJuice HAT  [https://uk.pi-supply.com/products/pijuice-standard]
```

- **`Chrome→Edge`** is the direction the change travelled. A lone browser name (no arrow) means
  nothing else was connected at the time — for a `DELETE`, that name is the browser it was
  deleted from.
- **Paths are full**, so you know exactly which folder to look in.
- **`before → after`** carries whatever an undo needs: the old title for a rename, the old
  location for a move, the old order for a reorder.
- **Deletions record the URL**, because after the fact there is nothing left to look it up from.
  Deleting a folder logs one line per item lost, not just the folder.

This file is never rotated or trimmed — it's the record of what happened to your bookmarks.
The separate `browsersync-*.log` is diagnostics only (and no longer contains the EF SQL firehose
that used to bury everything).

## How deletions work

Deletions get special treatment because they're the only destructive operation here, and getting
them wrong is unrecoverable. There are two possible signals that something was deleted, and they
are **not** equally trustworthy:

| Signal | Trustworthy? | How it's treated |
|---|---|---|
| `chrome.bookmarks.onRemoved` fired | **Yes** — the browser explicitly saying so | Applied automatically, propagates to the other browser |
| Item absent from a snapshot | **No** — inference from a negative, only valid if the snapshot is complete | Never applied automatically; review-only |

The reliable signal is made durable rather than best-effort: `onRemoved` writes to a persistent
queue (`extension/src/pending-deletions.js`) *before* trying to send, so a delete survives the
MV3 service worker being torn down and the host being offline. The queue is flushed on every
connect, before the snapshot is sent, and entries are cleared only once confirmed written to an
open socket. So genuine deletions propagate promptly without depending on absence-inference at all.

Absence-inference remains only as a backstop for cases the queue can't cover (a profile restore,
an extension reinstall). It is deliberately hobbled, because misreading a truncated snapshot as a
mass deletion previously destroyed large batches of real bookmarks:

1. **Completeness guard** — a snapshot missing an implausible share (>20%) of what that client was
   known to have is treated as truncated. Adds and changes from it still apply; nothing at all is
   concluded from what it omits. (A partial snapshot can only omit items, never invent them.)
2. **Two-pass confirmation** — an item must be absent on two separate reconciliations to even
   become a candidate.
3. **Never acted on** — surviving candidates are written to
   `%LOCALAPPDATA%\BrowserSync\logs\pending-local-deletions-<browser>-<clientId>.json` and logged
   as a warning, and that is all that happens to them. Nothing deletes on the strength of an
   item merely being absent.

## Duplicate bookmarks

```
dotnet test src\BrowserSync.Core.Tests
```

## Project layout

```
src\BrowserSync.Core\        Protocol DTOs, EF Core schema, sync/diff/conflict-resolution logic
src\BrowserSync.Host\        Kestrel WebSocket server + WinForms tray app
src\BrowserSync.Core.Tests\  xUnit tests for the sync engine
extension\                   Manifest V3 extension (shared between Chrome and Edge)
```
