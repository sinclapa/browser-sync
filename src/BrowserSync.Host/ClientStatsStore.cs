using System.Collections.Concurrent;
using BrowserSync.Core.Data.Entities;

namespace BrowserSync.Host;

/// <summary>How many bookmarks each browser last reported, taken straight from its snapshot.</summary>
public sealed record ClientStats(BrowserKind BrowserKind, int BookmarkCount, int FolderCount, DateTime ReportedAtUtc);

/// <summary>
/// Latest per-browser bookmark counts, shown in the tray menu. These come from what each
/// browser actually reports rather than from canonical state, so the two lines can be compared
/// directly: if Chrome and Edge show different totals, they genuinely differ right now.
/// </summary>
public sealed class ClientStatsStore
{
    private readonly ConcurrentDictionary<Guid, ClientStats> _byClient = new();

    public void Set(Guid clientId, ClientStats stats) => _byClient[clientId] = stats;

    public IReadOnlyList<ClientStats> All =>
        _byClient.Values.OrderBy(s => s.BrowserKind).ToList();
}
