namespace BrowserSync.Core.Data.Entities;

public enum BookmarkKind
{
    Bookmark = 0,
    Folder = 1,
}

public enum RootRole
{
    None = 0,
    BookmarksBar = 1,
    OtherBookmarks = 2,
    MobileBookmarks = 3,
}

/// <summary>The single source of truth for one bookmark/folder, keyed by a host-assigned GUID
/// that is stable across both browsers (native bookmark IDs are not).</summary>
public class CanonicalBookmark
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public BookmarkKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public int SortIndex { get; set; }
    public RootRole RoleRoot { get; set; } = RootRole.None;
    public DateTime LastModifiedUtc { get; set; }
    public Guid? LastModifiedByClientId { get; set; }
}
