using BrowserSync.Core.Data;
using BrowserSync.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BrowserSync.Core.Sync;

/// <summary>Builds the readable folder path for a canonical item, e.g.
/// "Mobile bookmarks/Raspberry PI/Fan SHIM" — without which a log entry can't be acted on,
/// since a title alone doesn't say where to look.</summary>
public static class BookmarkPath
{
    public static string RootName(RootRole role) => role switch
    {
        RootRole.BookmarksBar => "Bookmarks bar",
        RootRole.OtherBookmarks => "Other bookmarks",
        RootRole.MobileBookmarks => "Mobile bookmarks",
        _ => "?",
    };

    /// <summary>Path of the folder containing <paramref name="canonicalId"/>, excluding the item itself.</summary>
    public static async Task<string> OfParentAsync(BrowserSyncDbContext db, Guid? parentId)
    {
        var segments = new List<string>();
        var currentId = parentId;

        // Bounded so a corrupted parent cycle can't spin here.
        for (var depth = 0; currentId is not null && depth < 64; depth++)
        {
            var node = await db.CanonicalBookmarks.FindAsync(currentId.Value);
            if (node is null)
                break;

            segments.Add(node.RoleRoot != RootRole.None ? RootName(node.RoleRoot) : node.Title);
            if (node.RoleRoot != RootRole.None)
                break;

            currentId = node.ParentId;
        }

        segments.Reverse();
        return segments.Count == 0 ? "?" : string.Join("/", segments);
    }

    /// <summary>Full path including the item's own title.</summary>
    public static async Task<string> OfAsync(BrowserSyncDbContext db, CanonicalBookmark item)
    {
        var parent = await OfParentAsync(db, item.ParentId);
        return $"{parent}/{item.Title}";
    }
}
