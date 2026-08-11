namespace BrowserSync.Core.Sync;

/// <summary>One item a client's reconciliation snapshot no longer reports — inferred as
/// "deleted locally" — awaiting explicit user confirmation before it's actually removed
/// from the canonical store. Never auto-applied: see <see cref="SyncEngine.ConfirmLocalDeletionsAsync"/>.</summary>
public sealed record LocalDeletionCandidate(Guid CanonicalId, string Title, string? Url, string NativeId);
