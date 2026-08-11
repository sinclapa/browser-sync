using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;
using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

/// <summary>
/// Covers the first-run merge of two browsers that already hold overlapping bookmarks.
///
/// Without content matching, each browser's copy of the same folder became a SEPARATE canonical
/// item: edits to one never reached the other (they were unrelated items), and each got pushed
/// to the other browser as a brand-new folder. Observed in practice as "Mobile bookmarks >
/// Raspberry PI doesn't sync" alongside runaway duplication, with the canonical store holding
/// exactly double the real number of items.
/// </summary>
public class InitialMergeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static List<BookmarkSnapshotNode> Roots(string bar, string other, string mobile) =>
    [
        new() { NativeId = bar, Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
        new() { NativeId = other, Kind = SnapshotNodeKind.Folder, Title = "Other", Role = WellKnownRoots.OtherRole, Index = 1, LastLocalModified = T0 },
        new() { NativeId = mobile, Kind = SnapshotNodeKind.Folder, Title = "Mobile", Role = WellKnownRoots.MobileRole, Index = 2, LastLocalModified = T0 },
    ];

    [Fact]
    public async Task BothBrowsersAlreadyHaveTheSameSubtree_MergesIntoOneCanonicalIdentity()
    {
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chrome = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        var edge = new SyncEngine(testDb.NewContext(), TimeProvider.System);

        // Chrome: Mobile > Raspberry PI > Fan SHIM. Native IDs "1"/"2"/"3" for roots.
        var chromeSnapshot = new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                .. Roots("1", "2", "3"),
                new BookmarkSnapshotNode { NativeId = "80", ParentNativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Raspberry PI", Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "81", ParentNativeId = "80", Kind = SnapshotNodeKind.Bookmark, Title = "Fan SHIM", Url = "https://shop.pimoroni.com/products/fan-shim", Index = 0, LastLocalModified = T0 },
            ],
        };
        await chrome.ReconcileAsync(chromeId, BrowserKind.Chrome, chromeSnapshot);

        // Edge already has the identical subtree, under its own (different) native IDs.
        var edgeSnapshot = new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                .. Roots("1", "30", "164"),
                new BookmarkSnapshotNode { NativeId = "900", ParentNativeId = "164", Kind = SnapshotNodeKind.Folder, Title = "Raspberry PI", Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "901", ParentNativeId = "900", Kind = SnapshotNodeKind.Bookmark, Title = "Fan SHIM", Url = "https://shop.pimoroni.com/products/fan-shim", Index = 0, LastLocalModified = T0 },
            ],
        };
        var edgeResult = await edge.ReconcileAsync(edgeId, BrowserKind.Edge, edgeSnapshot);

        await using var db = testDb.NewContext();

        // Exactly one canonical folder and one canonical bookmark — not two of each.
        var folder = Assert.Single(db.CanonicalBookmarks.Where(b => b.Title == "Raspberry PI"));
        var bookmark = Assert.Single(db.CanonicalBookmarks.Where(b => b.Title == "Fan SHIM"));
        Assert.Equal(WellKnownRoots.MobileBookmarks, folder.ParentId);
        Assert.Equal(folder.Id, bookmark.ParentId);

        // Both browsers map to that same shared identity, each via its own native IDs.
        Assert.Equal("80", db.ClientBookmarkMappings.Single(m => m.ClientId == chromeId && m.CanonicalId == folder.Id).NativeId);
        Assert.Equal("900", db.ClientBookmarkMappings.Single(m => m.ClientId == edgeId && m.CanonicalId == folder.Id).NativeId);
        Assert.Equal("81", db.ClientBookmarkMappings.Single(m => m.ClientId == chromeId && m.CanonicalId == bookmark.Id).NativeId);
        Assert.Equal("901", db.ClientBookmarkMappings.Single(m => m.ClientId == edgeId && m.CanonicalId == bookmark.Id).NativeId);

        // Nothing to push anywhere — recognising an existing item is not a change.
        Assert.Empty(edgeResult.ForRequester.Ops);
        Assert.Empty(edgeResult.ForOthers);
    }

    [Fact]
    public async Task AfterMerging_AnEditInOneBrowserReachesTheOther()
    {
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chrome = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        var edge = new SyncEngine(testDb.NewContext(), TimeProvider.System);

        await chrome.ReconcileAsync(chromeId, BrowserKind.Chrome, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                .. Roots("1", "2", "3"),
                new BookmarkSnapshotNode { NativeId = "80", ParentNativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Raspberry PI", Index = 0, LastLocalModified = T0 },
            ],
        });
        await edge.ReconcileAsync(edgeId, BrowserKind.Edge, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                .. Roots("1", "30", "164"),
                new BookmarkSnapshotNode { NativeId = "900", ParentNativeId = "164", Kind = SnapshotNodeKind.Folder, Title = "Raspberry PI", Index = 0, LastLocalModified = T0 },
            ],
        });

        // Chrome adds a bookmark inside its Raspberry PI folder.
        var added = await chrome.ApplyEventAsync(chromeId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Created,
            NativeId = "82",
            ParentNativeId = "80",
            Index = 0,
            Title = "PiJuice HAT",
            Url = "https://uk.pi-supply.com/products/pijuice-standard",
            Timestamp = T0.AddMinutes(1),
        });

        // It must land inside EDGE'S EXISTING folder (native "900"), not a new one.
        var command = await edge.BuildCommandForClientAsync(edgeId, added.ForOthers);
        var op = Assert.Single(command.Ops, o => o.Op == SyncCommandOpKind.Create);
        Assert.Equal("PiJuice HAT", op.Title);
        Assert.Equal("900", op.ParentNativeId);
    }

    [Fact]
    public async Task TwoIdenticalBookmarksInOneFolder_StayTwoItems()
    {
        // Content matching must not silently collapse genuine side-by-side duplicates within a
        // single browser — the first claims the canonical item, the second finds it taken.
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var engine = new SyncEngine(testDb.NewContext(), TimeProvider.System);

        await engine.ReconcileAsync(clientId, BrowserKind.Chrome, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                .. Roots("1", "2", "3"),
                new BookmarkSnapshotNode { NativeId = "10", ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "11", ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = "Example", Url = "https://example.com", Index = 1, LastLocalModified = T0 },
            ],
        });

        await using var db = testDb.NewContext();
        Assert.Equal(2, db.CanonicalBookmarks.Count(b => b.Title == "Example"));
        Assert.Equal(2, db.ClientBookmarkMappings.Count(m => m.ClientId == clientId && m.NativeId != "1" && m.NativeId != "2" && m.NativeId != "3"));
    }

    [Fact]
    public async Task SameTitleButDifferentUrl_IsNotMerged()
    {
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chrome = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        var edge = new SyncEngine(testDb.NewContext(), TimeProvider.System);

        await chrome.ReconcileAsync(chromeId, BrowserKind.Chrome, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes = [.. Roots("1", "2", "3"), new BookmarkSnapshotNode { NativeId = "10", ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = "Docs", Url = "https://a.example.com", Index = 0, LastLocalModified = T0 }],
        });
        await edge.ReconcileAsync(edgeId, BrowserKind.Edge, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes = [.. Roots("1", "30", "164"), new BookmarkSnapshotNode { NativeId = "900", ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = "Docs", Url = "https://b.example.com", Index = 0, LastLocalModified = T0 }],
        });

        await using var db = testDb.NewContext();
        Assert.Equal(2, db.CanonicalBookmarks.Count(b => b.Title == "Docs"));
    }

    [Fact]
    public async Task IdenticalTitlesUnderDifferentParents_AreNotMerged()
    {
        // "Raspberry PI" under Mobile and "Raspberry PI" under Bookmarks Bar are different folders.
        using var testDb = new TestDb();
        var chromeId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var chrome = new SyncEngine(testDb.NewContext(), TimeProvider.System);
        var edge = new SyncEngine(testDb.NewContext(), TimeProvider.System);

        await chrome.ReconcileAsync(chromeId, BrowserKind.Chrome, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes = [.. Roots("1", "2", "3"), new BookmarkSnapshotNode { NativeId = "80", ParentNativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Raspberry PI", Index = 0, LastLocalModified = T0 }],
        });
        await edge.ReconcileAsync(edgeId, BrowserKind.Edge, new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes = [.. Roots("1", "30", "164"), new BookmarkSnapshotNode { NativeId = "900", ParentNativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Raspberry PI", Index = 0, LastLocalModified = T0 }],
        });

        await using var db = testDb.NewContext();
        var folders = db.CanonicalBookmarks.Where(b => b.Title == "Raspberry PI").ToList();
        Assert.Equal(2, folders.Count);
        Assert.Contains(folders, f => f.ParentId == WellKnownRoots.MobileBookmarks);
        Assert.Contains(folders, f => f.ParentId == WellKnownRoots.BookmarksBar);
    }
}
