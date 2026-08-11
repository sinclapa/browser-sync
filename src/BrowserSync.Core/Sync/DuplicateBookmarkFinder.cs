using BrowserSync.Core.Protocol;

namespace BrowserSync.Core.Sync;

/// <summary>One group of exact-duplicate nodes found in a client's raw snapshot: same parent
/// folder, same title, same kind, and (for bookmarks) same URL. Keeps the oldest (lowest index)
/// copy and flags the rest.</summary>
public sealed record DuplicateNodeGroup(
    string? ParentNativeId,
    string Title,
    SnapshotNodeKind Kind,
    string? Url,
    string NativeIdToKeep,
    IReadOnlyList<string> NativeIdsToRemove);

/// <summary>
/// Finds exact-duplicate nodes within one client's flattened, unfiltered bookmark snapshot — the
/// raw tree the extension reports, before any canonical-mapping logic is applied. This is how
/// orphan duplicates (created by a past bug where two connections for the same client both
/// independently pushed a `create` command for the same not-yet-mapped item, or where a client's
/// persisted ID reset and it was re-adopted as a "new" device) can be found and cleaned up: the
/// host never had a mapping for the extra copy, so canonical/reconciliation state alone can't
/// reveal it — only the client's own raw tree can.
///
/// Bookmarks match on parent+title+URL, which is unambiguous. Folders match on parent+title
/// alone — weaker evidence than a URL match, since two folders with the same name aren't
/// inherently the same folder the way two bookmarks with the same URL are. Treat folder-removal
/// results with extra scrutiny before applying: removing a duplicate folder removes its entire
/// contents too (via removeTree), which is exactly what's wanted when the folder is a clone of
/// another (its contents are themselves duplicates, invisible to bookmark-level matching alone
/// because they sit under a different, also-duplicated parent ID) — but wrong for a folder that
/// only coincidentally shares a name.
/// </summary>
public static class DuplicateBookmarkFinder
{
    public static IReadOnlyList<DuplicateNodeGroup> FindDuplicates(IReadOnlyList<BookmarkSnapshotNode> nodes)
    {
        var groups = nodes
            .Where(n => n.Kind == SnapshotNodeKind.Folder || !string.IsNullOrEmpty(n.Url))
            .GroupBy(n => (n.ParentNativeId, n.Title, n.Kind, Url: n.Kind == SnapshotNodeKind.Bookmark ? n.Url : null))
            .Where(g => g.Count() > 1);

        var result = new List<DuplicateNodeGroup>();
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(n => n.Index).ThenBy(n => n.NativeId, StringComparer.Ordinal).ToList();
            var keep = ordered[0];
            var remove = ordered.Skip(1).Select(n => n.NativeId).ToList();
            result.Add(new DuplicateNodeGroup(group.Key.ParentNativeId, group.Key.Title, group.Key.Kind, group.Key.Url, keep.NativeId, remove));
        }

        return result;
    }
}
