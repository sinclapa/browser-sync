using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace BrowserSync.Core.Data;

public class BrowserSyncDbContext(DbContextOptions<BrowserSyncDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<CanonicalBookmark> CanonicalBookmarks => Set<CanonicalBookmark>();
    public DbSet<ClientBookmarkMapping> ClientBookmarkMappings => Set<ClientBookmarkMapping>();
    public DbSet<Tombstone> Tombstones => Set<Tombstone>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(e =>
        {
            e.HasKey(c => c.Id);
        });

        modelBuilder.Entity<CanonicalBookmark>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasOne<CanonicalBookmark>()
                .WithMany()
                .HasForeignKey(b => b.ParentId)
                .OnDelete(DeleteBehavior.NoAction);

            // Seed the three fixed root folders. Chromium assigns native IDs "1"/"2"/"3"
            // to these across both Chrome and Edge, so they are matched by role, not created/renamed/deleted.
            e.HasData(
                new CanonicalBookmark
                {
                    Id = WellKnownRoots.BookmarksBar,
                    ParentId = null,
                    Kind = BookmarkKind.Folder,
                    Title = "Bookmarks Bar",
                    SortIndex = 0,
                    RoleRoot = RootRole.BookmarksBar,
                    LastModifiedUtc = DateTime.UnixEpoch,
                },
                new CanonicalBookmark
                {
                    Id = WellKnownRoots.OtherBookmarks,
                    ParentId = null,
                    Kind = BookmarkKind.Folder,
                    Title = "Other Bookmarks",
                    SortIndex = 1,
                    RoleRoot = RootRole.OtherBookmarks,
                    LastModifiedUtc = DateTime.UnixEpoch,
                },
                new CanonicalBookmark
                {
                    Id = WellKnownRoots.MobileBookmarks,
                    ParentId = null,
                    Kind = BookmarkKind.Folder,
                    Title = "Mobile Bookmarks",
                    SortIndex = 2,
                    RoleRoot = RootRole.MobileBookmarks,
                    LastModifiedUtc = DateTime.UnixEpoch,
                });
        });

        modelBuilder.Entity<ClientBookmarkMapping>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.ClientId, m.CanonicalId }).IsUnique();
            e.HasIndex(m => new { m.ClientId, m.NativeId }).IsUnique();
        });

        modelBuilder.Entity<Tombstone>(e =>
        {
            e.HasKey(t => t.CanonicalId);
        });
    }
}
