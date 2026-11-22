namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>需要移入回收站的切片标识。</summary>
public sealed record ClipTrashRequest(IReadOnlyList<string>? ClipIds);
