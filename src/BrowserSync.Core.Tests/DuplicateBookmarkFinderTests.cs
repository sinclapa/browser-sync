using BrowserSync.Core.Protocol;
using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

public class DuplicateBookmarkFinderTests
{
    private static BookmarkSnapshotNode Bookmark(string nativeId, string? parentNativeId, string title, string url, int index) =>
        new() { NativeId = nativeId, ParentNativeId = parentNativeId, Kind = SnapshotNodeKind.Bookmark, Title = title, Url = url, Index = index, LastLocalModified = DateTime.UnixEpoch };

    private static BookmarkSnapshotNode Folder(string nativeId, string? parentNativeId, string title, int index) =>
        new() { NativeId = nativeId, ParentNativeId = parentNativeId, Kind = SnapshotNodeKind.Folder, Title = title, Index = index, LastLocalModified = DateTime.UnixEpoch };

    [Fact]
    public void NoDuplicates_ReturnsEmpty()
    {
        var nodes = new[]
        {
            Bookmark("10", "1", "Example", "https://example.com", 0),
            Bookmark("11", "1", "Other", "https://other.com", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        Assert.Empty(result);
    }

    [Fact]
    public void ExactDuplicate_SameParentTitleAndUrl_IsReported()
    {
        var nodes = new[]
        {
            Bookmark("10", "1", "Example", "https://example.com", 0),
            Bookmark("42", "1", "Example", "https://example.com", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        var group = Assert.Single(result);
        Assert.Equal("10", group.NativeIdToKeep); // lowest index kept
        Assert.Equal(["42"], group.NativeIdsToRemove);
    }

    [Fact]
    public void ThreeCopies_KeepsOneAndFlagsTheOtherTwo()
    {
        var nodes = new[]
        {
            Bookmark("20", "1", "Example", "https://example.com", 2),
            Bookmark("10", "1", "Example", "https://example.com", 0),
            Bookmark("30", "1", "Example", "https://example.com", 5),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        var group = Assert.Single(result);
        Assert.Equal("10", group.NativeIdToKeep);
        Assert.Equal(["20", "30"], group.NativeIdsToRemove);
    }

    [Fact]
    public void SameTitleAndUrlInDifferentFolders_IsNotADuplicate()
    {
        var nodes = new[]
        {
            Bookmark("10", "1", "Example", "https://example.com", 0),
            Bookmark("11", "2", "Example", "https://example.com", 0),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        Assert.Empty(result);
    }

    [Fact]
    public void SameTitleDifferentUrl_IsNotADuplicate()
    {
        var nodes = new[]
        {
            Bookmark("10", "1", "Example", "https://example.com/a", 0),
            Bookmark("11", "1", "Example", "https://example.com/b", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        Assert.Empty(result);
    }

    [Fact]
    public void DuplicateFolders_SameParentAndTitle_AreReported()
    {
        var nodes = new[]
        {
            Folder("10", "1", "Work", 0),
            Folder("11", "1", "Work", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        var group = Assert.Single(result);
        Assert.Equal(SnapshotNodeKind.Folder, group.Kind);
        Assert.Null(group.Url);
        Assert.Equal("10", group.NativeIdToKeep);
        Assert.Equal(["11"], group.NativeIdsToRemove);
    }

    [Fact]
    public void FolderAndBookmarkWithTheSameTitle_AreNotConfusedWithEachOther()
    {
        var nodes = new[]
        {
            Folder("10", "1", "Example", 0),
            Bookmark("11", "1", "Example", "https://example.com", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        Assert.Empty(result);
    }

    [Fact]
    public void DuplicateFolderContents_AreCaughtEvenThoughEachCopyHasADifferentParentId()
    {
        // The bug this guards against: two clone copies of "Intranet" (native ids 10 and 20)
        // each have their own "Home" bookmark underneath. Bookmark-level matching alone would
        // never group native ids 12/22 together, since their ParentNativeId differs (10 vs 20)
        // — only detecting the folder duplicate itself makes the whole clone cleanable via
        // removeTree.
        var nodes = new[]
        {
            Folder("10", "1", "Intranet", 0),
            Bookmark("12", "10", "Home", "https://intranet.example.com", 0),
            Folder("20", "1", "Intranet", 1),
            Bookmark("22", "20", "Home", "https://intranet.example.com", 0),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        var group = Assert.Single(result);
        Assert.Equal(SnapshotNodeKind.Folder, group.Kind);
        Assert.Equal("10", group.NativeIdToKeep);
        Assert.Equal(["20"], group.NativeIdsToRemove);
    }

    // The finder has no special-casing for which root a bookmark lives under — it just groups
    // by (ParentNativeId, Title, Url) over whatever nodes it's given. These tests lock in that
    // duplicates under "Other Bookmarks" (native id "2") and "Mobile Bookmarks" (native id "3"),
    // not just the Bookmarks Bar ("1"), are found — since the extension's snapshot includes the
    // whole chrome.bookmarks.getTree(), all three roots are already covered.

    [Fact]
    public void DuplicateDirectlyUnderOtherBookmarksRoot_IsReported()
    {
        var nodes = new[]
        {
            Bookmark("10", "2", "Example", "https://example.com", 0),
            Bookmark("42", "2", "Example", "https://example.com", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        var group = Assert.Single(result);
        Assert.Equal("2", group.ParentNativeId);
        Assert.Equal("10", group.NativeIdToKeep);
        Assert.Equal(["42"], group.NativeIdsToRemove);
    }

    [Fact]
    public void DuplicateDirectlyUnderMobileBookmarksRoot_IsReported()
    {
        var nodes = new[]
        {
            Bookmark("10", "3", "Example", "https://example.com", 0),
            Bookmark("42", "3", "Example", "https://example.com", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        var group = Assert.Single(result);
        Assert.Equal("3", group.ParentNativeId);
        Assert.Equal("10", group.NativeIdToKeep);
        Assert.Equal(["42"], group.NativeIdsToRemove);
    }

    [Fact]
    public void DuplicateInASubfolderNestedUnderOtherBookmarks_IsReported()
    {
        var nodes = new[]
        {
            Folder("100", "2", "Recipes", 0), // subfolder under Other Bookmarks
            Bookmark("10", "100", "Example", "https://example.com", 0),
            Bookmark("42", "100", "Example", "https://example.com", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        var group = Assert.Single(result);
        Assert.Equal("100", group.ParentNativeId);
        Assert.Equal("10", group.NativeIdToKeep);
        Assert.Equal(["42"], group.NativeIdsToRemove);
    }

    [Fact]
    public void DuplicatesAcrossAllThreeRoots_AreAllReportedInOnePass()
    {
        var nodes = new[]
        {
            Bookmark("10", "1", "Bar Dup", "https://bar.example.com", 0), // Bookmarks Bar
            Bookmark("11", "1", "Bar Dup", "https://bar.example.com", 1),
            Bookmark("20", "2", "Other Dup", "https://other.example.com", 0), // Other Bookmarks
            Bookmark("21", "2", "Other Dup", "https://other.example.com", 1),
            Bookmark("30", "3", "Mobile Dup", "https://mobile.example.com", 0), // Mobile Bookmarks
            Bookmark("31", "3", "Mobile Dup", "https://mobile.example.com", 1),
        };

        var result = DuplicateBookmarkFinder.FindDuplicates(nodes);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, g => g.ParentNativeId == "1" && g.NativeIdsToRemove.Contains("11"));
        Assert.Contains(result, g => g.ParentNativeId == "2" && g.NativeIdsToRemove.Contains("21"));
        Assert.Contains(result, g => g.ParentNativeId == "3" && g.NativeIdsToRemove.Contains("31"));
    }
}
