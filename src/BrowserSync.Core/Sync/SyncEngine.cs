using BrowserSync.Core.Data;
using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BrowserSync.Core.Sync;

/// <summary>
/// Owns the canonical bookmark store and the two ways a client's changes reach it:
/// real-time events (<see cref="ApplyEventAsync"/>) and periodic full-tree reconciliation
/// (<see cref="ReconcileAsync"/>). Also resolves canonical changes into per-client, native-ID
/// commands via <see cref="BuildCommandForClientAsync"/> — every client sees the same canonical
/// bookmark tree, but each one addresses it with its own chrome.bookmarks IDs.
/// </summary>
public sealed class SyncEngine(
    BrowserSyncDbContext db,
    TimeProvider timeProvider,
    PendingDeletionTracker? pendingDeletionsOverride = null,
    IActivityLog? activityLog = null)
{
    // In production this is a DI singleton so the debounce in ProcessClientDeletedLocallyAsync
    // works across the fresh SyncEngine instance created for every message. The fallback here
    // exists purely so tests (and any other caller) that `new SyncEngine(db, timeProvider)`
    // directly still compile and behave sensibly — reusing the SAME SyncEngine/tracker instance
    // across two ReconcileAsync calls still exercises the two-pass confirmation correctly.
    private readonly PendingDeletionTracker pendingDeletions = pendingDeletionsOverride ?? new PendingDeletionTracker();

    private readonly IActivityLog activity = activityLog ?? NullActivityLog.Instance;

    /// <summary>Names the browser a change came from, and the other one it therefore goes to.
    /// Scoped to one Chrome and one Edge, so "the others" is at most a single browser.</summary>
    private async Task<(string Source, string? Target)> DescribeDirectionAsync(Guid sourceClientId)
    {
        var source = await db.Clients.FindAsync(sourceClientId);
        var target = await db.Clients.FirstOrDefaultAsync(c => c.Id != sourceClientId);
        return (source?.BrowserKind.ToString() ?? "?", target?.BrowserKind.ToString());
    }

    private async Task RecordAsync(Guid sourceClientId, ActivityKind kind, string path, string? url = null, string? before = null, string? after = null)
    {
        var (source, target) = await DescribeDirectionAsync(sourceClientId);
        activity.Record(new ActivityRecord
        {
            Kind = kind,
            SourceBrowser = source,
            TargetBrowser = target,
            Path = path,
            Url = url,
            Before = before,
            After = after,
        });
    }

    public async Task EnsureClientAsync(Guid clientId, BrowserKind browserKind)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var client = await db.Clients.FindAsync(clientId);
        if (client is null)
        {
            db.Clients.Add(new Client { Id = clientId, BrowserKind = browserKind, LastSeenUtc = now });
        }
        else
        {
            client.LastSeenUtc = now;
            client.BrowserKind = browserKind;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Idempotently ensures the three well-known root folders are mapped for this
    /// client, using whichever native IDs THIS client's snapshot reports for them via
    /// <see cref="BookmarkSnapshotNode.Role"/> — Chromium does not guarantee the permanent
    /// folders keep native IDs "1"/"2"/"3"; that depends on the profile's bookmark history.</summary>
    public async Task EnsureRoleRootMappingsAsync(Guid clientId, IReadOnlyList<BookmarkSnapshotNode> nodes)
    {
        foreach (var node in nodes)
        {
            var canonicalRootId = WellKnownRoots.CanonicalIdForRole(node.Role);
            if (canonicalRootId is null)
                continue;

            var exists = await db.ClientBookmarkMappings.AnyAsync(m => m.ClientId == clientId && m.CanonicalId == canonicalRootId);
            if (!exists)
                db.ClientBookmarkMappings.Add(new ClientBookmarkMapping { ClientId = clientId, CanonicalId = canonicalRootId.Value, NativeId = node.NativeId });
        }

        await db.SaveChangesAsync();
    }

    // ---- Real-time event path ----

    public async Task<EventApplyResult> ApplyEventAsync(Guid clientId, BookmarkEventMessage evt)
    {
        return evt.Op switch
        {
            BookmarkEventOp.Created => await HandleCreateAsync(clientId, evt),
            BookmarkEventOp.Changed or BookmarkEventOp.Moved or BookmarkEventOp.Reordered => await HandleUpdateAsync(clientId, evt),
            BookmarkEventOp.Removed => await HandleRemoveAsync(clientId, evt),
            _ => EventApplyResult.None,
        };
    }

    private async Task<EventApplyResult> HandleCreateAsync(Guid clientId, BookmarkEventMessage evt)
    {
        var already = await db.ClientBookmarkMappings.FirstOrDefaultAsync(m => m.ClientId == clientId && m.NativeId == evt.NativeId);
        if (already is not null)
            return await HandleUpdateCoreAsync(clientId, already.CanonicalId, evt); // defensive: duplicate create treated as update

        var parentCanonicalId = await ResolveCanonicalParentAsync(clientId, evt.ParentNativeId);
        if (parentCanonicalId is null)
            return EventApplyResult.None; // parent not known yet; the next reconciliation pass will pick this up

        // chrome.bookmarks nodes only carry a `url` when they are an actual bookmark, never a folder.
        var kind = evt.Url is not null ? BookmarkKind.Bookmark : BookmarkKind.Folder;
        var canonical = new CanonicalBookmark
        {
            Id = Guid.NewGuid(),
            ParentId = parentCanonicalId,
            Kind = kind,
            Title = evt.Title ?? string.Empty,
            Url = evt.Url,
            SortIndex = evt.Index,
            LastModifiedUtc = evt.Timestamp,
            LastModifiedByClientId = clientId,
        };
        db.CanonicalBookmarks.Add(canonical);
        db.ClientBookmarkMappings.Add(new ClientBookmarkMapping { ClientId = clientId, CanonicalId = canonical.Id, NativeId = evt.NativeId });
        await db.SaveChangesAsync();

        await RecordAsync(
            clientId,
            kind == BookmarkKind.Folder ? ActivityKind.NewFolder : ActivityKind.Add,
            await BookmarkPath.OfAsync(db, canonical),
            canonical.Url);

        return new EventApplyResult { ForOthers = [new PendingChange(canonical.Id, PendingChangeKind.Created)] };
    }

    private async Task<EventApplyResult> HandleUpdateAsync(Guid clientId, BookmarkEventMessage evt)
    {
        var mapping = await db.ClientBookmarkMappings.FirstOrDefaultAsync(m => m.ClientId == clientId && m.NativeId == evt.NativeId);
        if (mapping is null)
            return EventApplyResult.None; // host doesn't know this node yet; reconciliation will adopt it as NewFromClient

        return await HandleUpdateCoreAsync(clientId, mapping.CanonicalId, evt);
    }

    private async Task<EventApplyResult> HandleUpdateCoreAsync(Guid clientId, Guid canonicalId, BookmarkEventMessage evt)
    {
        if (WellKnownRoots.IsRoot(canonicalId))
            return EventApplyResult.None;

        var canonical = await db.CanonicalBookmarks.FindAsync(canonicalId);
        if (canonical is null)
            return EventApplyResult.None; // deleted concurrently

        var isPositionEvent = evt.Op is BookmarkEventOp.Moved or BookmarkEventOp.Reordered;

        if (ConflictResolver.Resolve(canonical.LastModifiedUtc, evt.Timestamp) != ConflictResolver.Winner.Incoming)
        {
            var correction = isPositionEvent
                ? await BuildMoveCorrectionAsync(clientId, canonical, evt.NativeId)
                : new SyncCommandOp { Op = SyncCommandOpKind.Update, CanonicalId = canonicalId, NativeId = evt.NativeId, Title = canonical.Title, Url = canonical.Url };
            return new EventApplyResult { CorrectionForSender = correction };
        }

        var changes = new List<PendingChange>();
        if (!isPositionEvent)
        {
            var contentChanged = (evt.Title is not null && evt.Title != canonical.Title) || evt.Url != canonical.Url;
            if (!contentChanged)
                return EventApplyResult.None;

            var previousTitle = canonical.Title;
            var previousUrl = canonical.Url;
            var folder = await BookmarkPath.OfParentAsync(db, canonical.ParentId);

            canonical.Title = evt.Title ?? canonical.Title;
            canonical.Url = evt.Url;
            changes.Add(new PendingChange(canonicalId, PendingChangeKind.ContentChanged));

            if (previousTitle != canonical.Title)
                await RecordAsync(clientId, ActivityKind.Rename, $"{folder}/{canonical.Title}", canonical.Url, $"\"{previousTitle}\"", $"\"{canonical.Title}\"");
            else
                await RecordAsync(clientId, ActivityKind.Rename, $"{folder}/{canonical.Title}", null, previousUrl, canonical.Url);
        }
        else
        {
            var parentCanonicalId = await ResolveCanonicalParentAsync(clientId, evt.ParentNativeId);
            if (parentCanonicalId is null || (parentCanonicalId == canonical.ParentId && evt.Index == canonical.SortIndex))
                return EventApplyResult.None;

            // Captured before the move, because that's what an undo has to put back.
            var movedBetweenFolders = parentCanonicalId != canonical.ParentId;
            var previousFolder = await BookmarkPath.OfParentAsync(db, canonical.ParentId);
            var previousOrder = movedBetweenFolders ? null : await SiblingTitlesAsync(canonical.ParentId);

            var newOrder = await ApplyOrderedMoveAsync(canonical, parentCanonicalId.Value, evt.Index);
            changes.Add(new PendingChange(canonicalId, PendingChangeKind.PositionChanged));

            var newFolder = await BookmarkPath.OfParentAsync(db, canonical.ParentId);
            if (movedBetweenFolders)
            {
                await RecordAsync(clientId, ActivityKind.Move, $"{newFolder}/{canonical.Title}", canonical.Url,
                    $"{previousFolder}/{canonical.Title}", $"{newFolder}/{canonical.Title}");
            }
            else
            {
                await RecordAsync(clientId, ActivityKind.Reorder, newFolder, null, Describe(previousOrder), Describe(newOrder));
            }
        }

        canonical.LastModifiedUtc = evt.Timestamp;
        canonical.LastModifiedByClientId = clientId;
        await db.SaveChangesAsync();
        return new EventApplyResult { ForOthers = changes };
    }

    private async Task<List<string>> SiblingTitlesAsync(Guid? parentId) =>
        await db.CanonicalBookmarks
            .Where(b => b.ParentId == parentId)
            .OrderBy(b => b.SortIndex)
            .Select(b => b.Title)
            .ToListAsync();

    private static string Describe(IReadOnlyList<string>? titles) =>
        titles is null ? "?" : $"[{string.Join(", ", titles)}]";

    /// <summary>
    /// Moves <paramref name="moved"/> to <paramref name="newIndex"/> within
    /// <paramref name="newParentId"/>, renumbering the sibling list so canonical order matches
    /// what the browser now actually shows.
    ///
    /// This is necessary because a move event describes one node only: dragging B above A fires
    /// an event for B, while A silently shifts from index 0 to 1 with no event at all. Storing
    /// just the moved node's new index therefore left stale, duplicated indices behind (both A
    /// and B claiming index 0), which the next snapshot then reported as a difference — and the
    /// host "corrected" it by shoving the item back, undoing the user's reorder.
    ///
    /// Siblings renumbered here are deliberately NOT fanned out: applying the single move on the
    /// other browser shifts its siblings implicitly in exactly the same way.
    /// </summary>
    /// <returns>The destination folder's child titles in their new order. Returned rather than
    /// re-queried by the caller because these changes aren't saved yet — a fresh query would
    /// read the pre-move order straight back out of the database.</returns>
    private async Task<List<string>> ApplyOrderedMoveAsync(CanonicalBookmark moved, Guid newParentId, int newIndex)
    {
        var previousParentId = moved.ParentId;
        moved.ParentId = newParentId;

        var siblings = await db.CanonicalBookmarks
            .Where(b => b.ParentId == newParentId && b.Id != moved.Id)
            .OrderBy(b => b.SortIndex)
            .ToListAsync();

        siblings.Insert(Math.Clamp(newIndex, 0, siblings.Count), moved);
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].SortIndex = i;

        // The folder it left closes the gap behind it.
        if (previousParentId is not null && previousParentId != newParentId)
        {
            var formerSiblings = await db.CanonicalBookmarks
                .Where(b => b.ParentId == previousParentId)
                .OrderBy(b => b.SortIndex)
                .ToListAsync();
            for (var i = 0; i < formerSiblings.Count; i++)
                formerSiblings[i].SortIndex = i;
        }

        return siblings.Select(s => s.Title).ToList();
    }

    private async Task<SyncCommandOp?> BuildMoveCorrectionAsync(Guid clientId, CanonicalBookmark canonical, string nativeId)
    {
        var parentNative = await ResolveNativeForClientAsync(clientId, canonical.ParentId);
        if (parentNative is null)
            return null;
        return new SyncCommandOp { Op = SyncCommandOpKind.Move, CanonicalId = canonical.Id, NativeId = nativeId, ParentNativeId = parentNative, Index = canonical.SortIndex };
    }

    private async Task<EventApplyResult> HandleRemoveAsync(Guid clientId, BookmarkEventMessage evt)
    {
        var mapping = await db.ClientBookmarkMappings.FirstOrDefaultAsync(m => m.ClientId == clientId && m.NativeId == evt.NativeId);
        if (mapping is null)
            return EventApplyResult.None;
        if (WellKnownRoots.IsRoot(mapping.CanonicalId))
            return EventApplyResult.None;

        // Deletes are unconditional (delete-wins), not subject to last-write-wins against edits —
        // a simpler and safer default than trying to arbitrate an edit-vs-delete race.
        var changes = await DeleteCanonicalSubtreeAsync(mapping.CanonicalId, clientId);
        return new EventApplyResult { ForOthers = changes };
    }

    // ---- Reconciliation (full snapshot) path ----

    public async Task<ReconcileResult> ReconcileAsync(Guid clientId, BrowserKind browserKind, SnapshotMessage snapshot)
    {
        await EnsureClientAsync(clientId, browserKind);
        await EnsureRoleRootMappingsAsync(clientId, snapshot.Nodes);

        var filteredNodes = snapshot.Nodes
            .Where(n => n.Role is null)
            .ToList();

        var canonicalNodes = await db.CanonicalBookmarks.AsNoTracking().ToListAsync();
        var clientMappings = await db.ClientBookmarkMappings.Where(m => m.ClientId == clientId).AsNoTracking().ToListAsync();
        var tombstoneIds = (await db.Tombstones.Select(t => t.CanonicalId).ToListAsync()).ToHashSet();

        // Anything this snapshot still reports resets its "missing" strike, so a one-off
        // truncated/incomplete snapshot can't accumulate confirmations across unrelated gaps.
        var presentNativeIds = filteredNodes.Select(n => n.NativeId).ToHashSet();
        var trackedMappings = clientMappings.Where(m => !WellKnownRoots.IsRoot(m.CanonicalId)).ToList();
        foreach (var mapping in trackedMappings)
        {
            if (presentNativeIds.Contains(mapping.NativeId))
                pendingDeletions.ClearIfPresent(clientId, mapping.CanonicalId);
        }

        // A snapshot that has lost an implausible share of what this client was known to have is
        // almost certainly truncated rather than genuinely emptied, so nothing is concluded from
        // what it *doesn't* contain. Adds and changes below are unaffected — a partial snapshot
        // can only omit items, never invent them.
        var missingCount = trackedMappings.Count(m => !presentNativeIds.Contains(m.NativeId));
        var snapshotTooIncomplete = SnapshotCompletenessGuard.IsSuspect(trackedMappings.Count, missingCount);

        var diff = BookmarkTreeDiffer.Diff(canonicalNodes, clientMappings, filteredNodes, tombstoneIds);

        var requesterOps = new List<SyncCommandOp>();
        var pendingForOthers = new List<PendingChange>();

        await AdoptNewFromClientAsync(clientId, diff, pendingForOthers);
        await PushNewForClientAsync(clientId, diff, requesterOps);
        await ResolveChangedAsync(clientId, diff, requesterOps, pendingForOthers);
        await PushTombstonedRemovalsAsync(clientId, diff, requesterOps);
        var localDeletionCandidates = snapshotTooIncomplete
            ? []
            : await CollectLocalDeletionCandidatesAsync(clientId, diff);

        var client = await db.Clients.FindAsync(clientId);
        if (client is not null)
            client.LastReconciledUtc = timeProvider.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync();

        return new ReconcileResult
        {
            ForRequester = new SyncCommandMessage { ClientId = clientId, BatchId = Guid.NewGuid(), Ops = requesterOps },
            ForOthers = pendingForOthers,
            LocalDeletionCandidates = localDeletionCandidates,
            SnapshotTooIncompleteForDeletionInference = snapshotTooIncomplete,
        };
    }

    private async Task AdoptNewFromClientAsync(Guid clientId, IReadOnlyList<BookmarkDiffEntry> diff, List<PendingChange> pendingForOthers)
    {
        // Processed with dependency-order retry so a whole new subtree (folder + children)
        // sent in a single snapshot resolves in one pass instead of needing N reconciliations.
        var pending = diff.Where(e => e.Kind == DiffKind.NewFromClient).ToList();
        var progressed = true;
        while (pending.Count > 0 && progressed)
        {
            progressed = false;
            foreach (var entry in pending.ToList())
            {
                var node = entry.SnapshotNode!;
                var parentCanonicalId = await ResolveCanonicalParentAsync(clientId, node.ParentNativeId);
                if (parentCanonicalId is null)
                    continue; // parent not resolvable yet this pass; retried next reconciliation

                // Both browsers almost always already hold the same bookmarks before BrowserSync
                // ever runs. Without this, each browser's copy of "Raspberry PI" was adopted as a
                // SEPARATE canonical item, so edits to one never reached the other (they were
                // unrelated items) and each got pushed to the other as a new folder — the exact
                // "doesn't sync, and duplicates everything" symptom. Matching an identical,
                // not-yet-claimed sibling merges the two trees into one shared identity instead.
                var existing = await FindUnclaimedContentMatchAsync(clientId, parentCanonicalId.Value, node);
                if (existing is not null)
                {
                    db.ClientBookmarkMappings.Add(new ClientBookmarkMapping { ClientId = clientId, CanonicalId = existing.Id, NativeId = node.NativeId });
                    await db.SaveChangesAsync();
                    // Deliberately no PendingChange: canonical state is unchanged, this client
                    // just learned that something it already had is the same item the other
                    // browser already had. Fanning out here would push a pointless duplicate.
                    pending.Remove(entry);
                    progressed = true;
                    continue;
                }

                var canonical = new CanonicalBookmark
                {
                    Id = Guid.NewGuid(),
                    ParentId = parentCanonicalId,
                    Kind = node.Kind == SnapshotNodeKind.Folder ? BookmarkKind.Folder : BookmarkKind.Bookmark,
                    Title = node.Title,
                    Url = node.Url,
                    SortIndex = node.Index,
                    LastModifiedUtc = node.LastLocalModified,
                    LastModifiedByClientId = clientId,
                };
                db.CanonicalBookmarks.Add(canonical);
                db.ClientBookmarkMappings.Add(new ClientBookmarkMapping { ClientId = clientId, CanonicalId = canonical.Id, NativeId = node.NativeId });
                // Saved immediately (not batched to the end of the foreach) so a child processed
                // later in this same pass can resolve a parent that was only just created.
                await db.SaveChangesAsync();
                pendingForOthers.Add(new PendingChange(canonical.Id, PendingChangeKind.Created));

                pending.Remove(entry);
                progressed = true;
            }
        }
    }

    private async Task PushNewForClientAsync(Guid clientId, IReadOnlyList<BookmarkDiffEntry> diff, List<SyncCommandOp> requesterOps)
    {
        foreach (var entry in diff.Where(e => e.Kind == DiffKind.NewForClient))
        {
            var canonical = await db.CanonicalBookmarks.FindAsync(entry.CanonicalId!.Value);
            if (canonical is null)
                continue;

            // The diff was computed before AdoptNewFromClientAsync ran, which may have just
            // discovered this client already owns this item (content match) and mapped it.
            // Pushing a create off the stale diff would recreate the very duplicate that
            // matching exists to prevent.
            if (await FindMappingAsync(clientId, canonical.Id) is not null)
                continue;

            var parentNative = await ResolveNativeForClientAsync(clientId, canonical.ParentId);
            if (parentNative is null)
                continue; // parent not yet materialized for this client; retried next reconciliation

            requesterOps.Add(new SyncCommandOp
            {
                Op = SyncCommandOpKind.Create,
                CanonicalId = canonical.Id,
                ParentNativeId = parentNative,
                Title = canonical.Title,
                Url = canonical.Url,
                Index = canonical.SortIndex,
            });
        }
    }

    private async Task ResolveChangedAsync(Guid clientId, IReadOnlyList<BookmarkDiffEntry> diff, List<SyncCommandOp> requesterOps, List<PendingChange> pendingForOthers)
    {
        foreach (var entry in diff.Where(e => e.Kind == DiffKind.Changed))
        {
            var node = entry.SnapshotNode!;
            var canonical = await db.CanonicalBookmarks.FindAsync(entry.CanonicalId!.Value);
            if (canonical is null)
                continue;

            if (ConflictResolver.Resolve(canonical.LastModifiedUtc, node.LastLocalModified) == ConflictResolver.Winner.Incoming)
            {
                var parentCanonicalId = await ResolveCanonicalParentAsync(clientId, node.ParentNativeId) ?? canonical.ParentId;
                var contentChanged = node.Title != canonical.Title || node.Url != canonical.Url;
                var positionChanged = parentCanonicalId != canonical.ParentId || node.Index != canonical.SortIndex;

                canonical.Title = node.Title;
                canonical.Url = node.Url;
                canonical.ParentId = parentCanonicalId;
                canonical.SortIndex = node.Index;
                canonical.LastModifiedUtc = node.LastLocalModified;
                canonical.LastModifiedByClientId = clientId;

                if (contentChanged)
                    pendingForOthers.Add(new PendingChange(canonical.Id, PendingChangeKind.ContentChanged));
                if (positionChanged)
                    pendingForOthers.Add(new PendingChange(canonical.Id, PendingChangeKind.PositionChanged));
            }
            else
            {
                if (node.Title != canonical.Title || node.Url != canonical.Url)
                {
                    requesterOps.Add(new SyncCommandOp
                    {
                        Op = SyncCommandOpKind.Update,
                        CanonicalId = canonical.Id,
                        NativeId = node.NativeId,
                        Title = canonical.Title,
                        Url = canonical.Url,
                    });
                }

                // Only a genuine change of FOLDER is corrected back to the client. A pure
                // ordering difference is not: the client is reporting the order the user is
                // actually looking at, and pushing back would undo their reorder — and because
                // applying that correction fires fresh move events, the two ends can trade
                // moves indefinitely. Ordering instead settles on whatever the most recent
                // reconciliation reported, which is stable and converges.
                var parentCanonicalId = await ResolveCanonicalParentAsync(clientId, node.ParentNativeId);
                var parentNativeForRequester = await ResolveNativeForClientAsync(clientId, canonical.ParentId);
                var movedToADifferentFolder = parentCanonicalId is not null && parentCanonicalId != canonical.ParentId;

                if (movedToADifferentFolder && parentNativeForRequester is not null)
                {
                    requesterOps.Add(new SyncCommandOp
                    {
                        Op = SyncCommandOpKind.Move,
                        CanonicalId = canonical.Id,
                        NativeId = node.NativeId,
                        ParentNativeId = parentNativeForRequester,
                        Index = canonical.SortIndex,
                    });
                }
                else if (node.Index != canonical.SortIndex)
                {
                    // Take the client's ordering and pass it on to the other browser.
                    canonical.SortIndex = node.Index;
                    pendingForOthers.Add(new PendingChange(canonical.Id, PendingChangeKind.PositionChanged));
                }
            }
        }
    }

    private async Task PushTombstonedRemovalsAsync(Guid clientId, IReadOnlyList<BookmarkDiffEntry> diff, List<SyncCommandOp> requesterOps)
    {
        foreach (var entry in diff.Where(e => e.Kind == DiffKind.ClientHasTombstoned))
        {
            requesterOps.Add(new SyncCommandOp { Op = SyncCommandOpKind.Remove, CanonicalId = entry.CanonicalId!.Value, NativeId = entry.NativeId });

            var staleMapping = await db.ClientBookmarkMappings.FirstOrDefaultAsync(m => m.ClientId == clientId && m.CanonicalId == entry.CanonicalId);
            if (staleMapping is not null)
                db.ClientBookmarkMappings.Remove(staleMapping);
        }
    }

    /// <summary>Finds items this client's snapshot no longer reports. Does NOT delete anything,
    /// and nothing downstream acts on the result either — it is logged for diagnosis only. A
    /// past bug where a truncated snapshot made the host infer, and carry out, a large batch of
    /// real deletions means absence is not trusted as evidence at all. Genuine deletions arrive
    /// instead as explicit `removed` events from the extension's durable queue.</summary>
    private async Task<List<LocalDeletionCandidate>> CollectLocalDeletionCandidatesAsync(Guid clientId, IReadOnlyList<BookmarkDiffEntry> diff)
    {
        var candidates = new List<LocalDeletionCandidate>();
        foreach (var entry in diff.Where(e => e.Kind == DiffKind.ClientDeletedLocally))
        {
            // Still require two separate, independent reconciliation passes to agree before
            // even surfacing this as a candidate — cuts down noise from one-off blips. This is
            // not, by itself, sufficient protection against a *consistently* truncated snapshot
            // (the same missing subtree on two separate attempts) — hence no auto-delete.
            if (!pendingDeletions.ConfirmMissing(clientId, entry.CanonicalId!.Value))
                continue;

            var canonical = await db.CanonicalBookmarks.FindAsync(entry.CanonicalId!.Value);
            if (canonical is null)
                continue; // already gone

            candidates.Add(new LocalDeletionCandidate(canonical.Id, canonical.Title, canonical.Url, entry.NativeId ?? string.Empty));
        }

        return candidates;
    }

    // ---- Ack path ----

    /// <summary>Records the native ID the browser assigned to each host-initiated create.</summary>
    public async Task ApplyAckAsync(Guid clientId, AckMessage ack)
    {
        foreach (var created in ack.Created)
        {
            var exists = await db.ClientBookmarkMappings.AnyAsync(m => m.ClientId == clientId && m.CanonicalId == created.CanonicalId);
            if (!exists)
                db.ClientBookmarkMappings.Add(new ClientBookmarkMapping { ClientId = clientId, CanonicalId = created.CanonicalId, NativeId = created.NativeId });
        }

        await db.SaveChangesAsync();
    }

    // ---- Fan-out: translate canonical changes into one target client's native-ID commands ----

    public async Task<SyncCommandMessage> BuildCommandForClientAsync(Guid targetClientId, IReadOnlyList<PendingChange> changes)
    {
        var ops = new List<SyncCommandOp>();

        foreach (var group in changes.GroupBy(c => c.CanonicalId))
        {
            var canonicalId = group.Key;
            var kinds = group.Select(c => c.Kind).ToHashSet();

            if (kinds.Contains(PendingChangeKind.Removed))
            {
                var mapping = await FindMappingAsync(targetClientId, canonicalId);
                if (mapping is not null)
                {
                    ops.Add(new SyncCommandOp { Op = SyncCommandOpKind.Remove, CanonicalId = canonicalId, NativeId = mapping.NativeId });
                    db.ClientBookmarkMappings.Remove(mapping);
                }

                continue;
            }

            var canonical = await db.CanonicalBookmarks.FindAsync(canonicalId);
            if (canonical is null)
                continue; // deleted again before this fan-out ran

            var existingMapping = await FindMappingAsync(targetClientId, canonicalId);
            if (existingMapping is null)
            {
                var parentNative = await ResolveNativeForClientAsync(targetClientId, canonical.ParentId);
                if (parentNative is null)
                    continue; // target doesn't have the parent yet either; retried next reconciliation

                ops.Add(new SyncCommandOp
                {
                    Op = SyncCommandOpKind.Create,
                    CanonicalId = canonicalId,
                    ParentNativeId = parentNative,
                    Title = canonical.Title,
                    Url = canonical.Url,
                    Index = canonical.SortIndex,
                });
                continue;
            }

            if (kinds.Contains(PendingChangeKind.ContentChanged) || kinds.Contains(PendingChangeKind.Created))
            {
                ops.Add(new SyncCommandOp
                {
                    Op = SyncCommandOpKind.Update,
                    CanonicalId = canonicalId,
                    NativeId = existingMapping.NativeId,
                    Title = canonical.Title,
                    Url = canonical.Url,
                });
            }

            if (kinds.Contains(PendingChangeKind.PositionChanged) || kinds.Contains(PendingChangeKind.Created))
            {
                var parentNative = await ResolveNativeForClientAsync(targetClientId, canonical.ParentId);
                if (parentNative is not null)
                {
                    // Prefer stating the folder's whole order over pushing this one item to an
                    // absolute index — see SyncCommandOpKind.Reorder for why a bare index is not
                    // reliable. The moved item is itself part of that order, so this also carries
                    // it into the correct folder when the parent changed.
                    var reorder = await BuildReorderOpAsync(targetClientId, canonical.ParentId, parentNative);
                    if (reorder is not null)
                    {
                        ops.Add(reorder);
                    }
                    else
                    {
                        // Folder has fewer than two items known to this client — ordering is
                        // meaningless, so a plain move is both sufficient and unambiguous.
                        ops.Add(new SyncCommandOp
                        {
                            Op = SyncCommandOpKind.Move,
                            CanonicalId = canonicalId,
                            NativeId = existingMapping.NativeId,
                            ParentNativeId = parentNative,
                            Index = canonical.SortIndex,
                        });
                    }
                }
            }
        }

        // One folder can collect several reorders in a single batch (e.g. a multi-item drag);
        // they'd all be identical, so keep just the first of each.
        ops = ops
            .Where((op, i) => op.Op != SyncCommandOpKind.Reorder
                || ops.FindIndex(o => o.Op == SyncCommandOpKind.Reorder && o.ParentNativeId == op.ParentNativeId) == i)
            .ToList();

        await db.SaveChangesAsync();
        return new SyncCommandMessage { ClientId = targetClientId, BatchId = Guid.NewGuid(), Ops = ops };
    }

    // ---- Shared helpers ----

    private async Task<ClientBookmarkMapping?> FindMappingAsync(Guid clientId, Guid canonicalId) =>
        await db.ClientBookmarkMappings.FirstOrDefaultAsync(m => m.ClientId == clientId && m.CanonicalId == canonicalId);

    /// <summary>Builds the target client's view of a folder's full child order: its own native
    /// IDs, in canonical order, listing only children it actually knows about. Items it doesn't
    /// have are simply skipped, so an unsynced or extra item on either side can't shift
    /// everything else out of place. Returns null when there's nothing meaningful to order.</summary>
    private async Task<SyncCommandOp?> BuildReorderOpAsync(Guid targetClientId, Guid? parentCanonicalId, string parentNativeId)
    {
        if (parentCanonicalId is null)
            return null;

        var children = await db.CanonicalBookmarks
            .Where(b => b.ParentId == parentCanonicalId)
            .OrderBy(b => b.SortIndex)
            .ToListAsync();

        var orderedNativeIds = new List<string>();
        foreach (var child in children)
        {
            var mapping = await FindMappingAsync(targetClientId, child.Id);
            if (mapping is not null)
                orderedNativeIds.Add(mapping.NativeId);
        }

        if (orderedNativeIds.Count < 2)
            return null;

        return new SyncCommandOp
        {
            Op = SyncCommandOpKind.Reorder,
            CanonicalId = parentCanonicalId.Value,
            ParentNativeId = parentNativeId,
            OrderedNativeIds = orderedNativeIds,
        };
    }

    /// <summary>
    /// Finds an existing canonical item that is evidently the same real-world bookmark as
    /// <paramref name="node"/> — same canonical parent, same kind, same title, same URL — and
    /// which this client has not already claimed a mapping to.
    ///
    /// The "not already claimed" part is what keeps this safe. If a browser genuinely has two
    /// identical bookmarks side by side in one folder, the first claims the existing canonical
    /// item and the second finds it taken, so it still gets its own — they stay two items, and
    /// cleanup is left to the (user-confirmed) duplicate removal flow rather than silently
    /// collapsing them here.
    ///
    /// Because adoption runs parent-before-child, a matched folder means its children are then
    /// compared against that same shared folder's children, so an entire overlapping subtree
    /// merges rather than only its root.
    /// </summary>
    private async Task<CanonicalBookmark?> FindUnclaimedContentMatchAsync(Guid clientId, Guid parentCanonicalId, BookmarkSnapshotNode node)
    {
        var kind = node.Kind == SnapshotNodeKind.Folder ? BookmarkKind.Folder : BookmarkKind.Bookmark;
        var candidates = await db.CanonicalBookmarks
            .Where(b => b.ParentId == parentCanonicalId && b.Kind == kind && b.Title == node.Title && b.Url == node.Url)
            .OrderBy(b => b.SortIndex)
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            var claimed = await db.ClientBookmarkMappings.AnyAsync(m => m.ClientId == clientId && m.CanonicalId == candidate.Id);
            if (!claimed)
                return candidate;
        }

        return null;
    }

    /// <summary>Looks up a client's canonical ID for a native parent ID — including the well-known
    /// roots, which resolve through the same plain mapping lookup since
    /// <see cref="EnsureRoleRootMappingsAsync"/> has already inserted real mapping rows for them
    /// using whatever native ID this specific client actually reports.</summary>
    private async Task<Guid?> ResolveCanonicalParentAsync(Guid clientId, string? parentNativeId)
    {
        if (parentNativeId is null)
            return null;

        var mapping = await db.ClientBookmarkMappings.FirstOrDefaultAsync(m => m.ClientId == clientId && m.NativeId == parentNativeId);
        return mapping?.CanonicalId;
    }

    private async Task<string?> ResolveNativeForClientAsync(Guid clientId, Guid? canonicalParentId)
    {
        if (canonicalParentId is null)
            return null;

        var mapping = await FindMappingAsync(clientId, canonicalParentId.Value);
        return mapping?.NativeId;
    }

    /// <summary>Tombstones a canonical node and its entire descendant subtree (used both for an
    /// explicit remove event and for a node discovered missing during reconciliation).</summary>
    private async Task<IReadOnlyList<PendingChange>> DeleteCanonicalSubtreeAsync(Guid rootCanonicalId, Guid deletedByClientId)
    {
        var all = await db.CanonicalBookmarks.ToListAsync(); // personal-scale dataset; a full load keeps the subtree walk simple

        var toDelete = new List<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(rootCanonicalId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (toDelete.Contains(id))
                continue;
            toDelete.Add(id);
            foreach (var child in all.Where(b => b.ParentId == id))
                stack.Push(child.Id);
        }

        // Paths are resolved for the WHOLE subtree up front. Resolving them as we go would walk
        // parents that this same loop has already removed, so a child's path came out as
        // "?/Fan SHIM" — useless for the one job this log has.
        var pathsBeforeDeletion = new Dictionary<Guid, string>();
        foreach (var id in toDelete)
        {
            var row = await db.CanonicalBookmarks.FindAsync(id);
            if (row is not null)
                pathsBeforeDeletion[id] = await BookmarkPath.OfAsync(db, row);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var changes = new List<PendingChange>();
        foreach (var id in toDelete)
        {
            var row = await db.CanonicalBookmarks.FindAsync(id);
            if (row is null)
                continue; // already gone (e.g. reported by two clients independently)

            // Recorded before removal — afterwards there is nothing left to describe what was
            // lost, and this log is the only way back if a deletion turns out to be unwanted.
            await RecordAsync(deletedByClientId, ActivityKind.Delete, pathsBeforeDeletion.GetValueOrDefault(id, row.Title), row.Url);

            db.CanonicalBookmarks.Remove(row);
            db.Tombstones.Add(new Tombstone { CanonicalId = id, DeletedAtUtc = now, DeletedByClientId = deletedByClientId });

            // Only the deleting client's own mapping is cleaned up here — it's the one we know
            // is stale. Other (possibly offline) clients keep their mapping until they're
            // individually told about the delete, either via fan-out (BuildCommandForClientAsync)
            // or, if they were offline, via reconciliation's ClientHasTombstoned handling. Wiping
            // every client's mapping eagerly here would make that tombstone undiscoverable and
            // let a late-reconnecting client resurrect the item instead of learning it was deleted.
            db.ClientBookmarkMappings.RemoveRange(db.ClientBookmarkMappings.Where(m => m.CanonicalId == id && m.ClientId == deletedByClientId));
            changes.Add(new PendingChange(id, PendingChangeKind.Removed));
        }

        await db.SaveChangesAsync();
        return changes;
    }
}
