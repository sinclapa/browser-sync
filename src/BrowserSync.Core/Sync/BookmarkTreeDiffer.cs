using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;

namespace BrowserSync.Core.Sync;

public enum DiffKind
{
    /// <summary>Client has a node the host has no canonical record of yet — host adopts it.</summary>
    NewFromClient,

    /// <summary>Canonical has a node this client doesn't have yet — push a create.</summary>
    NewForClient,

    /// <summary>Both sides know the node, but title/url/parent/index differ — resolve via LWW.</summary>
    Changed,

    /// <summary>Client still holds an item whose canonical record was deleted — push a remove,
    /// never resurrect it.</summary>
    ClientHasTombstoned,

    /// <summary>Client used to map this canonical node but it's missing from its snapshot —
    /// it was deleted locally (e.g. while the host was offline) — tombstone it and fan out.</summary>
    ClientDeletedLocally,
}

public sealed class BookmarkDiffEntry
{
    public required DiffKind Kind { get; init; }
    public Guid? CanonicalId { get; init; }
    public string? NativeId { get; init; }
    public BookmarkSnapshotNode? SnapshotNode { get; init; }
}

/// <summary>
/// Pure diff between one client's flattened bookmark snapshot and canonical state.
/// Assumes the caller has already ensured the three well-known root folders are mapped for
/// this client and stripped root nodes out of <paramref name="snapshotNodes"/> — roots are
/// always considered present and are never reported as new/changed/deleted.
/// </summary>
public static class BookmarkTreeDiffer
{
    public static IReadOnlyList<BookmarkDiffEntry> Diff(
        IReadOnlyList<CanonicalBookmark> canonicalNodes,
        IReadOnlyList<ClientBookmarkMapping> clientMappings,
        IReadOnlyList<BookmarkSnapshotNode> snapshotNodes,
        IReadOnlySet<Guid> tombstonedCanonicalIds)
    {
        var entries = new List<BookmarkDiffEntry>();

        var nativeToCanonical = clientMappings.ToDictionary(m => m.NativeId, m => m.CanonicalId);
        var canonicalToNative = clientMappings.ToDictionary(m => m.CanonicalId, m => m.NativeId);
        var canonicalById = canonicalNodes.ToDictionary(b => b.Id);
        var snapshotByNative = snapshotNodes.ToDictionary(n => n.NativeId);

        foreach (var node in snapshotNodes)
        {
            if (!nativeToCanonical.TryGetValue(node.NativeId, out var canonicalId))
            {
                entries.Add(new BookmarkDiffEntry { Kind = DiffKind.NewFromClient, SnapshotNode = node });
                continue;
            }

            if (tombstonedCanonicalIds.Contains(canonicalId))
            {
                entries.Add(new BookmarkDiffEntry { Kind = DiffKind.ClientHasTombstoned, CanonicalId = canonicalId, NativeId = node.NativeId });
                continue;
            }

            if (!canonicalById.TryGetValue(canonicalId, out var canonical))
                continue; // mapped to neither a live canonical row nor a tombstone — self-heals once the missing side appears

            // A node's recorded parent native ID resolving to nothing (parentCanonicalId stays
            // null) is treated the same as canonical.ParentId being null — both mean "no parent
            // recorded" — rather than as an automatic mismatch.
            Guid? parentCanonicalId = node.ParentNativeId is not null && nativeToCanonical.TryGetValue(node.ParentNativeId, out var pcid)
                ? pcid
                : null;
            var parentMatches = parentCanonicalId == canonical.ParentId;

            var differs = node.Title != canonical.Title
                || node.Url != canonical.Url
                || node.Index != canonical.SortIndex
                || !parentMatches;

            if (differs)
                entries.Add(new BookmarkDiffEntry { Kind = DiffKind.Changed, CanonicalId = canonicalId, SnapshotNode = node });
        }

        foreach (var canonical in canonicalNodes)
        {
            if (canonical.RoleRoot != RootRole.None)
                continue;

            if (!canonicalToNative.TryGetValue(canonical.Id, out var nativeId))
            {
                entries.Add(new BookmarkDiffEntry { Kind = DiffKind.NewForClient, CanonicalId = canonical.Id });
            }
            else if (!snapshotByNative.ContainsKey(nativeId))
            {
                entries.Add(new BookmarkDiffEntry { Kind = DiffKind.ClientDeletedLocally, CanonicalId = canonical.Id, NativeId = nativeId });
            }
        }

        return entries;
    }
}
