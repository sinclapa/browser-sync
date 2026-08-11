using BrowserSync.Core.Protocol;

namespace BrowserSync.Core.Sync;

/// <summary>Result of applying one real-time <see cref="BookmarkEventMessage"/>.</summary>
public sealed class EventApplyResult
{
    public static readonly EventApplyResult None = new();

    /// <summary>Canonical changes to fan out to every OTHER connected client.</summary>
    public IReadOnlyList<PendingChange> ForOthers { get; init; } = [];

    /// <summary>Set when the event lost a last-write-wins conflict against a newer canonical
    /// value — sent straight back to the originating client so it doesn't have to wait for
    /// the next reconciliation pass to be corrected.</summary>
    public SyncCommandOp? CorrectionForSender { get; init; }
}
