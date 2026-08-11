namespace BrowserSync.Core.Sync;

public static class ConflictResolver
{
    public enum Winner
    {
        Canonical,
        Incoming,
    }

    /// <summary>Last-write-wins by timestamp. A tie (or the incoming value being no newer)
    /// favors the existing canonical value — deterministic, not left ambiguous.</summary>
    public static Winner Resolve(DateTime canonicalModifiedUtc, DateTime incomingModifiedUtc) =>
        incomingModifiedUtc > canonicalModifiedUtc ? Winner.Incoming : Winner.Canonical;
}
