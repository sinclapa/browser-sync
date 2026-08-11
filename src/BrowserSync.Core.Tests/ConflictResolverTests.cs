using BrowserSync.Core.Sync;
using Xunit;

namespace BrowserSync.Core.Tests;

public class ConflictResolverTests
{
    [Fact]
    public void IncomingNewerThanCanonical_IncomingWins()
    {
        var canonical = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var incoming = canonical.AddSeconds(1);

        Assert.Equal(ConflictResolver.Winner.Incoming, ConflictResolver.Resolve(canonical, incoming));
    }

    [Fact]
    public void CanonicalNewerThanIncoming_CanonicalWins()
    {
        var canonical = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        var incoming = canonical.AddSeconds(-1);

        Assert.Equal(ConflictResolver.Winner.Canonical, ConflictResolver.Resolve(canonical, incoming));
    }

    [Fact]
    public void Tie_CanonicalWins()
    {
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(ConflictResolver.Winner.Canonical, ConflictResolver.Resolve(timestamp, timestamp));
    }
}
