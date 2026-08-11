namespace BrowserSync.Core.Data.Entities;

/// <summary>Maps a canonical bookmark to the native chrome.bookmarks ID a specific
/// client (browser) knows it by. Native IDs are per-browser-instance and never shared.</summary>
public class ClientBookmarkMapping
{
    public int Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid CanonicalId { get; set; }
    public string NativeId { get; set; } = string.Empty;
}
