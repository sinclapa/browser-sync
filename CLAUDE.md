# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Two-way bookmark sync between Chrome and Edge on a single Windows machine. A Manifest V3
extension (one codebase, loaded into both browsers) talks to a local .NET host over
`ws://127.0.0.1:8787/ws`. No cloud, no accounts, no native-messaging manifests.

## Commands

```powershell
dotnet build BrowserSync.sln
dotnet test src\BrowserSync.Core.Tests          # sync/diff/protocol logic
dotnet test src\BrowserSync.Host.Tests          # DI wiring, connection registry

# One class, or one test
dotnet test src\BrowserSync.Core.Tests --filter "ReorderTests"
dotnet test src\BrowserSync.Core.Tests --filter "ReorderingAndReorderingBack"

.\scripts\deploy.ps1                            # publish + install to %LOCALAPPDATA% + restart
.\scripts\deploy.ps1 -StartWithWindows          # also register at login
dotnet run --project src\BrowserSync.Host       # run from source instead
```

**A host started with `dotnet run` blocks the next build.** It holds the repo's build output
open, and the build fails with an opaque `MSB3027`/`MSB3021` file-lock error rather than
anything naming the cause. Stop it first:

```powershell
Get-Process BrowserSync.Host -ErrorAction SilentlyContinue | Stop-Process -Force
```

A host installed by `deploy.ps1` runs from `%LOCALAPPDATA%\BrowserSync\app` instead, so it does
*not* lock the repo and can be left running while you build. `deploy.ps1` stops it regardless,
since it overwrites that install.

Extension changes require a reload in both browsers (the reload icon on the extensions page).
Host-only changes just need the host restarted.

## Architecture

`docs/architecture.md` and `docs/protocol.md` cover this properly; the essentials:

**Why a canonical store exists.** Chrome and Edge assign *different* native bookmark IDs to the
same bookmark, and those IDs aren't stable across a reinstall. So the host can't relay "native id
42 changed" — 42 means nothing to the other browser. It keeps `CanonicalBookmark` (host-assigned
GUID, the real identity) plus `ClientBookmarkMapping` (per-client native ID), and translates into
each target's own IDs at the boundary, in `SyncEngine.BuildCommandForClientAsync`.

**Two inbound paths.** Real-time events (`SyncEngine.ApplyEventAsync`) and full-tree
reconciliation (`ReconcileAsync` + `BookmarkTreeDiffer`, run on every connect). The second is the
catch-up net for anything the first missed.

`SyncEngine` is scoped per message; the DbContext is too. `Core` deliberately has no ASP.NET or
WinForms dependency.

## Invariants — do not "simplify" these back

Each of these looks like over-engineering until you know what it's for. Every one replaced an
obvious approach that was tried first and failed; two of them destroyed real bookmarks.

| Invariant | Why it's like that |
|---|---|
| Root folders are located **positionally** (always the super-root's children, in fixed order) and each client's native IDs for them are learned from `BookmarkSnapshotNode.Role` | Chromium does **not** guarantee the permanent folders keep native IDs `1`/`2`/`3`. A real Edge profile had `30` and `164`, so Other/Mobile Bookmarks could never sync at all. |
| Deletion acts **only** on an explicit `onRemoved` event, made durable by `extension/src/pending-deletions.js` (persisted before sending, flushed on connect, cleared only on confirmed delivery) | `onRemoved` usually fires with no live socket, since MV3 tears the worker down every ~30s. Fire-and-forget lost most deletes. |
| Absence from a snapshot is logged and **never acted on** (`SnapshotCompletenessGuard`, plus two-pass confirmation) | A truncated snapshot is indistinguishable from a mass deletion. Treating it as one deleted large batches of real bookmarks, twice. |
| Ordering is transmitted as a folder's **whole child order** (`SyncCommandOpKind.Reorder`), applied front-to-back | `chrome.bookmarks.move(index)` and `onMoved`'s reported index disagree (off-by-one moving down), and the two browsers' folders needn't hold the same items — so an absolute index lands in the wrong place. |
| A move renumbers the **entire sibling list** (`SyncEngine.ApplyOrderedMoveAsync`) | A move event names one node; its siblings shift silently with no event. Storing only the moved node left duplicate indices, and the host then "corrected" the difference by undoing the user's reorder. |
| First-run adoption matches parent+title+URL against unclaimed canonical items before creating one (`FindUnclaimedContentMatchAsync`) | Both browsers already share most bookmarks. Without it each browser's copy became a *separate* canonical item: edits never crossed, and everything duplicated. Presents as "doesn't sync". |

## Gotchas when changing things

- `PendingDeletionTracker` and the other stores **must** stay DI singletons — they carry state
  across the per-message scoped `SyncEngine`.
- `BrowserSync.Host.Tests/ServiceRegistrationTests` resolves services through the real container
  on purpose: a missing `TimeProvider` registration once crashed every snapshot at runtime while
  every unit test passed, because the tests constructed `SyncEngine` directly.
- Tests driving two engines must mirror production scoping — fan-out runs on the *same* engine
  that applied the change. Calling it on a second engine reads that context's stale cached state
  and produces wrong results that look like product bugs.
- Publishing needs `IncludeNativeLibrariesForSelfExtract` (already in `scripts/deploy.ps1`);
  without it the single-file exe builds fine and dies at runtime when SQLite opens the DB.

## Logs

`%LOCALAPPDATA%\BrowserSync\`:

- `logs\activity-*.log` — one line per change: full folder path, direction (`Chrome→Edge`, or a
  bare browser name for a deletion showing where it was deleted from), and `before → after`.
  Written to be read start-to-finish and undone by hand; never rotated or trimmed.
- `logs\browsersync-*.log` — diagnostics only. EF Core and ASP.NET are pinned to `Warning`
  deliberately: at `Information` they emitted megabytes of SQL per hour and buried everything.
- `browsersync.db` — canonical state. Safe to delete; it rebuilds from both browsers on next
  connect (and that is the standard fix for a corrupted sync state).
