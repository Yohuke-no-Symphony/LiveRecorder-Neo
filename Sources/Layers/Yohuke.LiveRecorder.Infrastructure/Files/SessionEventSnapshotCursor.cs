namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed record SessionEventSnapshotCursor(int SchemaVersion, string Version, string ContextHash,
    IReadOnlyList<SessionEventSnapshotPosition> Sources, string? TranslationErrorCode);
