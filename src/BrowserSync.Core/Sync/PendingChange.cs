namespace BrowserSync.Core.Sync;

public enum PendingChangeKind
{
    Created,
    ContentChanged,
    PositionChanged,
    Removed,
}

/// <summary>A canonical-state change that still needs to be translated into per-client
/// native-ID commands for every OTHER connected client (see <see cref="SyncEngine.BuildCommandForClientAsync"/>).</summary>
public sealed record PendingChange(Guid CanonicalId, PendingChangeKind Kind);
