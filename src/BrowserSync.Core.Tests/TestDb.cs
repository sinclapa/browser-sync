using BrowserSync.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BrowserSync.Core.Tests;

/// <summary>A shared, kept-open Sqlite ":memory:" connection so multiple DbContext instances —
/// mirroring the app's per-request scoping in <see cref="SyncEngine" /> usage — can see the
/// same in-memory database within one test.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<BrowserSyncDbContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<BrowserSyncDbContext>().UseSqlite(_connection).Options;

        using var db = new BrowserSyncDbContext(_options);
        db.Database.EnsureCreated();
    }

    public BrowserSyncDbContext NewContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
