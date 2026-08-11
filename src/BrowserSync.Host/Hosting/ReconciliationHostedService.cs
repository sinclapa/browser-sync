using BrowserSync.Core.Protocol;
using BrowserSync.Host.WebSocketServer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrowserSync.Host.Hosting;

/// <summary>Periodically asks every connected client to send a fresh full-tree snapshot — the
/// safety net that catches changes missed by the real-time event path (e.g. the host was
/// restarted, or a browser's service worker was suspended mid-change).</summary>
public sealed class ReconciliationHostedService(
    ConnectionRegistry registry,
    IOptions<HostSettings> settings,
    ILogger<ReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.Value.ReconciliationIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        do
        {
            await TriggerNowAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task TriggerNowAsync(CancellationToken ct = default)
    {
        foreach (var connection in registry.All)
        {
            try
            {
                await connection.SendAsync(new SnapshotRequestMessage { ClientId = connection.ClientId }, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to request a snapshot from client {ClientId}", connection.ClientId);
            }
        }
    }
}
