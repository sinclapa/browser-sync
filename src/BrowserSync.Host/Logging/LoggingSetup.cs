using Serilog;
using Serilog.Events;

namespace BrowserSync.Host.Logging;

public static class LoggingSetup
{
    public static void Configure()
    {
        AppPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // EF logs every command it runs, at Information — for a sync that reconciles a few
            // hundred bookmarks every 30 seconds that was megabytes of SQL per hour, burying the
            // handful of lines worth reading. Warnings and errors from EF still come through.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            // Likewise one "Request starting/finished" pair per WebSocket reconnect.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDirectory, "browsersync-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();
    }
}
