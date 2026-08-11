using System.Collections.Concurrent;
using BrowserSync.Core.Sync;

namespace BrowserSync.Host.Duplicates;

/// <summary>Holds the most recent duplicate-bookmark findings per connected client (see
/// <see cref="DuplicateBookmarkFinder"/>), so the tray's "Remove detected duplicates" action
/// can act on them without re-requesting a snapshot.</summary>
public sealed class DuplicateReportStore
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<DuplicateNodeGroup>> _byClient = new();

    public void Set(Guid clientId, IReadOnlyList<DuplicateNodeGroup> groups) => _byClient[clientId] = groups;

    public IReadOnlyList<DuplicateNodeGroup> Get(Guid clientId) =>
        _byClient.TryGetValue(clientId, out var groups) ? groups : [];

    public void Clear(Guid clientId) => _byClient.TryRemove(clientId, out _);

    public IReadOnlyDictionary<Guid, IReadOnlyList<DuplicateNodeGroup>> Snapshot() =>
        new Dictionary<Guid, IReadOnlyList<DuplicateNodeGroup>>(_byClient);
}
