namespace BrowserSync.Core.Data.Entities;

/// <summary>Records a delete so a client that reconnects later learns about it
/// instead of resurrecting the item during reconciliation. Pruned after a retention window.</summary>
public class Tombstone
{
    public Guid CanonicalId { get; set; }
    public DateTime DeletedAtUtc { get; set; }
    public Guid? DeletedByClientId { get; set; }
}
