namespace BrowserSync.Core.Protocol;

public enum BookmarkEventOp
{
    Created,
    Changed,
    Moved,
    Removed,
    Reordered,
}

/// <summary>A single real-time bookmark change forwarded by the extension as it happens.</summary>
public class BookmarkEventMessage : BsMessage
{
    public BookmarkEventOp Op { get; set; }
    public string NativeId { get; set; } = string.Empty;
    public string? ParentNativeId { get; set; }
    public int Index { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public DateTime Timestamp { get; set; }
}
