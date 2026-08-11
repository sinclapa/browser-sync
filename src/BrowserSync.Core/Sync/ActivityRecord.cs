namespace BrowserSync.Core.Sync;

public enum ActivityKind
{
    Add,
    NewFolder,
    Delete,
    Rename,
    Move,
    Reorder,
}

/// <summary>
/// One human-meaningful change, written as a single line so the log can be read start-to-finish
/// and undone by hand. <see cref="Before"/>/<see cref="After"/> deliberately serve every kind
/// that changes something in place — a rename, a move between folders, a reordered folder — so
/// there's one shape to read rather than a field per case.
///
/// Whatever an undo needs must be captured here as it happens; for a deletion in particular
/// there is nothing left to look up afterwards.
/// </summary>
public sealed record ActivityRecord
{
    public required ActivityKind Kind { get; init; }

    /// <summary>Where the change came from — for a deletion, the browser it was deleted from.</summary>
    public required string SourceBrowser { get; init; }

    /// <summary>Where it was propagated to, or null if nothing else was connected.</summary>
    public string? TargetBrowser { get; init; }

    /// <summary>Full path including the item itself, e.g. "Mobile bookmarks/Raspberry PI/Fan SHIM".</summary>
    public required string Path { get; init; }

    public string? Url { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
}

/// <summary>Append-only record of changes, kept apart from diagnostic logging.</summary>
public interface IActivityLog
{
    void Record(ActivityRecord record);
}

/// <summary>Used when no log is wired up (tests, or a caller that doesn't want one).</summary>
public sealed class NullActivityLog : IActivityLog
{
    public static readonly NullActivityLog Instance = new();

    public void Record(ActivityRecord record)
    {
    }
}
