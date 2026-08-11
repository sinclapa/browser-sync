using System.Text.Json;
using BrowserSync.Core.Protocol;
using Xunit;

namespace BrowserSync.Core.Tests;

/// <summary>Round-trips every message type through <see cref="ProtocolJson.Options"/> and checks
/// the wire shape (camelCase field names, lowercase-camelCase enum values, "type" discriminator)
/// matches what the extension's `JSON.stringify`/`JSON.parse` on the other end expects.</summary>
public class ProtocolSerializationTests
{
    [Fact]
    public void HelloMessage_RoundTrips_WithCamelCaseDiscriminator()
    {
        var clientId = Guid.NewGuid();
        var original = new HelloMessage { ClientId = clientId, Browser = "chrome", ProtocolVersion = 1, Ts = 12345 };

        var json = JsonSerializer.Serialize<BsMessage>(original, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("hello", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("chrome", doc.RootElement.GetProperty("browser").GetString());

        var roundTripped = Assert.IsType<HelloMessage>(JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options));
        Assert.Equal(clientId, roundTripped.ClientId);
        Assert.Equal("chrome", roundTripped.Browser);
        Assert.Equal(1, roundTripped.ProtocolVersion);
    }

    [Fact]
    public void HelloAckMessage_RoundTrips()
    {
        var original = new HelloAckMessage { ClientId = Guid.NewGuid(), ServerTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), RequestSnapshot = true };

        var json = JsonSerializer.Serialize<BsMessage>(original, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("helloAck", doc.RootElement.GetProperty("type").GetString());

        var roundTripped = Assert.IsType<HelloAckMessage>(JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options));
        Assert.True(roundTripped.RequestSnapshot);
        Assert.Equal(original.ServerTimeUtc, roundTripped.ServerTimeUtc);
    }

    [Fact]
    public void SnapshotRequestMessage_RoundTrips()
    {
        var original = new SnapshotRequestMessage { ClientId = Guid.NewGuid() };

        var json = JsonSerializer.Serialize<BsMessage>(original, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("requestSnapshot", doc.RootElement.GetProperty("type").GetString());

        Assert.IsType<SnapshotRequestMessage>(JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options));
    }

    [Fact]
    public void SnapshotMessage_RoundTrips_WithCamelCaseNodeKind()
    {
        var original = new SnapshotMessage
        {
            ClientId = Guid.NewGuid(),
            GeneratedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Nodes =
            [
                new BookmarkSnapshotNode { NativeId = "1", Kind = SnapshotNodeKind.Folder, Title = "Bookmarks Bar", Index = 0, LastLocalModified = DateTime.UtcNow },
                new BookmarkSnapshotNode { NativeId = "10", ParentNativeId = "1", Kind = SnapshotNodeKind.Bookmark, Title = "Example", Url = "https://example.com", Index = 0, LastLocalModified = DateTime.UtcNow },
            ],
        };

        var json = JsonSerializer.Serialize<BsMessage>(original, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        var nodes = doc.RootElement.GetProperty("nodes");
        Assert.Equal("folder", nodes[0].GetProperty("kind").GetString());
        Assert.Equal("bookmark", nodes[1].GetProperty("kind").GetString());

        var roundTripped = Assert.IsType<SnapshotMessage>(JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options));
        Assert.Equal(2, roundTripped.Nodes.Count);
        Assert.Equal(SnapshotNodeKind.Bookmark, roundTripped.Nodes[1].Kind);
        Assert.Equal("https://example.com", roundTripped.Nodes[1].Url);
    }

    [Fact]
    public void BookmarkEventMessage_RoundTrips_WithCamelCaseOp()
    {
        var original = new BookmarkEventMessage
        {
            ClientId = Guid.NewGuid(),
            Op = BookmarkEventOp.Moved,
            NativeId = "10",
            ParentNativeId = "2",
            Index = 3,
            Timestamp = DateTime.UtcNow,
        };

        var json = JsonSerializer.Serialize<BsMessage>(original, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("event", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("moved", doc.RootElement.GetProperty("op").GetString());

        var roundTripped = Assert.IsType<BookmarkEventMessage>(JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options));
        Assert.Equal(BookmarkEventOp.Moved, roundTripped.Op);
        Assert.Equal("2", roundTripped.ParentNativeId);
        Assert.Equal(3, roundTripped.Index);
    }

    [Fact]
    public void SyncCommandMessage_RoundTrips_WithCamelCaseOpKinds()
    {
        var original = new SyncCommandMessage
        {
            ClientId = Guid.NewGuid(),
            BatchId = Guid.NewGuid(),
            Ops =
            [
                new SyncCommandOp { Op = SyncCommandOpKind.Create, CanonicalId = Guid.NewGuid(), ParentNativeId = "1", Title = "Example", Url = "https://example.com", Index = 0 },
                new SyncCommandOp { Op = SyncCommandOpKind.Remove, CanonicalId = Guid.NewGuid(), NativeId = "10" },
            ],
        };

        var json = JsonSerializer.Serialize<BsMessage>(original, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        var ops = doc.RootElement.GetProperty("ops");
        Assert.Equal("create", ops[0].GetProperty("op").GetString());
        Assert.Equal("remove", ops[1].GetProperty("op").GetString());

        var roundTripped = Assert.IsType<SyncCommandMessage>(JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options));
        Assert.Equal(2, roundTripped.Ops.Count);
        Assert.Equal(SyncCommandOpKind.Create, roundTripped.Ops[0].Op);
        Assert.Equal(SyncCommandOpKind.Remove, roundTripped.Ops[1].Op);
    }

    [Fact]
    public void AckMessage_RoundTrips()
    {
        var canonicalId = Guid.NewGuid();
        var original = new AckMessage { ClientId = Guid.NewGuid(), BatchId = Guid.NewGuid(), Created = [new AckCreatedItem { CanonicalId = canonicalId, NativeId = "123" }] };

        var json = JsonSerializer.Serialize<BsMessage>(original, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ack", doc.RootElement.GetProperty("type").GetString());

        var roundTripped = Assert.IsType<AckMessage>(JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options));
        Assert.Equal(canonicalId, roundTripped.Created[0].CanonicalId);
        Assert.Equal("123", roundTripped.Created[0].NativeId);
    }

    [Fact]
    public void ErrorMessage_RoundTrips()
    {
        var original = new ErrorMessage { ClientId = Guid.NewGuid(), Code = "bad_request", Message = "Something went wrong" };

        var json = JsonSerializer.Serialize<BsMessage>(original, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());

        var roundTripped = Assert.IsType<ErrorMessage>(JsonSerializer.Deserialize<BsMessage>(json, ProtocolJson.Options));
        Assert.Equal("bad_request", roundTripped.Code);
        Assert.Equal("Something went wrong", roundTripped.Message);
    }
}
