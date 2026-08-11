using BrowserSync.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrowserSync.Host.Hosting;

/// <summary>Removes tombstones (and any stale client mappings pointing at them) once they're
/// older than the retention window — before that, they exist so a late-reconnecting client
/// learns about a delete instead of resurrecting the item during reconciliation.</summary>
public sealed class TombstonePruningHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<HostSettings> settings,
    ILogger<TombstonePruningHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            await PruneAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BrowserSyncDbContext>();

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(1, settings.Value.TombstoneRetentionDays));
        var stale = await db.Tombstones.Where(t => t.DeletedAtUtc < cutoff).ToListAsync(ct);
        if (stale.Count == 0)
            return;

        foreach (var tombstone in stale)
            db.ClientBookmarkMappings.RemoveRange(db.ClientBookmarkMappings.Where(m => m.CanonicalId == tombstone.CanonicalId));

        db.Tombstones.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Pruned {Count} tombstones older than {Cutoff:u}", stale.Count, cutoff);
    }
}
