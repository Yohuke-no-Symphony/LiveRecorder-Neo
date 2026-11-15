namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>节点最近日志或指定游标之后的有界增量结果。</summary>
public sealed record TranscodeWorkerLogPage
{
    public IReadOnlyList<TranscodeWorkerLogEntry> Entries { get; init; } = [];
    public long NextCursor { get; init; }
    public bool HasMore { get; init; }
}
