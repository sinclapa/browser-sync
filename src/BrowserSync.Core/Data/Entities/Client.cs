namespace BrowserSync.Core.Data.Entities;

public enum BrowserKind
{
    Chrome = 0,
    Edge = 1,
}

/// <summary>A connected extension install (one per browser profile).</summary>
public class Client
{
    public Guid Id { get; set; }
    public BrowserKind BrowserKind { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? LastReconciledUtc { get; set; }
}
