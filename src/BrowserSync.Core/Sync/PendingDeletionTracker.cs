using System.Collections.Concurrent;

namespace BrowserSync.Core.Sync;

/// <summary>
/// Requires the same (client, canonical item) to be independently reported missing on two
/// separate reconciliation passes before <see cref="SyncEngine"/> actually tombstones it — a
/// single reconciliation's "this item is missing" observation is not enough on its own.
///
/// This exists because a truncated or incomplete snapshot (e.g. the extension's service worker
/// being torn down mid-build) previously made the host believe a large number of real bookmarks
/// had been deleted locally, and it deleted them for real, on both sides. A confirmed delete
/// still propagates within two reconciliation passes (which in practice run every time a
/// browser reconnects, not on some slow fixed interval), but a single bad snapshot can no
/// longer destroy data by itself.
/// </summary>
public sealed class PendingDeletionTracker
{
    private readonly ConcurrentDictionary<(Guid ClientId, Guid CanonicalId), byte> _pending = new();

    /// <summary>Call once per (client, canonical) pair the differ reports as missing this pass.
    /// Returns true only once the SAME pair has been reported missing on a previous, separate
    /// call — i.e. it's safe to actually delete now. Returns false (and just records it) the
    /// first time.</summary>
    public bool ConfirmMissing(Guid clientId, Guid canonicalId)
    {
        var key = (clientId, canonicalId);
        if (_pending.TryRemove(key, out _))
            return true; // already pending from a previous, separate pass -> confirmed

        _pending[key] = 0;
        return false; // first time seen missing; wait for reconfirmation
    }

    /// <summary>Call for anything the current snapshot shows is still present, so a transient
    /// single-pass miss doesn't linger and trip the next unrelated disappearance.</summary>
    public void ClearIfPresent(Guid clientId, Guid canonicalId) =>
        _pending.TryRemove((clientId, canonicalId), out _);
}
