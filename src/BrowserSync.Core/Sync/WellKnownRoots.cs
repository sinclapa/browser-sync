namespace BrowserSync.Core.Sync;

/// <summary>
/// Fixed canonical IDs for the browser's three built-in root folders (Bookmarks Bar / Other /
/// Mobile). These are stable, host-assigned GUIDs — NOT native bookmark IDs. Each client's own
/// native ID for a given root varies per browser/profile (see <see cref="Protocol.BookmarkSnapshotNode.Role"/>)
/// and is learned dynamically from that client's snapshot rather than assumed.
/// </summary>
public static class WellKnownRoots
{
    public static readonly Guid BookmarksBar = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid OtherBookmarks = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid MobileBookmarks = new("00000000-0000-0000-0000-000000000003");

    public const string BookmarksBarRole = "bookmarksBar";
    public const string OtherRole = "other";
    public const string MobileRole = "mobile";

    public static Guid? CanonicalIdForRole(string? role) => role switch
    {
        BookmarksBarRole => BookmarksBar,
        OtherRole => OtherBookmarks,
        MobileRole => MobileBookmarks,
        _ => null,
    };

    public static bool IsRoot(Guid canonicalId) =>
        canonicalId == BookmarksBar || canonicalId == OtherBookmarks || canonicalId == MobileBookmarks;
}
