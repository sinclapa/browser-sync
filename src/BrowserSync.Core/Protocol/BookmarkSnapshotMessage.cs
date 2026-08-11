namespace BrowserSync.Core.Protocol;

public enum SnapshotNodeKind
{
    Bookmark,
    Folder,
}

/// <summary>One node in a client's flattened bookmark tree snapshot.</summary>
public class BookmarkSnapshotNode
{
    public string NativeId { get; set; } = string.Empty;
    public string? ParentNativeId { get; set; }
    public SnapshotNodeKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public int Index { get; set; }

    /// <summary>"bookmarksBar" | "other" | "mobile" for the three permanent root folders,
    /// null for every other node. The client determines this POSITIONALLY — the three
    /// permanent folders are always exactly the super-root's children, in this fixed order —
    /// never by native ID. Chromium does not guarantee those special folders keep native IDs
    /// "1"/"2"/"3"; on a profile with enough history behind it, the "Other"/"Mobile" roots can
    /// end up with arbitrary IDs (observed in practice: "30" and "164").</summary>
    public string? Role { get; set; }

    public DateTime LastLocalModified { get; set; }
}

/// <summary>Full local bookmark tree, sent by the client on connect and on every
/// periodic reconciliation pass. Flattened (not nested) to make diffing trivial.</summary>
public class SnapshotMessage : BsMessage
{
    public DateTime GeneratedAt { get; set; }
    public List<BookmarkSnapshotNode> Nodes { get; set; } = [];
}
