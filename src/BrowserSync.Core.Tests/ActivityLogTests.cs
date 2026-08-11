using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;
using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

/// <summary>Checks the activity log captures enough to reverse each kind of change by hand —
/// the full path (so you know where to look), and the previous value or order (so you know what
/// to put back).</summary>
public class ActivityLogTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Recorder : IActivityLog
    {
        public List<ActivityRecord> Records { get; } = [];

        public void Record(ActivityRecord record) => Records.Add(record);
    }

    private static List<BookmarkSnapshotNode> Roots() =>
    [
        new() { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bar", Role = WellKnownRoots.BookmarksBarRole, Index = 0, LastLocalModified = T0 },
        new() { NativeId = "2", Kind = SnapshotNodeKind.Folder, Title = "Other", Role = WellKnownRoots.OtherRole, Index = 1, LastLocalModified = T0 },
        new() { NativeId = "3", Kind = SnapshotNodeKind.Folder, Title = "Mobile", Role = WellKnownRoots.MobileRole, Index = 2, LastLocalModified = T0 },
    ];

    private static async Task<(SyncEngine Engine, Recorder Log, Guid ClientId)> SetupAsync(TestDb testDb)
    {
        var log = new Recorder();
        var clientId = Guid.NewGuid();
        var engine = new SyncEngine(testDb.NewContext(), TimeProvider.System, null, log);
        await engine.EnsureClientAsync(clientId, BrowserKind.Chrome);
        await engine.EnsureRoleRootMappingsAsync(clientId, Roots());
        return (engine, log, clientId);
    }

    private static BookmarkEventMessage Created(string nativeId, string parent, string title, string? url, DateTime at) =>
        new() { Op = BookmarkEventOp.Created, NativeId = nativeId, ParentNativeId = parent, Index = 0, Title = title, Url = url, Timestamp = at };

    [Fact]
    public async Task AddingABookmark_RecordsFullPathAndUrl()
    {
        using var testDb = new TestDb();
        var (engine, log, clientId) = await SetupAsync(testDb);

        await engine.ApplyEventAsync(clientId, Created("80", "3", "Raspberry PI", null, T0));
        await engine.ApplyEventAsync(clientId, Created("81", "80", "Fan SHIM", "https://pimoroni.com/fan-shim", T0.AddSeconds(1)));

        var folder = Assert.Single(log.Records, r => r.Kind == ActivityKind.NewFolder);
        Assert.Equal("Mobile bookmarks/Raspberry PI", folder.Path);

        var bookmark = Assert.Single(log.Records, r => r.Kind == ActivityKind.Add);
        Assert.Equal("Mobile bookmarks/Raspberry PI/Fan SHIM", bookmark.Path);
        Assert.Equal("https://pimoroni.com/fan-shim", bookmark.Url);
        Assert.Equal("Chrome", bookmark.SourceBrowser);
    }

    [Fact]
    public async Task DeletingAFolder_RecordsEveryItemLostWithItsPathAndUrl()
    {
        using var testDb = new TestDb();
        var (engine, log, clientId) = await SetupAsync(testDb);
        await engine.ApplyEventAsync(clientId, Created("80", "3", "Raspberry PI", null, T0));
        await engine.ApplyEventAsync(clientId, Created("81", "80", "Fan SHIM", "https://pimoroni.com/fan-shim", T0.AddSeconds(1)));
        log.Records.Clear();

        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Removed, NativeId = "80", Timestamp = T0.AddSeconds(2),
        });

        // The folder AND its contents, each recoverable from its own line.
        var deletes = log.Records.Where(r => r.Kind == ActivityKind.Delete).ToList();
        Assert.Equal(2, deletes.Count);
        Assert.Contains(deletes, d => d.Path == "Mobile bookmarks/Raspberry PI");

        var lost = Assert.Single(deletes, d => d.Path == "Mobile bookmarks/Raspberry PI/Fan SHIM");
        Assert.Equal("https://pimoroni.com/fan-shim", lost.Url);
        Assert.Equal("Chrome", lost.SourceBrowser); // which browser it was deleted from
    }

    [Fact]
    public async Task RenamingABookmark_RecordsTheOldTitle()
    {
        using var testDb = new TestDb();
        var (engine, log, clientId) = await SetupAsync(testDb);
        await engine.ApplyEventAsync(clientId, Created("10", "1", "Old Name", "https://example.com", T0));
        log.Records.Clear();

        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Changed, NativeId = "10", Title = "New Name", Url = "https://example.com", Timestamp = T0.AddSeconds(5),
        });

        var rename = Assert.Single(log.Records, r => r.Kind == ActivityKind.Rename);
        Assert.Equal("Bookmarks bar/New Name", rename.Path);
        Assert.Equal("\"Old Name\"", rename.Before);
        Assert.Equal("\"New Name\"", rename.After);
    }

    [Fact]
    public async Task ReorderingAFolder_RecordsTheOrderBeforeAndAfter()
    {
        using var testDb = new TestDb();
        var (engine, log, clientId) = await SetupAsync(testDb);
        await engine.ApplyEventAsync(clientId, Created("10", "1", "A", "https://example.com/a", T0));
        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Created, NativeId = "11", ParentNativeId = "1", Index = 1, Title = "B", Url = "https://example.com/b", Timestamp = T0,
        });
        log.Records.Clear();

        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved, NativeId = "11", ParentNativeId = "1", Index = 0, Timestamp = T0.AddSeconds(5),
        });

        var reorder = Assert.Single(log.Records, r => r.Kind == ActivityKind.Reorder);
        Assert.Equal("Bookmarks bar", reorder.Path);
        Assert.Equal("[A, B]", reorder.Before);
        Assert.Equal("[B, A]", reorder.After);
    }

    [Fact]
    public async Task MovingBetweenFolders_RecordsWhereItCameFrom()
    {
        using var testDb = new TestDb();
        var (engine, log, clientId) = await SetupAsync(testDb);
        await engine.ApplyEventAsync(clientId, Created("70", "2", "Work", null, T0));
        await engine.ApplyEventAsync(clientId, Created("10", "1", "Docs", "https://example.com", T0));
        log.Records.Clear();

        await engine.ApplyEventAsync(clientId, new BookmarkEventMessage
        {
            Op = BookmarkEventOp.Moved, NativeId = "10", ParentNativeId = "70", Index = 0, Timestamp = T0.AddSeconds(5),
        });

        var move = Assert.Single(log.Records, r => r.Kind == ActivityKind.Move);
        Assert.Equal("Other bookmarks/Work/Docs", move.Path);
        Assert.Equal("Bookmarks bar/Docs", move.Before);
        Assert.Equal("Other bookmarks/Work/Docs", move.After);
    }
}
