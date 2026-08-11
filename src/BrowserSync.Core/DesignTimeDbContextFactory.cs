using BrowserSync.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BrowserSync.Core;

/// <summary>Lets `dotnet ef migrations add` construct the DbContext without needing the Host project running.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BrowserSyncDbContext>
{
    public BrowserSyncDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<BrowserSyncDbContext>();
        optionsBuilder.UseSqlite("Data Source=designtime.db");
        return new BrowserSyncDbContext(optionsBuilder.Options);
    }
}
