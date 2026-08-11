using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BrowserSync.Core.Data.Entities;
using BrowserSync.Core.Protocol;
using BrowserSync.Core.Sync;
using BrowserSync.Host.Duplicates;
using BrowserSync.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrowserSync.Host.WebSocketServer;

/// <summary>One extension's live WebSocket connection. Owns message framing/serialization and
/// serializes concurrent sends (a WebSocket cannot have two writes in flight at once).</summary>
public sealed class ClientConnection(WebSocket socket, ILogger<ClientConnection> logger)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Guid ClientId { get; internal set; }
    public BrowserKind BrowserKind { get; private set; }

    public async Task RunAsync(IServiceScopeFactory scopeFactory, ConnectionRegistry registry, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());
                BsMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Failed to parse message from client {ClientId}: {Json}", ClientId, json);
                    continue;
                }

                if (message is null)
                    continue;

                await using var scope = scopeFactory.CreateAsyncScope();
                await DispatchAsync(message, scope.ServiceProvider, registry, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (WebSocketException ex)
        {
            logger.LogInformation(ex, "WebSocket connection for client {ClientId} closed unexpectedly", ClientId);
        }
    }

    private async Task DispatchAsync(BsMessage message, IServiceProvider services, ConnectionRegistry registry, CancellationToken ct)
    {
        switch (message)
        {
            case HelloMessage hello:
                ClientId = hello.ClientId;
                BrowserKind = string.Equals(hello.Browser, "edge", StringComparison.OrdinalIgnoreCase)
                    ? BrowserKind.Edge
                    : BrowserKind.Chrome;
                registry.Add(this);
                logger.LogInformation("Client {ClientId} ({Browser}) connected", ClientId, BrowserKind);
                await SendAsync(new HelloAckMessage { ClientId = ClientId, ServerTimeUtc = DateTime.UtcNow, RequestSnapshot = true }, ct);
                break;

            case SnapshotMessage snapshot:
                await HandleSnapshotAsync(snapshot, services, registry, ct);
                break;

            case BookmarkEventMessage evt:
                await HandleEventAsync(evt, services, registry, ct);
                break;

            case AckMessage ack:
                await services.GetRequiredService<SyncEngine>().ApplyAckAsync(ClientId, ack);
                break;
        }
    }

    private async Task HandleSnapshotAsync(SnapshotMessage snapshot, IServiceProvider services, ConnectionRegistry registry, CancellationToken ct)
    {
        var engine = services.GetRequiredService<SyncEngine>();
        var result = await engine.ReconcileAsync(ClientId, BrowserKind, snapshot);

        if (result.ForRequester.Ops.Count > 0)
            await SendAsync(result.ForRequester, ct);

        await FanOutAsync(engine, registry, result.ForOthers, ct);

        if (result.SnapshotTooIncompleteForDeletionInference)
        {
            logger.LogWarning(
                "Snapshot from client {ClientId} ({Browser}) was missing an implausible share of its known items — treated as truncated, so nothing was inferred as deleted from it. Adds/changes were still applied.",
                ClientId, BrowserKind);
        }

        RecordStats(services, snapshot);
        await ReportDuplicatesAsync(services, snapshot);
        await ReportLocalDeletionCandidatesAsync(result.LocalDeletionCandidates);
    }

    /// <summary>Records what this browser just reported holding, for the tray's per-browser
    /// counts. Taken from the snapshot rather than canonical state so the two browsers' numbers
    /// can be compared directly.</summary>
    private void RecordStats(IServiceProvider services, SnapshotMessage snapshot)
    {
        var tracked = snapshot.Nodes.Where(n => n.Role is null).ToList();
        services.GetRequiredService<ClientStatsStore>().Set(ClientId, new ClientStats(
            BrowserKind,
            tracked.Count(n => n.Kind == SnapshotNodeKind.Bookmark),
            tracked.Count(n => n.Kind == SnapshotNodeKind.Folder),
            DateTime.UtcNow));
    }

    /// <summary>Logs items this client's snapshot no longer reports. Reporting only — a past
    /// incident where a truncated snapshot made the host infer, and actually carry out, a large
    /// batch of deletions means absence is never acted on. Genuine deletions arrive instead as
    /// explicit `removed` events from the extension's durable queue.</summary>
    private async Task ReportLocalDeletionCandidatesAsync(IReadOnlyList<LocalDeletionCandidate> candidates)
    {
        if (candidates.Count == 0)
            return;

        logger.LogWarning(
            "{Count} item(s) no longer reported by client {ClientId} ({Browser}) — recorded only, nothing deleted",
            candidates.Count, ClientId, BrowserKind);

        var reportPath = Path.Combine(AppPaths.LogsDirectory, $"pending-local-deletions-{BrowserKind}-{ClientId}.json");
        var json = JsonSerializer.Serialize(candidates, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json);
    }

    /// <summary>Scans this client's raw, unfiltered snapshot for exact-duplicate bookmarks
    /// (same parent+title+URL) and records/logs them. This is the only way to find orphan
    /// duplicates that were created outside the normal mapping bookkeeping (see
    /// <see cref="DuplicateBookmarkFinder"/>) — the canonical store has no record of them.
    /// Purely a report: nothing is removed here. Removal happens only when a user explicitly
    /// clicks "Remove detected duplicates" in the tray menu.</summary>
    private async Task ReportDuplicatesAsync(IServiceProvider services, SnapshotMessage snapshot)
    {
        var duplicates = DuplicateBookmarkFinder.FindDuplicates(snapshot.Nodes);
        services.GetRequiredService<DuplicateReportStore>().Set(ClientId, duplicates);

        if (duplicates.Count == 0)
            return;

        var extraCount = duplicates.Sum(g => g.NativeIdsToRemove.Count);
        logger.LogWarning(
            "Found {GroupCount} duplicate bookmark group(s), {ExtraCount} extra cop{Suffix}, for client {ClientId} ({Browser})",
            duplicates.Count, extraCount, extraCount == 1 ? "y" : "ies", ClientId, BrowserKind);

        var reportPath = Path.Combine(AppPaths.LogsDirectory, $"duplicates-{BrowserKind}-{ClientId}.json");
        var json = JsonSerializer.Serialize(duplicates, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json);
    }

    private async Task HandleEventAsync(BookmarkEventMessage evt, IServiceProvider services, ConnectionRegistry registry, CancellationToken ct)
    {
        var engine = services.GetRequiredService<SyncEngine>();
        var result = await engine.ApplyEventAsync(ClientId, evt);

        if (result.CorrectionForSender is not null)
        {
            await SendAsync(
                new SyncCommandMessage { ClientId = ClientId, BatchId = Guid.NewGuid(), Ops = [result.CorrectionForSender] },
                ct);
        }

        await FanOutAsync(engine, registry, result.ForOthers, ct);
    }

    private async Task FanOutAsync(SyncEngine engine, ConnectionRegistry registry, IReadOnlyList<PendingChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0)
            return;

        foreach (var other in registry.Others(ClientId))
        {
            var command = await engine.BuildCommandForClientAsync(other.ClientId, changes);
            if (command.Ops.Count > 0)
                await other.SendAsync(command, ct);
        }
    }

    public async Task SendAsync(BsMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Closes this connection because a newer one replaced it in <see cref="ConnectionRegistry"/>
    /// for the same client ID. Best-effort — the goal is just to make this connection's
    /// receive loop exit so it stops processing messages concurrently with the new one.</summary>
    public async Task CloseAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Replaced by a newer connection for this client", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // best-effort
        }
    }
}
