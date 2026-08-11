using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;
using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

/// <summary>
/// Reordering siblings is subtle: dragging B above A fires a bookmark event for B only. A's
/// index shifts from 0 to 1 implicitly, with no event at all. So the host learns one node's new
/// index while every other sibling's stored index silently goes stale.
/// </summary>
public class ReorderTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static List<BookmarkSnapshotNode> Roots() =>
    [
        new() { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
        new() { NativeId = "2", Kind = SnapshotNodeKind.Folder, Title = "Other", Role = WellKnownRoots.OtherRole, Index = 1, LastLocalModified = T0 },
        new() { NativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Mobile", Role = WellKnownRoots.MobileRole, Index = 2, LastLocalModified = T0 },
    ];

    private static BookmarkSnapshotNode Bookmark(string nativeId, string title, int index, DateTime modified) =>
        new() { NativeId = nativeId, ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = title, Url = $"https://example.com/{title}", Index = index, LastLocalModified = modified };

    /// <summary>A(0), B(1) adopted from a first snapshot.</summary>
    private static async Task<SyncEngine> SeedAsync(TestDb testDb, Guid clientId)
    {
        var engine = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        await engine.ReconcileAsync(clientId, BrowserKind.Chrome, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes = [.. Roots(), Bookmark("10", "A", 0, T0), Bookmark("11", "B", 1, T0)],
        });
        return engine;
    }

    [Fact]
    public async Task DraggingASiblingUp_LeavesCanonicalOrderConsistentWithTheBrowser()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await SeedAsync(testDb, clientId);

        // User drags B above A. Only B gets an event; A implicitly becomes index 1.
        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved,
            NativeId = "11",
            ParentNativeId = "1",
            Index = 0,
            Timestamp = T0.AddMinutes(1),
        });

        await using var db = testDb.NewContext();
        var a = db.CanonicalBookmarks.Single(x => x.Title == "A");
        var b = db.CanonicalBookmarks.Single(x => x.Title == "B");

        // The browser now shows B, A. Canonical must say the same, otherwise the two disagree
        // and the next snapshot looks like an unexplained difference.
        Assert.Equal(0, b.SortIndex);
        Assert.Equal(1, a.SortIndex);
    }

    [Fact]
    public async Task AfterAReorder_TheNextSnapshotDoesNotFightTheUser()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await SeedAsync(testDb, clientId);

        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved, NativeId = "11", ParentNativeId = "1", Index = 0, Timestamp = T0.AddMinutes(1),
        });

        // Chrome's next snapshot reports what the user actually sees: B(0), A(1).
        var result = await engine.ReconcileAsync(clientId, BrowserKind.Chrome, new SnapshotMessage
        {
            GeneratedAt = T0.AddMinutes(2),
            Nodes = [.. Roots(), Bookmark("11", "B", 0, T0.AddMinutes(1)), Bookmark("10", "A", 1, T0)],
        });

        // The host must NOT reply by shoving anything back — that would undo the reorder the
        // user just made, and each correction fires fresh events, so the two can trade moves
        // back and forth indefinitely.
        Assert.Empty(result.ForRequester.Ops.Where(o => o.Op == SyncCommandOpKind.Move));
    }

    [Fact]
    public async Task ReorderingAndReorderingBack_EndsUpWhereItStarted()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = await SeedAsync(testDb, clientId);

        // B up above A...
        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved, NativeId = "11", ParentNativeId = "1", Index = 0, Timestamp = T0.AddMinutes(1),
        });
        // ...then straight back down again.
        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved, NativeId = "11", ParentNativeId = "1", Index = 1, Timestamp = T0.AddMinutes(2),
        });

        await using var db = testDb.NewContext();
        Assert.Equal(0, db.CanonicalBookmarks.Single(x => x.Title == "A").SortIndex);
        Assert.Equal(1, db.CanonicalBookmarks.Single(x => x.Title == "B").SortIndex);
    }

    [Fact]
    public async Task AReorderIsFannedOutToTheOtherBrowser()
    {
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chrome = await SeedAsync(testDb, chromeId);

        // Edge already has the same two bookmarks; content matching merges them.
        var edge = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        await edge.ReconcileAsync(edgeId, BrowserKind.Edge, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                .. Roots(),
                new BookmarkSnapshotNode { NativeId = "900", ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = "A", Url = "https://example.com/A", Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "901", ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = "B", Url = "https://example.com/B", Index = 1, LastLocalModified = T0 },
            ],
        });

        var moved = await chrome.ApplyEventAsync(chromeId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved, NativeId = "11", ParentNativeId = "1", Index = 0, Timestamp = T0.AddMinutes(1),
        });

        // Fanned out by the engine that applied the change, exactly as ClientConnection does —
        // using the other engine here would read that engine's stale cached copy.
        var command = await chrome.BuildCommandForClientAsync(edgeId, moved.ForOthers);

        // The folder's whole order is stated in Edge's own native IDs, rather than pushing B to
        // an absolute index that Edge may interpret differently.
        var reorder = Assert.Single(command.Ops, o => o.Op == SyncCommandOpKind.Reorder);
        Assert.Equal(["901", "900"], reorder.OrderedNativeIds); // B then A
    }

    [Fact]
    public async Task ReorderUsesOnlyTheChildrenTheTargetBrowserActuallyHas()
    {
        // Real folders don't always match: one browser can hold an item the other hasn't got
        // yet. Listing an item the target doesn't know about would shift everything after it.
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chrome = await SeedAsync(testDb, chromeId);

        // Edge has only A — no B.
        var edge = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        await edge.ReconcileAsync(edgeId, BrowserKind.Edge, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                .. Roots(),
                new BookmarkSnapshotNode { NativeId = "900", ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = "A", Url = "https://example.com/A", Index = 0, LastLocalModified = T0 },
            ],
        });

        var moved = await chrome.ApplyEventAsync(chromeId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved, NativeId = "11", ParentNativeId = "1", Index = 0, Timestamp = T0.AddMinutes(1),
        });

        var command = await chrome.BuildCommandForClientAsync(edgeId, moved.ForOthers);

        // Only A is known to Edge, so there is no meaningful order to state — and critically no
        // op referencing a native ID Edge has never heard of.
        Assert.DoesNotContain(command.Ops, o => o.OrderedNativeIds?.Contains("11") == true);
        Assert.DoesNotContain(command.Ops, o => o.NativeId == "11");
    }

    [Fact]
    public async Task DraggingAnItemToTheEndOfAFolder_StatesTheFullResultingOrder()
    {
        // The Raspberry PI case: five bookmarks, one dragged to the bottom.
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();

        var titles = new[] { "Fan SHIM", "Samsung", "FIX", "PiJuice", "MicroUSB" };
        var chrome = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        await chrome.ReconcileAsync(chromeId, BrowserKind.Chrome, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes = [.. Roots(), .. titles.Select((t, i) => Bookmark($"{1182 + i}", t, i, T0))],
        });

        var edge = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        await edge.ReconcileAsync(edgeId, BrowserKind.Edge, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes = [.. Roots(), .. titles.Select((t, i) => Bookmark($"{1195 + i}", t, i, T0))],
        });

        // Drag "Samsung" (Chrome native 1183, currently index 1) to the bottom.
        var moved = await chrome.ApplyEventAsync(chromeId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved, NativeId = "1183", ParentNativeId = "1", Index = 4, Timestamp = T0.AddMinutes(1),
        });

        var command = await chrome.BuildCommandForClientAsync(edgeId, moved.ForOthers);
        var reorder = Assert.Single(command.Ops, o => o.Op == SyncCommandOpKind.Reorder);

        // Edge's native IDs, Samsung (1196) last.
        Assert.Equal(["1195", "1197", "1198", "1199", "1196"], reorder.OrderedNativeIds);
    }
}
