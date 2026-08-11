using BrowserSync.Core.Data;
using BrowserSync.Core.Sync;
using BrowserSync.Host.Duplicates;
using BrowserSync.Host.Hosting;
using BrowserSync.Host.Logging;
using BrowserSync.Host.WebSocketServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrowserSync.Host;

/// <summary>All DI registrations the host needs, factored out of <c>Program.cs</c> so the
/// wiring itself — not just the individual services — can be exercised by a test
/// (see <c>BrowserSync.Host.Tests</c>). This is what would have caught the missing
/// <see cref="TimeProvider"/> registration that crashed every real snapshot/event at runtime
/// despite every <see cref="SyncEngine"/> unit test passing (those construct it directly).</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddBrowserSyncServices(this IServiceCollection services, IConfiguration configuration, string sqliteConnectionString)
    {
        services.Configure<HostSettings>(configuration.GetSection("BrowserSync"));
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<BrowserSyncDbContext>(options => options.UseSqlite(sqliteConnectionString));
        services.AddScoped<SyncEngine>();
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton<DuplicateReportStore>();
        services.AddSingleton<PendingDeletionTracker>();
        services.AddSingleton<ClientStatsStore>();
        services.AddSingleton<IActivityLog, FileActivityLog>();
        services.AddSingleton<ReconciliationHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<ReconciliationHostedService>());
        services.AddHostedService<TombstonePruningHostedService>();
        return services;
    }
}
