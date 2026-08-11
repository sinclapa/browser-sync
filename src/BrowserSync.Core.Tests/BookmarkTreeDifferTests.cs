using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;
using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

public class BookmarkTreeDifferTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NodeOnlyInSnapshot_IsReportedAsNewFromClient()
    {
        var node = new BookmarkSnapshotNode { NativeId = "10", Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = Now };

        var result = BookmarkTreeDiffer.Diff([], [], [node], new HashSet<Guid>());

        var entry = Assert.Single(result);
        Assert.Equal(DiffKind.NewFromClient, entry.Kind);
        Assert.Same(node, entry.SnapshotNode);
    }

    [Fact]
    public void CanonicalNodeNotMappedForClient_IsReportedAsNewForClient()
    {
        var canonicalId = Guid.NewGuid();
        var canonical = new CanonicalBookmark { Id = canonicalId, Title = "Example", SortIndex = 0, LastModifiedUtc = Now };

        var result = BookmarkTreeDiffer.Diff([canonical], [], [], new HashSet<Guid>());

        var entry = Assert.Single(result);
        Assert.Equal(DiffKind.NewForClient, entry.Kind);
        Assert.Equal(canonicalId, entry.CanonicalId);
    }

    [Fact]
    public void MatchingMappedNode_ProducesNoDiff()
    {
        var clientId = Guid.NewGuid();
        var canonicalId = Guid.NewGuid();
        var canonical = new CanonicalBookmark { Id = canonicalId, Title = "Example", Url = "https://example.com", SortIndex = 0, LastModifiedUtc = Now };
        var mapping = new ClientBookmarkMapping { ClientId = clientId, CanonicalId = canonicalId, NativeId = "10" };
        var node = new BookmarkSnapshotNode { NativeId = "10", Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = Now };

        var result = BookmarkTreeDiffer.Diff([canonical], [mapping], [node], new HashSet<Guid>());

        Assert.Empty(result);
    }

    [Fact]
    public void MappedNodeWithDifferentTitle_IsReportedAsChanged()
    {
        var clientId = Guid.NewGuid();
        var canonicalId = Guid.NewGuid();
        var canonical = new CanonicalBookmark { Id = canonicalId, Title = "Old Title", Url = "https://example.com", SortIndex = 0, LastModifiedUtc = Now };
        var mapping = new ClientBookmarkMapping { ClientId = clientId, CanonicalId = canonicalId, NativeId = "10" };
        var node = new BookmarkSnapshotNode { NativeId = "10", Title = "New Title", Url = "https://example.com", Index = 0, LastLocalModified = Now.AddSeconds(1) };

        var result = BookmarkTreeDiffer.Diff([canonical], [mapping], [node], new HashSet<Guid>());

        var entry = Assert.Single(result);
        Assert.Equal(DiffKind.Changed, entry.Kind);
        Assert.Equal(canonicalId, entry.CanonicalId);
    }

    [Fact]
    public void MappedNodeWithDifferentParent_IsReportedAsChanged()
    {
        var clientId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var canonicalId = Guid.NewGuid();
        var folder = new CanonicalBookmark { Id = folderId, Kind = BookmarkKind.Folder, Title = "Folder", SortIndex = 0, LastModifiedUtc = Now };
        var canonical = new CanonicalBookmark { Id = canonicalId, ParentId = null, Title = "Example", Url = "https://example.com", SortIndex = 0, LastModifiedUtc = Now };
        var folderMapping = new ClientBookmarkMapping { ClientId = clientId, CanonicalId = folderId, NativeId = "20" };
        var mapping = new ClientBookmarkMapping { ClientId = clientId, CanonicalId = canonicalId, NativeId = "10" };
        var folderNode = new BookmarkSnapshotNode { NativeId = "20", Title = "Folder", Index = 0, LastLocalModified = Now };
        // Client has since moved the bookmark into the folder.
        var node = new BookmarkSnapshotNode { NativeId = "10", ParentNativeId = "20", Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = Now.AddSeconds(1) };

        var result = BookmarkTreeDiffer.Diff([folder, canonical], [folderMapping, mapping], [folderNode, node], new HashSet<Guid>());

        var entry = Assert.Single(result);
        Assert.Equal(DiffKind.Changed, entry.Kind);
        Assert.Equal(canonicalId, entry.CanonicalId);
    }

    [Fact]
    public void ClientStillHoldingATombstonedNode_IsReportedAsClientHasTombstoned()
    {
        var clientId = Guid.NewGuid();
        var canonicalId = Guid.NewGuid();
        var mapping = new ClientBookmarkMapping { ClientId = clientId, CanonicalId = canonicalId, NativeId = "10" };
        var node = new BookmarkSnapshotNode { NativeId = "10", Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = Now };

        var result = BookmarkTreeDiffer.Diff([], [mapping], [node], new HashSet<Guid> { canonicalId });

        var entry = Assert.Single(result);
        Assert.Equal(DiffKind.ClientHasTombstoned, entry.Kind);
        Assert.Equal(canonicalId, entry.CanonicalId);
        Assert.Equal("10", entry.NativeId);
    }

    [Fact]
    public void MappedNodeMissingFromSnapshot_IsReportedAsClientDeletedLocally()
    {
        var clientId = Guid.NewGuid();
        var canonicalId = Guid.NewGuid();
        var canonical = new CanonicalBookmark { Id = canonicalId, Title = "Example", SortIndex = 0, LastModifiedUtc = Now };
        var mapping = new ClientBookmarkMapping { ClientId = clientId, CanonicalId = canonicalId, NativeId = "10" };

        var result = BookmarkTreeDiffer.Diff([canonical], [mapping], [], new HashSet<Guid>());

        var entry = Assert.Single(result);
        Assert.Equal(DiffKind.ClientDeletedLocally, entry.Kind);
        Assert.Equal(canonicalId, entry.CanonicalId);
        Assert.Equal("10", entry.NativeId);
    }

    [Fact]
    public void RootFolders_AreNeverReportedAsNewForClient()
    {
        var root = new CanonicalBookmark { Id = WellKnownRoots.BookmarksBar, RoleRoot = RootRole.BookmarksBar, Title = "Bookmarks Bar", SortIndex = 0, LastModifiedUtc = Now };

        var result = BookmarkTreeDiffer.Diff([root], [], [], new HashSet<Guid>());

        Assert.Empty(result);
    }
}
