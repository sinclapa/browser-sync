using System.Diagnostics;
using System.Windows.Forms;
using BrowserSync.Core.Protocol;
using BrowserSync.Host.Duplicates;
using BrowserSync.Host.Hosting;
using BrowserSync.Host.Startup;
using BrowserSync.Host.WebSocketServer;
using Microsoft.AspNetCore.Builder;

namespace BrowserSync.Host;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly WebApplication _app;
    private readonly ReconciliationHostedService _reconciliation;
    private readonly ConnectionRegistry _registry;
    private readonly DuplicateReportStore _duplicateReports;
    private readonly ClientStatsStore _clientStats;
    private readonly NotifyIconManager _tray;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private DateTime? _lastSyncedUtc;

    public TrayApplicationContext(
        WebApplication app,
        ReconciliationHostedService reconciliation,
        ConnectionRegistry registry,
        DuplicateReportStore duplicateReports,
        ClientStatsStore clientStats)
    {
        _app = app;
        _reconciliation = reconciliation;
        _registry = registry;
        _duplicateReports = duplicateReports;
        _clientStats = clientStats;

        _tray = new NotifyIconManager();
        _tray.SyncNowRequested += async (_, _) => await SyncNowAsync();
        _tray.OpenLogsRequested += (_, _) => OpenLogs();
        _tray.RemoveDuplicatesRequested += async (_, _) => await RemoveDuplicatesAsync();
        _tray.QuitRequested += (_, _) => Quit();
        _tray.ToggleStartupRequested += (_, enabled) =>
        {
            if (enabled) RunKeyManager.Enable();
            else RunKeyManager.Disable();
        };
        _tray.SetStartupChecked(RunKeyManager.IsEnabled());
        _tray.UpdateStatus(null);
        _tray.UpdateCounts(_clientStats.All);
        _tray.Show();

        // Browsers reconnect and re-snapshot every ~30s under MV3, so this cadence keeps the
        // counts roughly current without polling anything.
        _statusTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _statusTimer.Tick += (_, _) =>
        {
            _tray.UpdateStatus(_lastSyncedUtc);
            _tray.UpdateCounts(_clientStats.All);
        };
        _statusTimer.Start();
    }

    private async Task SyncNowAsync()
    {
        await _reconciliation.TriggerNowAsync();
        _lastSyncedUtc = DateTime.UtcNow;
        _tray.UpdateStatus(_lastSyncedUtc);
    }

    private static void OpenLogs()
    {
        Process.Start(new ProcessStartInfo { FileName = AppPaths.LogsDirectory, UseShellExecute = true });
    }

    /// <summary>Sends a targeted `remove` command for every duplicate bookmark most recently
    /// detected (see `duplicates-*.json` in the logs folder) on each currently-connected
    /// browser. These are orphan native bookmarks the canonical store has no mapping for, so
    /// they're removed directly by native ID rather than through the normal sync-engine path.</summary>
    private async Task RemoveDuplicatesAsync()
    {
        var removedAny = false;
        var totalRemoved = 0;

        foreach (var connection in _registry.All)
        {
            var duplicates = _duplicateReports.Get(connection.ClientId);
            if (duplicates.Count == 0)
                continue;

            var ops = duplicates
                .SelectMany(g => g.NativeIdsToRemove)
                .Select(nativeId => new SyncCommandOp { Op = SyncCommandOpKind.Remove, CanonicalId = Guid.Empty, NativeId = nativeId })
                .ToList();
            if (ops.Count == 0)
                continue;

            await connection.SendAsync(new SyncCommandMessage { ClientId = connection.ClientId, BatchId = Guid.NewGuid(), Ops = ops }, CancellationToken.None);
            _duplicateReports.Clear(connection.ClientId);
            removedAny = true;
            totalRemoved += ops.Count;
        }

        MessageBox.Show(
            removedAny
                ? $"Removed {totalRemoved} duplicate bookmark(s). Give it a moment, then use \"Sync now\" to confirm."
                : "No duplicates currently detected on any connected browser. Duplicates are (re)detected every time a browser sends a snapshot — try \"Sync now\" first.",
            "BrowserSync",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }


    private void Quit()
    {
        _statusTimer.Stop();
        _tray.Dispose();
        _app.StopAsync().GetAwaiter().GetResult();
        ExitThread();
    }
}
