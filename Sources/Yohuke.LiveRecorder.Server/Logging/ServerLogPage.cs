namespace Yohuke.LiveRecorder.Server.Logging;

internal sealed record ServerLogPage(ServerLogEntry[] Entries, string NextCursor, bool HasMore, string? ResetReason);
