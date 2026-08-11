using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;
using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

public class SyncEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<SyncEngine> NewEngineAsync(TestDb testDb, Guid clientId, BrowserKind kind)
    {
        var db = testDb.NewContext();
        var engine = new SyncEngine(db, TimeProvider.System);
        await engine.EnsureClientAsync(clientId, kind);
        // Native IDs "1"/"2"/"3" here are just this test suite's convention, not an assumption
        // the engine relies on — EnsureRoleRootMappingsAsync learns roots from Role, not native ID.
        await engine.EnsureRoleRootMappingsAsync(clientId, RootNodes());
        return engine;
    }

    private static List<BookmarkSnapshotNode> RootNodes() =>
    [
        new() { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
        new() { NativeId = "2", Kind = SnapshotNodeKind.Folder, Title = "Other Bookmarks", Role = WellKnownRoots.OtherRole, Index = 1, LastLocalModified = T0 },
        new() { NativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Mobile Bookmarks", Role = WellKnownRoots.MobileRole, Index = 2, LastLocalModified = T0 },
    ];

    private static BookmarkEventMessage CreateEvt(string nativeId, string parentNativeId, string title, string? url, DateTime timestamp) =>
        new()
        {
            Op = BookmarkEventOp.Created,
            NativeId = nativeId,
            ParentNativeId = parentNativeId,
            Index = 0,
            Title = title,
            Url = url,
            Timestamp = timestamp,
        };

    [Fact]
    public async Task CreateEvent_AddsCanonicalRowAndMapping_AndReportsForOthers()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);

        var result = await engine.ApplyEventAsync(clientId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        var change = Assert.Single(result.ForOthers);
        Assert.Equal(PendingChangeKind.Created, change.Kind);

        await using var db = testDb.NewContext();
        var canonical = Assert.Single(db.CanonicalBookmarks.Where(b => b.RoleRoot == RootRole.None));
        Assert.Equal("Example", canonical.Title);
        Assert.Equal("https://example.com", canonical.Url);
        Assert.Equal(WellKnownRoots.BookmarksBar, canonical.ParentId);

        var mapping = Assert.Single(db.ClientBookmarkMappings.Where(m => m.NativeId == "100"));
        Assert.Equal(canonical.Id, mapping.CanonicalId);
        Assert.Equal(clientId, mapping.ClientId);
    }

    [Fact]
    public async Task DuplicateCreateEvent_IsTreatedAsUpdateNotASecondRow()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);

        await engine.ApplyEventAsync(clientId, CreateEvt("100", "1", "Example", "https://example.com", T0));
        await engine.ApplyEventAsync(clientId, CreateEvt("100", "1", "Example (renamed)", "https://example.com", T0.AddSeconds(1)));

        await using var db = testDb.NewContext();
        var canonical = Assert.Single(db.CanonicalBookmarks.Where(b => b.RoleRoot == RootRole.None));
        Assert.Equal("Example (renamed)", canonical.Title);
    }

    [Fact]
    public async Task NewerRenameEvent_UpdatesCanonicalAndReportsForOthers()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);
        await engine.ApplyEventAsync(clientId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        var result = await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Changed,
            NativeId = "100",
            Title = "Renamed",
            Url = "https://example.com",
            Timestamp = T0.AddSeconds(5),
        });

        Assert.Contains(result.ForOthers, c => c.Kind == PendingChangeKind.ContentChanged);
        Assert.Null(result.CorrectionForSender);

        await using var db = testDb.NewContext();
        var canonical = Assert.Single(db.CanonicalBookmarks.Where(b => b.RoleRoot == RootRole.None));
        Assert.Equal("Renamed", canonical.Title);
    }

    [Fact]
    public async Task StaleRenameEvent_IsDroppedAndSenderIsCorrected()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);
        await engine.ApplyEventAsync(clientId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        // Canonical already has a later edit than the stale one about to arrive.
        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Changed, NativeId = "100", Title = "Newer Title", Url = "https://example.com", Timestamp = T0.AddSeconds(10),
        });

        var staleResult = await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Changed, NativeId = "100", Title = "Stale Title", Url = "https://example.com", Timestamp = T0.AddSeconds(1),
        });

        Assert.Empty(staleResult.ForOthers);
        Assert.NotNull(staleResult.CorrectionForSender);
        Assert.Equal(SyncCommandOpKind.Update, staleResult.CorrectionForSender!.Op);
        Assert.Equal("Newer Title", staleResult.CorrectionForSender.Title);
    }

    [Fact]
    public async Task RemoveEvent_TombstonesAndReportsForOthers()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);
        await engine.ApplyEventAsync(clientId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        var result = await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Removed, NativeId = "100", Timestamp = T0.AddSeconds(2),
        });

        var change = Assert.Single(result.ForOthers);
        Assert.Equal(PendingChangeKind.Removed, change.Kind);

        await using var db = testDb.NewContext();
        Assert.Empty(db.CanonicalBookmarks.Where(b => b.RoleRoot == RootRole.None));
        Assert.Single(db.Tombstones);
        Assert.Empty(db.ClientBookmarkMappings.Where(m => m.NativeId == "100"));
    }

    [Fact]
    public async Task BuildCommandForClient_ProducesCreateOpForClientMissingTheNode()
    {
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chromeEngine = await NewEngineAsync(testDb, chromeId, BrowserKind.Chrome);
        await chromeEngine.ApplyEventAsync(chromeId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        // Edge connects and gets its own root mappings established, but doesn't have this bookmark yet.
        var edgeEngine = await NewEngineAsync(testDb, edgeId, BrowserKind.Edge);

        await using var db = testDb.NewContext();
        var canonicalId = db.CanonicalBookmarks.Single(b => b.RoleRoot == RootRole.None).Id;

        var command = await edgeEngine.BuildCommandForClientAsync(edgeId, [new PendingChange(canonicalId, PendingChangeKind.Created)]);

        var op = Assert.Single(command.Ops);
        Assert.Equal(SyncCommandOpKind.Create, op.Op);
        Assert.Equal("1", op.ParentNativeId); // Edge's own native ID for the Bookmarks Bar root
        Assert.Equal("Example", op.Title);
    }

    [Fact]
    public async Task Reconcile_NewFromClientSubtree_ResolvesFolderAndChildInOnePass()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);

        var snapshot = new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "200", ParentNativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Work", Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "201", ParentNativeId = "200", Kind = SnapshotNodeKind.Bookmark, Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = T0 },
            ],
        };

        var result = await engine.ReconcileAsync(clientId, BrowserKind.Chrome, snapshot);

        Assert.Empty(result.ForRequester.Ops); // requester already has everything it sent
        Assert.Equal(2, result.ForOthers.Count(c => c.Kind == PendingChangeKind.Created));

        await using var db = testDb.NewContext();
        var work = Assert.Single(db.CanonicalBookmarks.Where(b => b.Title == "Work"));
        var example = Assert.Single(db.CanonicalBookmarks.Where(b => b.Title == "Example"));
        Assert.Equal(work.Id, example.ParentId);
        Assert.Equal(WellKnownRoots.BookmarksBar, work.ParentId);
    }

    [Fact]
    public async Task Reconcile_CanonicalNodeMissingFromClientSnapshot_PushesCreateBackToClient()
    {
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chromeEngine = await NewEngineAsync(testDb, chromeId, BrowserKind.Chrome);
        await chromeEngine.ApplyEventAsync(chromeId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        var edgeEngine = await NewEngineAsync(testDb, edgeId, BrowserKind.Edge);
        var edgeSnapshot = new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes = [new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 }],
        };

        var result = await edgeEngine.ReconcileAsync(edgeId, BrowserKind.Edge, edgeSnapshot);

        var op = Assert.Single(result.ForRequester.Ops);
        Assert.Equal(SyncCommandOpKind.Create, op.Op);
        Assert.Equal("Example", op.Title);
        Assert.Equal("1", op.ParentNativeId);
    }

    [Fact]
    public async Task Reconcile_ClientDeletedLocallyWhileHostWasOffline_ReportsACandidateButNeverDeletes()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);
        await engine.ApplyEventAsync(clientId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        // Client's next snapshot no longer contains the bookmark — it was deleted locally
        // without an event ever reaching the host (e.g. while the host was down).
        var snapshot = new SnapshotMessage
        {
            GeneratedAt = T0.AddMinutes(5),
            Nodes = [new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 }],
        };

        // A single reconciliation pass reporting the item missing is not enough on its own — see
        // PendingDeletionTracker. The first pass just records the observation.
        var firstPass = await engine.ReconcileAsync(clientId, BrowserKind.Chrome, snapshot);
        Assert.Empty(firstPass.LocalDeletionCandidates);
        await using (var mid = testDb.NewContext())
            Assert.Single(mid.CanonicalBookmarks.Where(b => b.RoleRoot == RootRole.None));

        // A second, separate pass confirming the same absence reports it as a candidate — but
        // still never deletes it. A truncated snapshot once caused a large batch of real
        // bookmarks to be auto-deleted, so absence is recorded for diagnosis and nothing more;
        // genuine deletions arrive as explicit `removed` events instead.
        var secondPass = await engine.ReconcileAsync(clientId, BrowserKind.Chrome, snapshot);
        var candidate = Assert.Single(secondPass.LocalDeletionCandidates);
        Assert.Equal("Example", candidate.Title);
        Assert.Empty(secondPass.ForOthers);

        await using var db = testDb.NewContext();
        Assert.Single(db.CanonicalBookmarks.Where(b => b.RoleRoot == RootRole.None));
        Assert.Empty(db.Tombstones);
    }

    [Fact]
    public async Task Reconcile_ItemReappearsBetweenPasses_ResetsTheMissingStrikeAndNeverDeletes()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);
        await engine.ApplyEventAsync(clientId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        var withoutItem = new SnapshotMessage
        {
            GeneratedAt = T0.AddMinutes(1),
            Nodes = [new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 }],
        };
        var withItem = new SnapshotMessage
        {
            GeneratedAt = T0.AddMinutes(2),
            Nodes =
            [
                new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "100", ParentNativeId = "1", Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = T0 },
            ],
        };

        // Simulates a one-off truncated snapshot: missing once, then present again, missing
        // again — never two CONSECUTIVE misses, so it must never be deleted.
        await engine.ReconcileAsync(clientId, BrowserKind.Chrome, withoutItem); // strike 1
        await engine.ReconcileAsync(clientId, BrowserKind.Chrome, withItem); // reappears -> strike cleared
        var result = await engine.ReconcileAsync(clientId, BrowserKind.Chrome, withoutItem); // strike 1 again, not 2

        Assert.Empty(result.LocalDeletionCandidates);
        Assert.DoesNotContain(result.ForOthers, c => c.Kind == PendingChangeKind.Removed);
        await using var db = testDb.NewContext();
        Assert.Single(db.CanonicalBookmarks.Where(b => b.RoleRoot == RootRole.None));
        Assert.Empty(db.Tombstones);
    }

    [Fact]
    public async Task Reconcile_ClientStillHoldingATombstonedItem_TellsClientToRemoveIt()
    {
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chromeEngine = await NewEngineAsync(testDb, chromeId, BrowserKind.Chrome);
        await chromeEngine.ApplyEventAsync(chromeId, CreateEvt("100", "1", "Example", "https://example.com", T0));

        // Edge adopts the bookmark, then the host learns (from Chrome) that it was deleted.
        var edgeEngine = await NewEngineAsync(testDb, edgeId, BrowserKind.Edge);
        await edgeEngine.ReconcileAsync(edgeId, BrowserKind.Edge, new SnapshotMessage { GeneratedAt = T0, Nodes = [] });
        await using (var db = testDb.NewContext())
        {
            var canonicalId = db.CanonicalBookmarks.Single(b => b.RoleRoot == RootRole.None).Id;
            db.ClientBookmarkMappings.Add(new ClientBookmarkMapping { ClientId = edgeId, CanonicalId = canonicalId, NativeId = "500" });
            await db.SaveChangesAsync();
        }
        await chromeEngine.ApplyEventAsync(chromeId, new BookmarkEventMessage { Op = BookmarkEventOp.Removed, NativeId = "100", Timestamp = T0.AddSeconds(1) });

        // Edge reconnects still believing it has native id "500" for the (now deleted) bookmark.
        var edgeSnapshot = new SnapshotMessage
        {
            GeneratedAt = T0.AddMinutes(1),
            Nodes =
            [
                new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "500", ParentNativeId = "1", Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = T0 },
            ],
        };

        var result = await edgeEngine.ReconcileAsync(edgeId, BrowserKind.Edge, edgeSnapshot);

        var op = Assert.Single(result.ForRequester.Ops);
        Assert.Equal(SyncCommandOpKind.Remove, op.Op);
        Assert.Equal("500", op.NativeId);
    }

    [Fact]
    public async Task Reconcile_RootNativeIdsDifferPerClient_StillSyncsOtherAndMobileContentAcrossBrowsers()
    {
        // Regression test for a real bug: Chromium does NOT guarantee the "Other"/"Mobile"
        // permanent folders keep native IDs "1"/"2"/"3" — on a profile with enough bookmark
        // history, they can end up with arbitrary IDs (observed on a real Edge profile: "30"
        // for Other, "164" for Mobile). The engine must resolve roots purely from each client's
        // own Role-tagged snapshot nodes, never by assuming a fixed native ID.
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chromeDb = testDb.NewContext();
        var chromeEngine = new SyncEngine(chromeDb, TimeProvider.System);
        var edgeDb = testDb.NewContext();
        var edgeEngine = new SyncEngine(edgeDb, TimeProvider.System);

        // Chrome: standard-looking native IDs.
        var chromeSnapshot = new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "2", Kind = SnapshotNodeKind.Folder, Title = "Other Bookmarks", Role = WellKnownRoots.OtherRole, Index = 1, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Mobile Bookmarks", Role = WellKnownRoots.MobileRole, Index = 2, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "80", ParentNativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Raspberry PI", Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "81", ParentNativeId = "80", Kind = SnapshotNodeKind.Bookmark, Title = "Fan SHIM", Url = "https://shop.pimoroni.com/products/fan-shim", Index = 0, LastLocalModified = T0 },
            ],
        };
        var chromeResult = await chromeEngine.ReconcileAsync(chromeId, BrowserKind.Chrome, chromeSnapshot);
        Assert.Equal(2, chromeResult.ForOthers.Count(c => c.Kind == PendingChangeKind.Created));

        // Edge: real-world non-standard native IDs for Other ("30") and Mobile ("164"), and no
        // knowledge yet of "Raspberry PI"/"Fan SHIM".
        var edgeSnapshot = new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Favourites bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "30", Kind = SnapshotNodeKind.Folder, Title = "Other favourites", Role = WellKnownRoots.OtherRole, Index = 1, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "164", Kind = SnapshotNodeKind.Folder, Title = "Mobile favourites", Role = WellKnownRoots.MobileRole, Index = 2, LastLocalModified = T0 },
            ],
        };
        await edgeEngine.ReconcileAsync(edgeId, BrowserKind.Edge, edgeSnapshot);

        // Fan out Chrome's new items to Edge, exactly as ClientConnection.FanOutAsync would. Only
        // the folder resolves this pass — its child's parent-native-ID isn't knowable until Edge
        // acks the folder create and reports what native ID it assigned (the accepted v1
        // limitation: a brand-new multi-level subtree can take more than one pass to fully land).
        var pushToEdge = await edgeEngine.BuildCommandForClientAsync(edgeId, chromeResult.ForOthers);
        var folderCreate = Assert.Single(pushToEdge.Ops, o => o.Op == SyncCommandOpKind.Create && o.Title == "Raspberry PI");
        Assert.Equal("164", folderCreate.ParentNativeId); // Edge's OWN native ID for the Mobile root, not "3"
    }

    [Fact]
    public async Task Reconcile_TruncatedSnapshotMissingMostOfTheTree_InfersNoDeletionsAtAll()
    {
        // Reproduces the real incident: a client with a large collection sends a snapshot
        // containing only a fraction of it (service worker killed mid-build). Previously this
        // read as a mass deletion and destroyed real bookmarks on both browsers.
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);

        for (var i = 0; i < 100; i++)
            await engine.ApplyEventAsync(clientId, CreateEvt($"{1000 + i}", "1", $"Bookmark {i}", $"https://example.com/{i}", T0));

        var fullNodes = Enumerable.Range(0, 100)
            .Select(i => new BookmarkSnapshotNode { NativeId = $"{1000 + i}", ParentNativeId = "1", Title = $"Bookmark {i}", Url = $"https://example.com/{i}", Index = i, LastLocalModified = T0 })
            .ToList();

        // Only the first 10 of 100 survived the truncation.
        var truncated = new SnapshotMessage
        {
            GeneratedAt = T0.AddMinutes(1),
            Nodes = [.. RootNodes(), .. fullNodes.Take(10)],
        };

        // Two consecutive truncated snapshots — enough to satisfy the two-pass confirmation on
        // its own, which is exactly why that alone was insufficient protection.
        var first = await engine.ReconcileAsync(clientId, BrowserKind.Chrome, truncated);
        var second = await engine.ReconcileAsync(clientId, BrowserKind.Chrome, truncated);

        Assert.True(first.SnapshotTooIncompleteForDeletionInference);
        Assert.True(second.SnapshotTooIncompleteForDeletionInference);
        Assert.Empty(first.LocalDeletionCandidates);
        Assert.Empty(second.LocalDeletionCandidates);

        await using var db = testDb.NewContext();
        Assert.Equal(100, db.CanonicalBookmarks.Count(b => b.RoleRoot == RootRole.None));
        Assert.Empty(db.Tombstones);
    }

    [Fact]
    public async Task Reconcile_ModestNumberOfRealDeletions_IsStillDetectedNormally()
    {
        // The guard must not swallow genuine deletions — only implausible mass ones.
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await NewEngineAsync(testDb, clientId, BrowserKind.Chrome);

        for (var i = 0; i < 100; i++)
            await engine.ApplyEventAsync(clientId, CreateEvt($"{1000 + i}", "1", $"Bookmark {i}", $"https://example.com/{i}", T0));

        // 3 of 100 genuinely deleted — well inside plausible.
        var remaining = Enumerable.Range(3, 97)
            .Select(i => new BookmarkSnapshotNode { NativeId = $"{1000 + i}", ParentNativeId = "1", Title = $"Bookmark {i}", Url = $"https://example.com/{i}", Index = i, LastLocalModified = T0 })
            .ToList();
        var snapshot = new SnapshotMessage { GeneratedAt = T0.AddMinutes(1), Nodes = [.. RootNodes(), .. remaining] };

        await engine.ReconcileAsync(clientId, BrowserKind.Chrome, snapshot);
        var second = await engine.ReconcileAsync(clientId, BrowserKind.Chrome, snapshot);

        Assert.False(second.SnapshotTooIncompleteForDeletionInference);
        Assert.Equal(3, second.LocalDeletionCandidates.Count);
    }
}
