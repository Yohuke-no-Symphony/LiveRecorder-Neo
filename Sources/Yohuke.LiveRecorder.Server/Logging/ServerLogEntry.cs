namespace Yohuke.LiveRecorder.Server.Logging;

internal sealed record ServerLogEntry(long Id, DateTimeOffset TimestampUtc, string Level, string Category, string Message, string? ExceptionType);
