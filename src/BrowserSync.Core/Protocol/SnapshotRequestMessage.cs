namespace BrowserSync.Core.Protocol;

/// <summary>Host -&gt; client: asks the client to send a fresh full-tree <see cref="SnapshotMessage"/>.
/// Sent once right after `hello` (via <see cref="HelloAckMessage"/>) and again on every periodic
/// reconciliation tick as a safety net for missed real-time events.</summary>
public class SnapshotRequestMessage : BsMessage;
