namespace Yohuke.LiveRecorder.Transcoding.Contracts;

/// <summary>完成验证后的唯一文件集合与顺序播放分片时长。</summary>
public sealed record TranscodePlaylist
{
    public IReadOnlyList<string> FileNames { get; init; } = [];
    public IReadOnlyList<double> SegmentDurations { get; init; } = [];
}
