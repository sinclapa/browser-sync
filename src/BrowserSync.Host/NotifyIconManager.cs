using System.Drawing;
using System.Windows.Forms;
using BrowserSync.Core.Data.Entities;

namespace BrowserSync.Host;

/// <summary>Owns the tray icon and its context menu. Raises events; doesn't know how to act on them.</summary>
public sealed class NotifyIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _chromeCountItem;
    private readonly ToolStripMenuItem _edgeCountItem;
    private readonly ToolStripMenuItem _startupItem;
    private bool _suppressToggleEvent;

    public event EventHandler? SyncNowRequested;
    public event EventHandler? OpenLogsRequested;
    public event EventHandler? RemoveDuplicatesRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler<bool>? ToggleStartupRequested;

    public NotifyIconManager()
    {
        _statusItem = new ToolStripMenuItem("Not yet synced") { Enabled = false };
        _chromeCountItem = new ToolStripMenuItem("Chrome: not connected") { Enabled = false };
        _edgeCountItem = new ToolStripMenuItem("Edge: not connected") { Enabled = false };

        var syncNowItem = new ToolStripMenuItem("Sync now");
        syncNowItem.Click += (_, _) => SyncNowRequested?.Invoke(this, EventArgs.Empty);

        _startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _startupItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressToggleEvent)
                ToggleStartupRequested?.Invoke(this, _startupItem.Checked);
        };

        var openLogsItem = new ToolStripMenuItem("Open logs");
        openLogsItem.Click += (_, _) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);

        var removeDuplicatesItem = new ToolStripMenuItem("Remove detected duplicates...");
        removeDuplicatesItem.Click += (_, _) => RemoveDuplicatesRequested?.Invoke(this, EventArgs.Empty);

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_chromeCountItem);
        menu.Items.Add(_edgeCountItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(syncNowItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(removeDuplicatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "BrowserSync",
            ContextMenuStrip = menu,
            Visible = false,
        };
    }

    /// <summary>Loads the embedded "B" badge, falling back to a stock icon rather than failing
    /// to show a tray entry at all if the resource is ever missing.</summary>
    private static Icon LoadAppIcon()
    {
        var assembly = typeof(NotifyIconManager).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("browsersync.ico", StringComparison.OrdinalIgnoreCase));
        if (resource is null)
            return SystemIcons.Application;

        using var stream = assembly.GetManifestResourceStream(resource);
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    public void Show() => _icon.Visible = true;

    public void SetStartupChecked(bool enabled)
    {
        _suppressToggleEvent = true;
        _startupItem.Checked = enabled;
        _suppressToggleEvent = false;
    }

    public void UpdateStatus(DateTime? lastSyncedUtc)
    {
        _statusItem.Text = lastSyncedUtc is null ? "Not yet synced" : $"Synced {FormatAgo(lastSyncedUtc.Value)}";
    }

    /// <summary>Shows what each browser last reported holding. Straight from their snapshots, so
    /// a mismatch between the two lines means they really are out of step right now.</summary>
    public void UpdateCounts(IReadOnlyList<ClientStats> stats)
    {
        _chromeCountItem.Text = Describe("Chrome", stats.FirstOrDefault(s => s.BrowserKind == BrowserKind.Chrome));
        _edgeCountItem.Text = Describe("Edge", stats.FirstOrDefault(s => s.BrowserKind == BrowserKind.Edge));

        var counts = stats.Select(s => s.BookmarkCount).Distinct().ToList();
        _icon.Text = counts.Count == 1
            ? $"BrowserSync — {counts[0]} bookmarks in sync"
            : "BrowserSync";
    }

    private static string Describe(string label, ClientStats? stats) => stats is null
        ? $"{label}: not connected"
        : $"{label}: {stats.BookmarkCount} bookmarks, {stats.FolderCount} folders";

    private static string FormatAgo(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        return $"{(int)span.TotalHours} hr ago";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
