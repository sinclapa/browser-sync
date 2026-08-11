using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;
using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

/// <summary>Regression coverage for a real-world sync gap: a first-ever snapshot containing
/// top-level folders directly under "Other Bookmarks" and "Mobile Bookmarks" (not just the
/// Bookmarks Bar) must adopt correctly, including nested bookmarks inside those folders.</summary>
public class OtherMobileRootReproTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Reconcile_FirstSnapshot_AdoptsTopLevelFoldersUnderOtherAndMobileRoots()
    {
        using var testDb = new TestDb();
        var clientId = Guid.NewGuid();
        var db = testDb.NewContext();
        var engine = new SyncEngine(db, TimeProvider.System);

        var snapshot = new SnapshotMessage
        {
            GeneratedAt = T0,
            Nodes =
            [
                new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "2", Kind = SnapshotNodeKind.Folder, Title = "Other Bookmarks", Role = WellKnownRoots.OtherRole, Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Mobile Bookmarks", Role = WellKnownRoots.MobileRole, Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "70", ParentNativeId = "2", Kind = SnapshotNodeKind.Folder, Title = "CookingCode", Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "80", ParentNativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Raspberry PI", Index = 0, LastLocalModified = T0 },
                new BookmarkSnapshotNode { NativeId = "81", ParentNativeId = "80", Kind = SnapshotNodeKind.Bookmark, Title = "Fan SHIM", Url = "https://shop.pimoroni.com/products/fan-shim", Index = 0, LastLocalModified = T0 },
            ],
        };

        await engine.ReconcileAsync(clientId, BrowserKind.Chrome, snapshot);

        await using var check = testDb.NewContext();
        var nonRoot = check.CanonicalBookmarks.Where(b => b.RoleRoot == RootRole.None).ToList();

        Assert.Contains(nonRoot, b => b.Title == "CookingCode");
        Assert.Contains(nonRoot, b => b.Title == "Raspberry PI");
        Assert.Contains(nonRoot, b => b.Title == "Fan SHIM");
    }
}
