namespace BrowserSync.Core.Sync;

/// <summary>
/// Decides whether a client's snapshot looks complete enough to draw *negative* conclusions
/// from — i.e. to treat "item X isn't in here" as evidence X was deleted.
///
/// Additions and modifications are positive signals and are always safe to apply from any
/// snapshot: a partial snapshot can only omit things, never invent them. Absence is the
/// dangerous direction, and a truncated snapshot (e.g. the extension's service worker killed
/// mid-build) looks exactly like a mass deletion. That misreading twice destroyed large batches
/// of real bookmarks, so a snapshot that has lost an implausible share of what the client was
/// known to have is treated as suspect and simply not used for absence-based reasoning.
///
/// Deletes still propagate promptly regardless, because they arrive as explicit `removed`
/// events from the extension's durable queue rather than being inferred here.
/// </summary>
public static class SnapshotCompletenessGuard
{
    /// <summary>Below this, proportional reasoning is meaningless (losing 1 of 3 items is 33%),
    /// so small collections are never judged suspect — the two-pass confirmation and manual
    /// review remain the safeguards there.</summary>
    public const int MinimumTrackedItemsForGuard = 20;

    /// <summary>A real user deleting more than this share of their bookmarks between two
    /// reconciliations is far less likely than a truncated snapshot, so that's the reading
    /// this errs toward.</summary>
    public const double MaxPlausibleMissingFraction = 0.2;

    public static bool IsSuspect(int trackedItemCount, int missingItemCount)
    {
        if (trackedItemCount < MinimumTrackedItemsForGuard)
            return false;

        return missingItemCount > trackedItemCount * MaxPlausibleMissingFraction;
    }
}
