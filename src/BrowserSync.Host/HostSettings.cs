namespace BrowserSync.Host;

public sealed class HostSettings
{
    public int ReconciliationIntervalMinutes { get; set; } = 4;
    public int TombstoneRetentionDays { get; set; } = 30;
}
