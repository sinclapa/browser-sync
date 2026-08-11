using System.Net.WebSockets;
using BrowserSync.Host.WebSocketServer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BrowserSync.Host.Tests;

/// <summary>
/// Regression coverage for the duplicate-bookmark bug: two live connections for the same
/// client each independently ran reconciliation and both pushed `create` commands for
/// anything the other's ack hadn't landed for yet, so the client ran chrome.bookmarks.create()
/// twice for the same canonical item. <see cref="ConnectionRegistry"/> now enforces at most
/// one live connection per client ID.
/// </summary>
public class ConnectionRegistryTests
{
    private static ClientConnection NewConnection(Guid clientId)
    {
        var connection = new ClientConnection(new ClientWebSocket(), NullLogger<ClientConnection>.Instance);
        connection.ClientId = clientId;
        return connection;
    }

    [Fact]
    public void Add_SecondConnectionForSameClientId_ReplacesTheFirstInTheRegistry()
    {
        var registry = new ConnectionRegistry();
        var clientId = Guid.NewGuid();
        var first = NewConnection(clientId);
        var second = NewConnection(clientId);

        registry.Add(first);
        registry.Add(second);

        Assert.DoesNotContain(registry.All, c => ReferenceEquals(c, first));
        Assert.Contains(registry.All, c => ReferenceEquals(c, second));
    }

    [Fact]
    public void Remove_OldConnectionCleaningUpLate_DoesNotEvictTheReplacementConnection()
    {
        var registry = new ConnectionRegistry();
        var clientId = Guid.NewGuid();
        var first = NewConnection(clientId);
        var second = NewConnection(clientId);

        registry.Add(first);
        registry.Add(second); // replaces first
        registry.Remove(first); // first's RunAsync loop exiting after the fact

        Assert.Contains(registry.All, c => ReferenceEquals(c, second));
    }

    [Fact]
    public void Others_ExcludesOnlyTheGivenClientId()
    {
        var registry = new ConnectionRegistry();
        var a = NewConnection(Guid.NewGuid());
        var b = NewConnection(Guid.NewGuid());
        registry.Add(a);
        registry.Add(b);

        var others = registry.Others(a.ClientId).ToList();

        Assert.DoesNotContain(others, c => ReferenceEquals(c, a));
        Assert.Contains(others, c => ReferenceEquals(c, b));
    }
}
