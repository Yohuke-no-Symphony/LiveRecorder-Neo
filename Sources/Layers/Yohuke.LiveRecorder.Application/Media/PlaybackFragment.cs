namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>连续播放清单中一个按关键帧对齐的不可变源范围。</summary>
public sealed class PlaybackFragment
{
    public required int Sequence { get; init; }
    public required double SourceStartMs { get; init; }
    public required double SourceEndMs { get; init; }
    public double DurationMs => SourceEndMs - SourceStartMs;
}
