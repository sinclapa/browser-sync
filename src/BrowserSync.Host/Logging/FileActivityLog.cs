using System.Text;
using BrowserSync.Core.Sync;

namespace BrowserSync.Host.Logging;

/// <summary>
/// Writes one line per change to <c>activity-yyyyMMdd.log</c>, kept separate from the diagnostic
/// log so it stays readable start-to-finish and can be used to reverse a change by hand.
///
/// Never rotated or trimmed by the app: this is the record of what happened to the user's
/// bookmarks, so losing it silently would defeat the point.
/// </summary>
public sealed class FileActivityLog : IActivityLog
{
    private readonly object _gate = new();

    public void Record(ActivityRecord record)
    {
        var line = Format(record);
        var path = Path.Combine(AppPaths.LogsDirectory, $"activity-{DateTime.Now:yyyyMMdd}.log");

        try
        {
            // Serialized because fan-out to several browsers can produce entries concurrently,
            // and interleaved half-lines would make the log useless for its one purpose.
            lock (_gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Failing to log must never take the sync down with it.
        }
    }

    internal static string Format(ActivityRecord r)
    {
        var direction = r.TargetBrowser is null ? r.SourceBrowser : $"{r.SourceBrowser}→{r.TargetBrowser}";
        var sb = new StringBuilder();
        sb.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {direction,-13}  {Verb(r.Kind),-9}  {r.Path}");

        if (r.Before is not null || r.After is not null)
            sb.Append($"  {r.Before ?? "?"} → {r.After ?? "?"}");

        if (!string.IsNullOrEmpty(r.Url))
            sb.Append($"  [{r.Url}]");

        return sb.ToString();
    }

    private static string Verb(ActivityKind kind) => kind switch
    {
        ActivityKind.Add => "ADD",
        ActivityKind.NewFolder => "NEWFOLDER",
        ActivityKind.Delete => "DELETE",
        ActivityKind.Rename => "RENAME",
        ActivityKind.Move => "MOVE",
        ActivityKind.Reorder => "REORDER",
        _ => kind.ToString().ToUpperInvariant(),
    };
}
