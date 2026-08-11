using System.Text.Json.Serialization;

namespace BrowserSync.Core.Protocol;

/// <summary>
/// Base type for every message on the extension &lt;-&gt; host WebSocket connection.
/// Discriminated by the "type" property so a single `JsonSerializer.Deserialize&lt;BsMessage&gt;`
/// call picks the concrete type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(HelloAckMessage), "helloAck")]
[JsonDerivedType(typeof(SnapshotRequestMessage), "requestSnapshot")]
[JsonDerivedType(typeof(SnapshotMessage), "snapshot")]
[JsonDerivedType(typeof(BookmarkEventMessage), "event")]
[JsonDerivedType(typeof(SyncCommandMessage), "command")]
[JsonDerivedType(typeof(AckMessage), "ack")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract class BsMessage
{
    public int V { get; set; } = 1;
    public Guid ClientId { get; set; }
    public long Ts { get; set; }
}
