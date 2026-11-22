namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>单条切片的回收结果。</summary>
public sealed record ClipTrashResult(string ClipId, bool Success, string? Error = null);
