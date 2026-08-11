using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

public class SnapshotCompletenessGuardTests
{
    [Fact]
    public void NothingMissing_IsNotSuspect()
    {
        Assert.False(SnapshotCompletenessGuard.IsSuspect(trackedItemCount: 200, missingItemCount: 0));
    }

    [Fact]
    public void AFewItemsMissingFromALargeCollection_IsNotSuspect()
    {
        // A handful of genuine deletions must still flow through normally.
        Assert.False(SnapshotCompletenessGuard.IsSuspect(trackedItemCount: 200, missingItemCount: 5));
    }

    [Fact]
    public void HalfTheCollectionMissing_IsSuspect()
    {
        // The real incident: 222 tracked items, ~112 vanished from one snapshot.
        Assert.True(SnapshotCompletenessGuard.IsSuspect(trackedItemCount: 222, missingItemCount: 112));
    }

    [Fact]
    public void EverythingMissing_IsSuspect()
    {
        Assert.True(SnapshotCompletenessGuard.IsSuspect(trackedItemCount: 200, missingItemCount: 200));
    }

    [Fact]
    public void SmallCollections_AreNeverJudgedSuspect()
    {
        // Proportional reasoning is meaningless at this size — deleting 2 of 5 bookmarks is
        // entirely normal, and the two-pass confirmation plus manual review cover this range.
        Assert.False(SnapshotCompletenessGuard.IsSuspect(trackedItemCount: 5, missingItemCount: 5));
    }

    [Fact]
    public void JustUnderAndJustOverTheThreshold()
    {
        Assert.False(SnapshotCompletenessGuard.IsSuspect(trackedItemCount: 100, missingItemCount: 20)); // exactly 20%
        Assert.True(SnapshotCompletenessGuard.IsSuspect(trackedItemCount: 100, missingItemCount: 21));
    }
}
