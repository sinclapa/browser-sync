using System.Windows.Forms;
using BrowserSync.Core.Data;
using BrowserSync.Core.Sync;
using BrowserSync.Host;
using BrowserSync.Host.Duplicates;
using BrowserSync.Host.Hosting;
using BrowserSync.Host.Logging;
using BrowserSync.Host.WebSocketServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        LoggingSetup.Configure();

        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseSerilog();
        // Loopback only — this traffic never needs to (and must not) leave the machine.
        builder.WebHost.UseUrls("http://127.0.0.1:8787");

        builder.Services.AddBrowserSyncServices(builder.Configuration, $"Data Source={AppPaths.DatabasePath}");

        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", BookmarkSyncEndpoint.HandleAsync);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BrowserSyncDbContext>();
            db.Database.Migrate();
        }

        await app.StartAsync();
        Log.Information("BrowserSync host listening on ws://127.0.0.1:8787/ws");

        ApplicationConfiguration.Initialize();

        var reconciliation = app.Services.GetRequiredService<ReconciliationHostedService>();
        var registry = app.Services.GetRequiredService<ConnectionRegistry>();
        var duplicateReports = app.Services.GetRequiredService<DuplicateReportStore>();
        var clientStats = app.Services.GetRequiredService<ClientStatsStore>();
        using var trayContext = new TrayApplicationContext(app, reconciliation, registry, duplicateReports, clientStats);
        Application.Run(trayContext);

        await app.StopAsync();
        Log.CloseAndFlush();
    }
}
