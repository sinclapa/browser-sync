using BrowserSync.Core.Protocol;

namespace BrowserSync.Core.Sync;

/// <summary>Result of a full-tree reconciliation pass for one client.</summary>
public sealed class ReconcileResult
{
    /// <summary>Commands to send straight back to the client that submitted the snapshot.</summary>
    public required SyncCommandMessage ForRequester { get; init; }

    /// <summary>Canonical changes to fan out to every OTHER connected client.</summary>
    public IReadOnlyList<PendingChange> ForOthers { get; init; } = [];

    /// <summary>Items this client's snapshot no longer reports, confirmed missing on two
    /// separate reconciliation passes. NOT yet deleted — surfaced for explicit user review via
    /// the tray, never auto-applied. See <see cref="SyncEngine.ConfirmLocalDeletionsAsync"/>.</summary>
    public IReadOnlyList<LocalDeletionCandidate> LocalDeletionCandidates { get; init; } = [];

    /// <summary>True when the snapshot was missing an implausible share of this client's known
    /// items (see <see cref="SnapshotCompletenessGuard"/>), so no absence-based reasoning was
    /// done from it at all. Adds and changes from the snapshot were still applied.</summary>
    public bool SnapshotTooIncompleteForDeletionInference { get; init; }
}
