# Wire Protocol

Extension ⇄ host over a single WebSocket at `ws://127.0.0.1:8787/ws`. Every message is JSON,
camelCase, sharing an envelope:

```json
{ "type": "...", "v": 1, "clientId": "...", "ts": 1735689600000 }
```

- `type` — discriminator (see below).
- `v` — protocol version (always `1` for now).
- `clientId` — the extension install's persistent GUID (`crypto.randomUUID()`, stored once in
  `chrome.storage.local`).
- `ts` — sender's `Date.now()` / epoch milliseconds when the message was sent (not used for
  conflict resolution — see `timestamp`/`lastLocalModified` below).

## Messages

### `hello` (client → host)
Sent immediately after the socket opens.
```json
{ "type": "hello", "browser": "chrome", "protocolVersion": 1 }
```

### `helloAck` (host → client)
```json
{ "type": "helloAck", "serverTimeUtc": "2026-01-01T00:00:00Z", "requestSnapshot": true }
```
v1 always requests a snapshot right after `hello`.

### `requestSnapshot` (host → client)
No extra fields. Sent once at connect (via `helloAck.requestSnapshot`) and again on every
periodic reconciliation tick, asking the client to send a fresh `snapshot`.

### `snapshot` (client → host)
The client's entire flattened bookmark tree (not nested — flat list, parent references by
native ID), sent on connect and on every periodic reconciliation tick.
```json
{
  "type": "snapshot",
  "generatedAt": "2026-01-01T00:00:00Z",
  "nodes": [
    { "nativeId": "1", "parentNativeId": null, "kind": "folder", "title": "Bookmarks Bar", "index": 0, "lastLocalModified": "1970-01-01T00:00:00Z" },
    { "nativeId": "10", "parentNativeId": "1", "kind": "bookmark", "title": "Example", "url": "https://example.com", "index": 0, "lastLocalModified": "2026-01-01T00:00:00Z" }
  ]
}
```
Root folders (native IDs `1`/`2`/`3` — Bookmarks Bar / Other / Mobile) are included; the host
matches them to its three well-known canonical roots rather than treating them as regular nodes.

`lastLocalModified` is tracked by the extension itself (in `chrome.storage.local`), because
`chrome.bookmarks.BookmarkTreeNode` has no reliable "last modified" field — this is the value
used for last-write-wins comparisons.

### `event` (client → host)
One real-time bookmark change.
```json
{ "type": "event", "op": "created", "nativeId": "10", "parentNativeId": "1", "index": 0, "title": "Example", "url": "https://example.com", "timestamp": "2026-01-01T00:00:00Z" }
```
`op` is one of `created`, `changed`, `moved`, `removed`, `reordered`. `chrome.bookmarks.onChildrenReordered`
doesn't identify a single node, so the extension synthesizes one `reordered` event per affected
child (same shape as `moved`).

### `command` (host → client)
A batch of operations the client must apply via `chrome.bookmarks.*`.
```json
{
  "type": "command",
  "batchId": "b2b1...",
  "ops": [
    { "op": "create", "canonicalId": "c1...", "parentNativeId": "1", "title": "Example", "url": "https://example.com", "index": 0 },
    { "op": "remove", "canonicalId": "c2...", "nativeId": "20" }
  ]
}
```
`op` is one of `create`, `update`, `move`, `remove`.

### `ack` (client → host)
Sent after a `command` batch has been applied, reporting the native IDs assigned to any `create`
ops — this is how the host learns to map its canonical GUID to this client's native ID.
```json
{ "type": "ack", "batchId": "b2b1...", "created": [ { "canonicalId": "c1...", "nativeId": "42" } ] }
```

### `error` (either direction)
```json
{ "type": "error", "code": "bad_request", "message": "..." }
```
