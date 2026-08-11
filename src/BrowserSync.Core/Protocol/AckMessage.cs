namespace BrowserSync.Core.Protocol;

/// <summary>Reports the native ID the browser assigned to a host-initiated create.</summary>
public class AckCreatedItem
{
    public Guid CanonicalId { get; set; }
    public string NativeId { get; set; } = string.Empty;
}

/// <summary>Client -&gt; host, sent after a `command` batch has been applied locally.</summary>
public class AckMessage : BsMessage
{
    public Guid BatchId { get; set; }
    public List<AckCreatedItem> Created { get; set; } = [];
}
