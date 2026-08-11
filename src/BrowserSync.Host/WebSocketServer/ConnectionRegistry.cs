using System.Collections.Concurrent;

namespace BrowserSync.Host.WebSocketServer;

/// <summary>Tracks currently-connected extension clients so canonical changes can be fanned
/// out to every OTHER connected client. Enforces at most one live connection per client ID.</summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ClientConnection> _connections = new();

    public void Add(ClientConnection connection)
    {
        // If a second socket shows up for a clientId that already has one registered (an
        // extension-side reconnect race, or a stray old connection that hasn't noticed it's
        // dead yet), evict the old one instead of letting both run reconciliation
        // concurrently. Two concurrent reconciliation passes for the same client each compute
        // "this canonical item has no mapping yet" independently and both push a `create`
        // command for it — the client then runs chrome.bookmarks.create() twice, producing a
        // real duplicate bookmark neither side ever finds out about (both acks land, but only
        // the first is needed; the second create's native ID is simply never referenced again).
        if (_connections.TryGetValue(connection.ClientId, out var existing) && !ReferenceEquals(existing, connection))
            _ = existing.CloseAsync();

        _connections[connection.ClientId] = connection;
    }

    public void Remove(ClientConnection connection) =>
        // Conditional remove: only evict the registry entry if it still points at THIS
        // connection. Otherwise an old connection's cleanup (running after Add() already
        // replaced it above) would wrongly evict the newer, valid connection.
        _connections.TryRemove(new KeyValuePair<Guid, ClientConnection>(connection.ClientId, connection));

    public IReadOnlyCollection<ClientConnection> All => _connections.Values.ToList();

    public IEnumerable<ClientConnection> Others(Guid excludeClientId) =>
        _connections.Values.Where(c => c.ClientId != excludeClientId);
}
