namespace BrowserSync.Core.Protocol;

public enum SyncCommandOpKind
{
    Create,
    Update,
    Move,
    Remove,

    /// <summary>Sets the complete order of one folder's children, rather than pushing a single
    /// item to an absolute index. Positions can't be synced reliably as a bare index:
    /// `chrome.bookmarks.move` does not interpret an index the same way `onMoved` reports one
    /// (moving down within a folder is off by one), and the two browsers' folders may not even
    /// hold the same items, so "canonical position 3" need not be position 3 over there.
    /// Applying an explicit sequence front-to-back is correct under either interpretation and
    /// regardless of extra/missing items.</summary>
    Reorder,
}

/// <summary>One operation the client must apply via chrome.bookmarks.* to catch up to canonical state.</summary>
public class SyncCommandOp
{
    public SyncCommandOpKind Op { get; set; }
    public Guid CanonicalId { get; set; }
    public string? NativeId { get; set; }
    public string? ParentNativeId { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public int? Index { get; set; }

    /// <summary>For <see cref="SyncCommandOpKind.Reorder"/>: this client's native IDs for the
    /// folder's children, in the order they should appear.</summary>
    public List<string>? OrderedNativeIds { get; set; }
}

/// <summary>Host -&gt; client batch of operations, sent from both the real-time fan-out
/// path and the reconciliation path.</summary>
public class SyncCommandMessage : BsMessage
{
    public Guid BatchId { get; set; }
    public List<SyncCommandOp> Ops { get; set; } = [];
}
